using GSCode.Server.Configuration;
using GSCode.Server.Handlers;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using Xunit;

namespace GSCode.Server.Tests.Handlers;

/// <summary>
/// Completion offering a function from a script this file has not imported, with the directive
/// attached.
///
/// Driven through the handler rather than the engine, because the half that makes the feature usable
/// is the half the engine deliberately does not own: the engine names a script path, and the handler
/// turns it into the `additionalTextEdits` that actually writes the directive. A candidate produced
/// with no edit behind it is a suggestion that inserts a call which does not compile.
/// </summary>
public sealed class AutoImportCompletionTests
{
    private const string LibSource = "#namespace lib;\nfunction get_players()\n{\n}\n";

    private static string CallerSource(string typed, bool importsLib)
    {
        return (importsLib ? "#using scripts\\lib;\n" : "")
            + "#namespace caller;\nfunction run()\n{\n    " + typed + "\n}\n";
    }

    /// <summary>The completion list for a caller whose last line is <paramref name="typed"/>.</summary>
    private static async Task<CompletionList> CompleteAfterAsync(string typed, bool importsLib = false, bool autoImport = true)
    {
        using HandlerWorkspace workspace = await HandlerWorkspace.BuildAsync(
        [
            new TestFile(@"scripts\lib.gsc", LibSource),
            new TestFile(@"scripts\caller.gsc", CallerSource(typed, importsLib)),
        ]);
        workspace.Open(@"scripts\caller.gsc");

        ServerSettings settings = new() { CompletionAutoImport = autoImport };
        CompletionHandler handler = new(
            workspace.Navigation,
            workspace.Completion,
            settings,
            HandlerWorkspace.Selector,
            workspace.Builtins);

        // The caret sits at the end of the typed word, which is the line after the brace.
        int line = importsLib ? 4 : 3;
        CompletionParams request = new()
        {
            TextDocument = HandlerWorkspace.Identify(@"scripts\caller.gsc"),
            Position = new Position(line, 4 + typed.Length),
        };

        return await handler.Handle(request, CancellationToken.None);
    }

    /// <summary>
    /// The row for a function, found by what it INSERTS rather than by its label. A label carries
    /// the parameter hint appended (this client negotiates no labelDetails support, so
    /// <c>SplitLabel</c> folds it in) and, for a function reached through an import, the namespace
    /// qualifier too — neither of which the test is about.
    /// </summary>
    private static CompletionItem? ItemInserting(CompletionList list, string insertPrefix)
    {
        return list.Items.FirstOrDefault(
            item => item.InsertText is not null && item.InsertText.StartsWith(insertPrefix, StringComparison.Ordinal));
    }

    [Fact]
    public async Task AFunctionFromAnUnimportedScriptIsOfferedWithItsUsing()
    {
        CompletionList list = await CompleteAfterAsync("get_pl");

        // Qualified on a namespace dialect: the #using alone does not make an unqualified call
        // resolve.
        CompletionItem? item = ItemInserting(list, "lib::get_players");
        Assert.NotNull(item);

        TextEdit edit = Assert.Single(item!.AdditionalTextEdits!);
        Assert.Equal("#using scripts\\lib;\n", edit.NewText);

        // Above the namespace line, which is where a file with no imports yet takes its first.
        Assert.Equal(0, edit.Range.Start.Line);
        Assert.Equal(0, edit.Range.Start.Character);

        // The candidates are prefix-matched and capped, so this page is only true for this prefix.
        Assert.True(list.IsIncomplete);
    }

    [Fact]
    public async Task AFunctionAlreadyInScopeCarriesNoImport()
    {
        CompletionList list = await CompleteAfterAsync("get_pl", importsLib: true);

        CompletionItem? item = ItemInserting(list, "lib::get_players");
        Assert.NotNull(item);
        Assert.True(item!.AdditionalTextEdits is null || item.AdditionalTextEdits.Count() == 0);
    }

    [Fact]
    public async Task NothingIsOfferedBeforeThreeCharacters()
    {
        CompletionList list = await CompleteAfterAsync("ge");

        Assert.Null(ItemInserting(list, "lib::get_players"));
        Assert.False(list.IsIncomplete);
    }

    [Fact]
    public async Task TheSettingTurnsItOff()
    {
        CompletionList list = await CompleteAfterAsync("get_pl", autoImport: false);

        Assert.Null(ItemInserting(list, "lib::get_players"));
    }
}
