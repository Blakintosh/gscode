using GSCode.Server.Handlers;
using GSCode.Workspace.Completion;
using Xunit;

namespace GSCode.Server.Tests.Handlers;

/// <summary>
/// What breaks a tie between two rows the editor scored equally.
///
/// Statement scope is a median of 1,168 entries and up to 5,059, of which the great majority are
/// engine builtins. With no <c>sortText</c> the tie-break was the label alone, so a prefix of the
/// variable two lines up could put an engine function above it on alphabetical order — the reported
/// symptom, where typing <c>str_</c> surfaced <c>start_timed_vortex</c>.
///
/// The tier is by DISTANCE from the cursor. These tests assert the ORDER between tiers rather than
/// the digits themselves, which are an implementation detail.
/// </summary>
public class CompletionSortTextTests
{
    private static CompletionEntry Entry(string label, CompletionKind kind, bool isBuiltin = false)
    {
        return new CompletionEntry(label, kind, IsBuiltin: isBuiltin);
    }

    private static string Sort(string label, CompletionKind kind, bool isBuiltin = false)
    {
        return CompletionHandler.SortText(Entry(label, kind, isBuiltin));
    }

    [Fact]
    public void ALocalOutranksAnEngineBuiltin_EvenWhenTheBuiltinSortsFirstAlphabetically()
    {
        string local = Sort("str_pool_name", CompletionKind.Variable);
        string builtin = Sort("start_timed_vortex", CompletionKind.Function, isBuiltin: true);

        Assert.True(string.CompareOrdinal(local, builtin) < 0);
    }

    [Fact]
    public void AScriptFunctionOutranksABuiltinOfTheSameName()
    {
        // BO3 ships an engine SpawnSpectator and three scripts declaring one. Both rows are offered;
        // the one written in the workspace is the nearer of the two.
        string script = Sort("spawnSpectator", CompletionKind.Function);
        string builtin = Sort("SpawnSpectator", CompletionKind.Function, isBuiltin: true);

        Assert.True(string.CompareOrdinal(script, builtin) < 0);
    }

    [Fact]
    public void TheTiersRunNearestFirst()
    {
        string local = Sort("a", CompletionKind.Variable);
        string member = Sort("a", CompletionKind.Field);
        string function = Sort("a", CompletionKind.Function);
        string macro = Sort("a", CompletionKind.Macro);
        string keyword = Sort("a", CompletionKind.Keyword);
        string builtin = Sort("a", CompletionKind.Function, isBuiltin: true);

        // A parameter or local and a class member are both bound where the cursor is, so they share
        // the nearest tier.
        Assert.Equal(local, member);

        Assert.True(string.CompareOrdinal(local, function) < 0);
        Assert.Equal(function, macro);
        Assert.True(string.CompareOrdinal(function, keyword) < 0);
        Assert.True(string.CompareOrdinal(keyword, builtin) < 0);
    }

    /// <summary>
    /// Within a tier the ordering has to stay alphabetical, which is what carrying the label in the
    /// sort key is for — a digit alone would leave every row in a tier equal and hand the ordering
    /// back to whatever the client does with ties.
    /// </summary>
    [Fact]
    public void WithinATierTheNameStillDecides()
    {
        Assert.True(string.CompareOrdinal(
            Sort("a_players", CompletionKind.Variable), Sort("n_count", CompletionKind.Variable)) < 0);
    }

    /// <summary>
    /// Case must not decide anything: GSC names are case-insensitive, and an ordinal comparison puts
    /// every capitalised name above every lowercase one — which would sort the engine API, written
    /// in mixed case, apart from the scripts that call it.
    /// </summary>
    [Fact]
    public void CaseDoesNotDecide()
    {
        Assert.True(string.CompareOrdinal(
            Sort("Alpha", CompletionKind.Function), Sort("beta", CompletionKind.Function)) < 0);
    }
}
