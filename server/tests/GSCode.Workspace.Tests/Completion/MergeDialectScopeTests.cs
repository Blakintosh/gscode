using System.Collections.Immutable;
using GSCode.Core;
using GSCode.Core.Paths;
using GSCode.Core.Symbols;
using GSCode.Core.Text;
using GSCode.Parser;
using GSCode.Parser.Preprocessing;
using GSCode.Workspace.Api;
using GSCode.Workspace.Completion;
using GSCode.Workspace.Database;
using GSCode.Workspace.Resolution;
using GSCode.Workspace.Tests.Resolution;
using Xunit;

namespace GSCode.Workspace.Tests.Completion;

/// <summary>
/// What a MERGE dialect (CoD4/WaW/MW2/BO1) considers in scope for statement completion: this file,
/// plus the files it <c>#include</c>s. Nothing else.
///
/// The trap is that these games have no <c>#namespace</c>, so <c>SymbolExtractor</c> defaults every
/// function's namespace to the FILE NAME STEM. That default is a resolution fallback and names no
/// scope anybody wrote — and MW2's own tree has two <c>_utility.gsc</c> files, at
/// <c>maps\_utility.gsc</c> and <c>maps\mp\_utility.gsc</c>, with no include between them. Asking
/// the namespace query here therefore offered one file's functions while editing the other, and
/// listed the asking file's own functions TWICE, because the include-scope query already returns
/// them through its same-file arm and neither query can see the other's results.
///
/// Records are built explicitly rather than through <see cref="Workspace.Indexing.WorkspaceIndexer"/>,
/// which parses with <c>GameProfile.Active</c>: under BO3 a keyword-less <c>is_coop()</c> is not a
/// declaration at all, so the store would come back empty and every assertion here would pass for
/// the wrong reason.
/// </summary>
public class MergeDialectScopeTests
{
    private static readonly GameProfile Mw2 = GameProfile.ByName("mw2")!;

    private const string Raw = @"C:\iw4";

    private const string SameStemOtherFile = "is_coop()\n{\n}\n";
    private const string IncludedFile = "exploder_playSound()\n{\n}\n";

    /// <summary>The file under the cursor, with the caret on the blank line inside its last function.</summary>
    private const string EditedFile =
        "#include common_scripts\\utility;\n"
        + "\n"
        + "_playLocalSound( soundAlias )\n"
        + "{\n"
        + "}\n"
        + "\n"
        + "exploder_sound()\n"
        + "{\n"
        + "    \n"
        + "}\n";

    /// <summary>
    /// MW2's shape, reduced: two files sharing the stem <c>_utility</c> and not including one
    /// another, plus one file that IS included.
    /// </summary>
    private static ImmutableArray<CompletionEntry> CompleteInMpUtility()
    {
        // Normalized, because GetScriptRelativePath compares against the normalized root. A raw
        // spelling silently yields an EMPTY relative path, the include match then never fires, and
        // the test passes for the wrong reason on the two assertions that expect nothing.
        string editedPath = PathUtil.NormalizeAbsolute(@$"{Raw}\maps\mp\_utility.gsc");
        (string Path, string Text)[] world =
        [
            (PathUtil.NormalizeAbsolute(@$"{Raw}\maps\_utility.gsc"), SameStemOtherFile),
            (PathUtil.NormalizeAbsolute(@$"{Raw}\common_scripts\utility.gsc"), IncludedFile),
            (editedPath, EditedFile),
        ];

        FakeFileSystem files = new();
        foreach ( (string path, string text) in world )
        {
            files.AddFile(path, text);
        }

        RootConfig config = RootConfig.Create(true, Raw, null, [], files);
        PathResolver resolver = new(config, files);
        ScriptDatabase database = new();
        NameTable names = new();

        foreach ( (string path, string text) in world )
        {
            ParseResult indexed = Analyze(path, text, names);
            ResolutionContext context = resolver.GetContext(path);
            ScriptRecord record = ScriptDatabase.BuildRecord(
                indexed, context, isDirty: false, resolver.GetScriptRelativePath(path, context));
            database.StoreFor(record.Language).Upsert(record);
        }

        string api = Path.Combine(AppContext.BaseDirectory, "Api");
        CompletionEngine engine = new(database, BuiltinApiSet.Load(api, Mw2), ObjectFields.Load(api, Mw2));

        // Line 8 is the blank line inside exploder_sound's body.
        return engine.Complete(
            Analyze(editedPath, EditedFile, names), "raw", new Position(8, 4), profile: Mw2);
    }

    private static ParseResult Analyze(string path, string text, NameTable names)
    {
        return ScriptAnalysis.Analyze(
            path, ScriptLanguage.Gsc, SourceText.From(text), NullInsertProvider.Instance, names, Mw2);
    }

    /// <summary>
    /// How many entries offer this function. A function entry's label carries its parameter list
    /// ("_playLocalSound( soundAlias )"), so the name alone is matched on the opening parenthesis —
    /// which is also what keeps a prefix from matching a longer name.
    /// </summary>
    private static int CountOf(ImmutableArray<CompletionEntry> entries, string name)
    {
        int count = 0;
        foreach ( CompletionEntry entry in entries )
        {
            if ( entry.Kind == CompletionKind.Function
                && (entry.Label == name || entry.Label.StartsWith(name + "(", StringComparison.Ordinal)) )
            {
                count++;
            }
        }

        return count;
    }

    [Fact]
    public void AFunctionDeclaredInThisFile_IsOfferedExactlyOnce()
    {
        // The reported symptom: two identical `_playLocalSound( soundAlias )` rows, one from the
        // file-stem namespace query and one from the include-scope query's same-file arm.
        Assert.Equal(1, CountOf(CompleteInMpUtility(), "_playLocalSound"));
    }

    [Fact]
    public void AFileSharingOnlyItsNameStem_ContributesNothing()
    {
        // maps\_utility.gsc has the same stem and no #include reaching it, so `is_coop` is not in
        // scope here and typing it would not resolve.
        Assert.Equal(0, CountOf(CompleteInMpUtility(), "is_coop"));
    }

    [Fact]
    public void AnIncludedFilesFunctions_AreStillOffered()
    {
        // The other half of the fix mattering: narrowing to the include scope must not narrow it to
        // this file alone. #include MERGES, so an included file's functions are offered and inserted
        // exactly like a local one.
        Assert.Equal(1, CountOf(CompleteInMpUtility(), "exploder_playSound"));
    }
}
