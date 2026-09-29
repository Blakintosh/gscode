using System.Collections.Immutable;
using GSCode.Core;
using GSCode.Core.Text;
using GSCode.Workspace.Api;
using GSCode.Workspace.Completion;
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
/// Indexed through <see cref="TestWorkspace"/>, which pins the dialect. Indexing under the wrong one
/// leaves the store empty rather than failing, and these assertions would then pass without proving
/// anything.
/// </summary>
public class MergeDialectScopeTests
{
    private static readonly GameProfile s_mw2 = GameProfile.ByName("mw2")!;

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
        using TestWorkspace workspace = TestWorkspace.Build(
            [
                new TestFile(@"maps\_utility.gsc", SameStemOtherFile),
                new TestFile(@"common_scripts\utility.gsc", IncludedFile),
                new TestFile(@"maps\mp\_utility.gsc", EditedFile),
            ],
            s_mw2);

        // Line 8 is the blank line inside exploder_sound's body.
        return EngineOver(workspace).Complete(
            workspace.Analyze(@"maps\mp\_utility.gsc"), "raw", new Position(8, 4), profile: s_mw2);
    }

    private static CompletionEngine EngineOver(TestWorkspace workspace)
    {
        string api = Path.Combine(AppContext.BaseDirectory, "Api");
        return new CompletionEngine(workspace.Database, BuiltinApiSet.Load(api, s_mw2), ObjectFields.Load(api, s_mw2));
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

    /// <summary>
    /// MW2's own shape: `maps\_utility.gsc` and `maps\mp\_utility.gsc` share a stem and no
    /// #include reaches between them, but a THIRD file can still name either one directly by its
    /// full inline path — `maps\_utility::is_coop()` — with no import at all.
    /// </summary>
    private static ImmutableArray<CompletionEntry> CompleteAfterInlinePathQualifier()
    {
        const string editedFile = "run()\n{\n    maps\\_utility::\n}\n";

        using TestWorkspace workspace = TestWorkspace.Build(
            [
                new TestFile(@"maps\_utility.gsc", SameStemOtherFile),
                new TestFile(@"maps\mp\_utility.gsc", IncludedFile),
                new TestFile(@"maps\mp\gametypes\_globallogic.gsc", editedFile),
            ],
            s_mw2);

        // Line 2, right after "maps\_utility::".
        return EngineOver(workspace).Complete(
            workspace.Analyze(@"maps\mp\gametypes\_globallogic.gsc"), "raw", new Position(2, 19), profile: s_mw2);
    }

    [Fact]
    public void AnInlinePathQualifier_OffersTheFileItNames()
    {
        Assert.Equal(1, CountOf(CompleteAfterInlinePathQualifier(), "is_coop"));
    }

    [Fact]
    public void AnInlinePathQualifier_DoesNotReachTheOtherFileSharingItsStem()
    {
        // The reported false positive, confirmed: `ns::` reads its qualifier as a single
        // identifier token — the LAST segment of the path — and asks for functions by that bare
        // stem. Since these dialects have no #namespace, SymbolExtractor defaults every
        // function's namespace to its own file's name stem, so `maps\_utility::` offered
        // `exploder_playSound` from the unrelated `maps\mp\_utility.gsc` as readily as `is_coop`
        // from the file actually named.
        Assert.Equal(0, CountOf(CompleteAfterInlinePathQualifier(), "exploder_playSound"));
    }
}
