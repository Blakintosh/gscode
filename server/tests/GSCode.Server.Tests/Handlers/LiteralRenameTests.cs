using GSCode.Server.Handlers;
using OmniSharp.Extensions.LanguageServer.Protocol;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using Xunit;
using LspPosition = OmniSharp.Extensions.LanguageServer.Protocol.Models.Position;

namespace GSCode.Server.Tests.Handlers;

/// <summary>
/// Renaming a literal the scripts coin — a notify string, a hash, an anim reference — driven
/// through PrepareRenameHandler and RenameHandler the way the editor drives them.
///
/// The reference range of a literal is its whole token, quotes included. Prepare offered that, so
/// the box held <c>"spawned"</c>; renaming to <c>"ready"</c> was refused (a quote is not legal
/// content) and renaming to <c>ready</c> wrote it over the token, turning
/// <c>notify( "spawned" )</c> into <c>notify( ready )</c> in every file.
/// </summary>
public class LiteralRenameTests
{
    private const string NotifierPath = @"scripts\shared\notifier.gsc";
    private const string WaiterPath = @"scripts\shared\waiter.gsc";

    private const string NotifierSource =
        "function f()\n{\n    self notify( \"spawned\" );\n    x = #\"spawned_hash\";\n    y = %run_anim;\n}\n";

    private const string WaiterSource =
        "function g()\n{\n    self waittill( \"spawned\" );\n    x = #\"spawned_hash\";\n    y = % run_anim;\n}\n";

    private static LspPosition NotifyString => new(2, 20);
    private static LspPosition HashString => new(3, 12);
    private static LspPosition AnimReference => new(4, 12);

    private static async Task<(RangeOrPlaceholderRange? Prepared, Dictionary<string, string> Renamed)> RenameAsync(
        LspPosition position, string newName)
    {
        using HandlerWorkspace workspace = await HandlerWorkspace.BuildAsync(
        [
            new TestFile(NotifierPath, NotifierSource),
            new TestFile(WaiterPath, WaiterSource),
        ]);
        workspace.Open(NotifierPath);

        PrepareRenameHandler prepare = new(
            workspace.Navigation, workspace.Builtins, workspace.ObjectFields, HandlerWorkspace.Selector);
        RenameHandler rename = new(
            workspace.Navigation, workspace.Builtins, workspace.ObjectFields, HandlerWorkspace.Selector);

        RangeOrPlaceholderRange? prepared = await prepare.Handle(
            new PrepareRenameParams { TextDocument = HandlerWorkspace.Identify(NotifierPath), Position = position },
            CancellationToken.None);

        WorkspaceEdit? edit = await rename.Handle(
            new RenameParams { TextDocument = HandlerWorkspace.Identify(NotifierPath), Position = position, NewName = newName },
            CancellationToken.None);

        Dictionary<string, string> renamed = new()
        {
            [NotifierPath] = NotifierSource,
            [WaiterPath] = WaiterSource,
        };

        if ( edit?.Changes is not null )
        {
            foreach ( KeyValuePair<DocumentUri, IEnumerable<TextEdit>> change in edit.Changes )
            {
                string key = renamed.Keys.Single(path => change.Key.GetFileSystemPath().EndsWith(path, StringComparison.OrdinalIgnoreCase));
                renamed[key] = Apply(renamed[key], change.Value);
            }
        }

        return (prepared, renamed);
    }

    private static string Apply(string text, IEnumerable<TextEdit> edits)
    {
        string[] lines = text.Split('\n');
        foreach ( TextEdit edit in edits.OrderByDescending(e => e.Range.Start.Line).ThenByDescending(e => e.Range.Start.Character) )
        {
            Assert.Equal(edit.Range.Start.Line, edit.Range.End.Line);
            string line = lines[edit.Range.Start.Line];
            lines[edit.Range.Start.Line] = line[..edit.Range.Start.Character] + edit.NewText + line[edit.Range.End.Character..];
        }

        return string.Join('\n', lines);
    }

    private static string PreparedText(RangeOrPlaceholderRange? prepared, string source)
    {
        Assert.NotNull(prepared);
        Assert.True(prepared.IsRange);
        OmniSharp.Extensions.LanguageServer.Protocol.Models.Range range = prepared.Range!;
        string line = source.Split('\n')[range.Start.Line];

        return line[range.Start.Character..range.End.Character];
    }

    [Fact]
    public async Task ANotifyStringKeepsItsQuotesInEveryFile()
    {
        (RangeOrPlaceholderRange? prepared, Dictionary<string, string> renamed) = await RenameAsync(NotifyString, "ready");

        Assert.Equal("spawned", PreparedText(prepared, NotifierSource));
        Assert.Contains("self notify( \"ready\" );", renamed[NotifierPath], StringComparison.Ordinal);
        Assert.Contains("self waittill( \"ready\" );", renamed[WaiterPath], StringComparison.Ordinal);
    }

    [Fact]
    public async Task AHashStringKeepsItsPrefixAndQuotes()
    {
        (RangeOrPlaceholderRange? prepared, Dictionary<string, string> renamed) = await RenameAsync(HashString, "ready_hash");

        Assert.Equal("spawned_hash", PreparedText(prepared, NotifierSource));
        Assert.Contains("x = #\"ready_hash\";", renamed[NotifierPath], StringComparison.Ordinal);
        Assert.Contains("x = #\"ready_hash\";", renamed[WaiterPath], StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnAnimReferenceKeepsItsSigil()
    {
        (RangeOrPlaceholderRange? prepared, Dictionary<string, string> renamed) = await RenameAsync(AnimReference, "walk_anim");

        Assert.Equal("run_anim", PreparedText(prepared, NotifierSource));
        Assert.Contains("y = %walk_anim;", renamed[NotifierPath], StringComparison.Ordinal);
        Assert.Contains("y = % walk_anim;", renamed[WaiterPath], StringComparison.Ordinal);
    }

    [Fact]
    public async Task ANameWithAQuoteIsStillRefused()
    {
        (_, Dictionary<string, string> renamed) = await RenameAsync(NotifyString, "\"ready\"");

        Assert.Equal(NotifierSource, renamed[NotifierPath]);
        Assert.Equal(WaiterSource, renamed[WaiterPath]);
    }
}
