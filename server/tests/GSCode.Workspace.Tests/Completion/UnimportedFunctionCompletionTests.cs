using System.Collections.Immutable;
using GSCode.Core;
using GSCode.Core.Symbols;
using GSCode.Core.Text;
using GSCode.Parser;
using GSCode.Parser.Preprocessing;
using GSCode.Workspace.Api;
using GSCode.Workspace.Completion;
using GSCode.Workspace.Tests.Resolution;
using Xunit;

namespace GSCode.Workspace.Tests.Completion;

/// <summary>
/// Which functions the auto-import producer offers, and how each dialect family spells the result.
///
/// The candidate set is the same question in both — a file this one does not link against — but the
/// answer is written two ways, and getting either wrong produces a suggestion that inserts code
/// which does not compile. A namespace dialect must insert the QUALIFIED call, because a `#using`
/// does not make an unqualified call into another namespace resolve; a merge dialect must insert
/// the bare name, because `#include` folds the function into local scope and a qualifier there
/// names a file rather than a namespace.
/// </summary>
public class UnimportedFunctionCompletionTests
{
    private static readonly GameProfile Bo3 = GameProfile.ByName("bo3")!;
    private static readonly GameProfile Mw2 = GameProfile.ByName("mw2")!;

    private static ImmutableArray<CompletionEntry> CompleteIn(
        GameProfile profile, string rawRoot, string editedPath, string editedText, Position position,
        params (string Path, string Text)[] otherFiles)
    {
        (string Path, string Text)[] files = [.. otherFiles, (editedPath, editedText)];
        TestWorkspace.Built workspace = TestWorkspace.Build(profile, rawRoot, files);

        string api = Path.Combine(AppContext.BaseDirectory, "Api");
        CompletionEngine engine = new(
            workspace.Database, BuiltinApiSet.Load(api, profile), ObjectFields.Load(api, profile));

        ParseResult result = ScriptAnalysis.Analyze(
            editedPath, ScriptLanguage.Gsc, SourceText.From(editedText), NullInsertProvider.Instance,
            new NameTable(), profile);

        return engine.Complete(result, "raw", position, profile: profile);
    }

    private static CompletionEntry? EntryInserting(ImmutableArray<CompletionEntry> entries, string insertPrefix)
    {
        foreach ( CompletionEntry entry in entries )
        {
            if ( entry.InsertText.StartsWith(insertPrefix, StringComparison.Ordinal) )
            {
                return entry;
            }
        }

        return null;
    }

    [Fact]
    public void ANamespaceDialectOffersTheQualifiedCallAndTheUsingPath()
    {
        const string raw = @"C:\bo3";
        const string edited = "#namespace caller;\nfunction run()\n{\n    get_pl\n}\n";

        ImmutableArray<CompletionEntry> entries = CompleteIn(
            Bo3,
            raw,
            @$"{raw}\scripts\caller.gsc",
            edited,
            new Position(3, 10),
            (@$"{raw}\scripts\lib.gsc", "#namespace lib;\nfunction get_players()\n{\n}\n"));

        CompletionEntry? entry = EntryInserting(entries, "lib::get_players");
        Assert.NotNull(entry);
        Assert.Equal("scripts\\lib", entry!.ImportPath);
    }

    [Fact]
    public void AMergeDialectOffersTheBareCallAndTheIncludePath()
    {
        const string raw = @"C:\iw4";
        const string edited = "run()\n{\n    exploder_pl\n}\n";

        ImmutableArray<CompletionEntry> entries = CompleteIn(
            Mw2,
            raw,
            @$"{raw}\maps\mp\caller.gsc",
            edited,
            new Position(2, 15),
            (@$"{raw}\common_scripts\utility.gsc", "exploder_playSound()\n{\n}\n"));

        CompletionEntry? entry = EntryInserting(entries, "exploder_playSound");
        Assert.NotNull(entry);
        Assert.Equal("common_scripts\\utility", entry!.ImportPath);
    }

    [Fact]
    public void AFileAlreadyLinkedAgainstIsNotOfferedAsAnImport()
    {
        const string raw = @"C:\bo3";
        const string edited = "#using scripts\\lib;\n#namespace caller;\nfunction run()\n{\n    get_pl\n}\n";

        ImmutableArray<CompletionEntry> entries = CompleteIn(
            Bo3,
            raw,
            @$"{raw}\scripts\caller.gsc",
            edited,
            new Position(4, 10),
            (@$"{raw}\scripts\lib.gsc", "#namespace lib;\nfunction get_players()\n{\n}\n"));

        // Offered — through the ordinary imported-namespace arm — but with nothing to import.
        CompletionEntry? entry = EntryInserting(entries, "lib::get_players");
        Assert.NotNull(entry);
        Assert.Equal("", entry!.ImportPath);
    }

    [Fact]
    public void APrivateFunctionIsNeverOffered()
    {
        const string raw = @"C:\bo3";
        const string edited = "#namespace caller;\nfunction run()\n{\n    hid\n}\n";

        ImmutableArray<CompletionEntry> entries = CompleteIn(
            Bo3,
            raw,
            @$"{raw}\scripts\caller.gsc",
            edited,
            new Position(3, 7),
            (@$"{raw}\scripts\lib.gsc", "#namespace lib;\nfunction private hidden_helper()\n{\n}\n"));

        // Privacy is per namespace, and a file that has not imported the script is not in it by any
        // route that would make the call legal — so offering the import would offer an error.
        Assert.Null(EntryInserting(entries, "lib::hidden_helper"));
    }
}
