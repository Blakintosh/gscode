using System.Collections.Frozen;
using System.Collections.Immutable;
using GSCode.Core;
using GSCode.Core.Diagnostics;
using GSCode.Core.Symbols;
using GSCode.Core.Text;
using GSCode.Parser;
using GSCode.Parser.Preprocessing;
using GSCode.Workspace.Analysis;
using GSCode.Workspace.Api;
using GSCode.Workspace.Database;
using GSCode.Workspace.Resolution;
using Xunit;

namespace GSCode.Workspace.Tests.Analysis;

/// <summary>
/// The precedence half of the arity rule: which DECLARATION a call is judged against when a script
/// function and an engine builtin share a name.
///
/// Builtins are the fallback after the current namespace — <c>sys::</c> exists as an explicit alias
/// precisely because a script function otherwise wins — and this lint had it backwards, judging every
/// unqualified call against the library whatever the script declared.
/// </summary>
public class ArgumentCountLintTests
{
    private static readonly string AskingPath = TestPaths.Raw(@"scripts\zm\_zm.gsc");

    /// <summary>
    /// A stand-in for BO3's <c>SpawnSpectator( origin, angles )</c>, both parameters mandatory —
    /// the entry that reported the shipped <c>_zm.gsc</c> call as missing two arguments.
    /// </summary>
    private static BuiltinApi Builtins()
    {
        BuiltinParameter origin = new("origin", "", true, "vector");
        BuiltinParameter angles = new("angles", "", true, "vector");
        BuiltinOverload overload = new("player", [origin, angles], "", false);
        BuiltinFunction spawnSpectator = new("SpawnSpectator", "", [overload], "");

        return new BuiltinApi(
            new Dictionary<string, BuiltinFunction> { ["SpawnSpectator"] = spawnSpectator }
                .ToFrozenDictionary(StringComparer.OrdinalIgnoreCase));
    }

    private static ImmutableArray<Diagnostic> Lint(string askingSource, string? otherFile = null)
    {
        FakeFileSystem files = new();
        RootConfig config = TestPaths.Config(files);
        PathResolver resolver = new(config, files);
        ScriptDatabase database = new();

        // A second file in the SAME namespace, for the case where the shadowing declaration is not in
        // the calling file. #using cannot bring an unqualified name into reach on a namespace dialect,
        // so the namespace is the only way this happens.
        if ( otherFile is not null )
        {
            string otherPath = TestPaths.Raw(@"scripts\zm\_zm_utility.gsc");
            ParseResult other = ScriptAnalysis.Analyze(
                otherPath, ScriptLanguage.Gsc, SourceText.From(otherFile), NullInsertProvider.Instance, new NameTable());

            database.Commit(other, ResolutionContext.RawContext, isDirty: false, @"scripts\zm\_zm_utility.gsc");
        }

        ParseResult result = ScriptAnalysis.Analyze(
            AskingPath, ScriptLanguage.Gsc, SourceText.From(askingSource), NullInsertProvider.Instance, new NameTable());

        // The asking file is indexed too, as it is in a live workspace. It matters: the script half of
        // this rule reads declarations from the STORE, so an un-indexed asking file has no arity to
        // judge against and the rule stands down — silently passing a test that meant to exercise it.
        database.Commit(result, ResolutionContext.RawContext, isDirty: false, @"scripts\zm\_zm.gsc");

        return ArgumentCountLint.Analyze(result, database.Gsc, "raw", AskingPath, Builtins());
    }

    [Fact]
    public void AFunctionTheFileDeclaresItselfBeatsTheBuiltinOfTheSameName()
    {
        // scripts\zm\_zm.gsc declares `function spawnSpectator()` taking nothing and calls it 2,138
        // lines later. Judged against BO3's SpawnSpectator( origin, angles ) it read as missing two
        // arguments — an Error on a file that ships and works.
        string source = "#namespace zm;\n"
            + "function spawnSpectator()\n{\n}\n"
            + "function respawn()\n{\n    self thread spawnSpectator();\n}\n";

        Assert.Empty(Lint(source));
    }

    [Fact]
    public void ADeclarationElsewhereInTheSameNamespaceAlsoBeatsIt()
    {
        // The namespace is the unit, not the file: an unqualified call reaches everything declared
        // into the namespace this file declares into, and only falls through to the engine after.
        string source = "#namespace zm;\nfunction respawn()\n{\n    self thread spawnSpectator();\n}\n";
        string other = "#namespace zm;\nfunction spawnSpectator()\n{\n}\n";

        Assert.Empty(Lint(source, other));
    }

    [Fact]
    public void ADeclarationSPELLEDDifferentlyDoesNotShadowTheBuiltin()
    {
        // scripts\shared\exploder_shared.gsc declares `function earthquake()` taking nothing and nine
        // lines later calls `Earthquake( magnitude, duration, origin, radius )` — the ENGINE one. It
        // ships and works, so the spelling is what tells the two apart; treating the declaration as
        // shadowing reported four arguments passed to a nought-parameter function.
        string source = "#namespace zm;\n"
            + "function spawnspectator()\n{\n}\n"
            + "function respawn()\n{\n    self SpawnSpectator( 1, 2 );\n}\n";

        Assert.Empty(Lint(source));
    }

    [Fact]
    public void TheBuiltinStillJudgesACallNoScriptFunctionExplains()
    {
        // The control. Without it this rule could be "fixed" by never reporting anything, and the
        // lower bound on a genuine engine call is the whole point of 5023.
        string source = "#namespace zm;\nfunction respawn()\n{\n    self thread spawnSpectator();\n}\n";

        Diagnostic reported = Assert.Single(Lint(source));

        Assert.Equal(GscDiagnosticCode.WrongBuiltinArgumentCount, reported.Code);
        Assert.Contains("at least 2", reported.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AnExplicitSysCallIsJudgedAgainstTheBuiltin()
    {
        // `sys::` names the engine function outright, so it owes the builtin's mandatory arguments
        // exactly as the bare call above does. Extraction already reads it that way — it keys
        // `sys::name` as the namespace-less builtin key — but this rule took `sys` for a script
        // namespace, found nothing declared in it, and stood down.
        string source = "#namespace zm;\nfunction respawn()\n{\n    self thread sys::spawnSpectator();\n}\n";

        Diagnostic reported = Assert.Single(Lint(source));

        Assert.Equal(GscDiagnosticCode.WrongBuiltinArgumentCount, reported.Code);
        Assert.Contains("at least 2", reported.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AnExplicitSysCallReachesPastAShadowingScriptFunction()
    {
        // The reason `sys::` exists: the file's own spawnSpectator() wins a bare call, and the
        // qualifier is how a script asks for the engine's instead. Judging it against the script
        // declaration — or not at all — ignores the one thing the author wrote to say which.
        string source = "#namespace zm;\n"
            + "function spawnSpectator()\n{\n}\n"
            + "function respawn()\n{\n    self thread sys::spawnSpectator();\n}\n";

        Diagnostic reported = Assert.Single(Lint(source));

        Assert.Equal(GscDiagnosticCode.WrongBuiltinArgumentCount, reported.Code);
    }

    [Fact]
    public void TooManyArgumentsForTheShadowingScriptFunctionIsStillReported()
    {
        // Shadowing moves which declaration is authoritative; it does not switch the rule off. The
        // script side's bound is the upper one — fewer arguments than declared is legal in GSC.
        string source = "#namespace zm;\n"
            + "function spawnSpectator()\n{\n}\n"
            + "function respawn()\n{\n    self thread spawnSpectator( 1, 2 );\n}\n";

        Diagnostic reported = Assert.Single(Lint(source));

        Assert.Equal(GscDiagnosticCode.TooManyArguments, reported.Code);
    }

    // --- CameFromMacro only checked an IdentifierNode callee and IdentifierNode arguments ---

    [Fact]
    public void AMacroExpandingToAQualifiedCall_StandsDownTheRule()
    {
        // `util::helper` lexes as a QualifiedNode, not an IdentifierNode — CameFromMacro's callee
        // check only matched the latter, so a macro whose body calls a qualified name was never
        // recognised as the source of the call at all.
        string source = "#namespace zm;\n#define CALL_HELPER() util::helper( 1, 2, 3 )\n"
            + "function respawn()\n{\n    CALL_HELPER();\n}\n";
        string other = "#namespace util;\nfunction helper( a )\n{\n}\n";

        Assert.Empty(Lint(source, other));
    }

    [Fact]
    public void AMacroExpandingToALiteralArgumentList_StandsDownTheRule()
    {
        // An object-like macro's body substitutes verbatim wherever it is written, so
        // `helper( DEFAULTS )` becomes `helper( 1, 2 )` after expansion — two LITERAL arguments the
        // author never wrote, neither of them an IdentifierNode, so CameFromMacro's argument check
        // never saw them either.
        string source = "#namespace zm;\n#define DEFAULTS 1, 2\n"
            + "function helper( a )\n{\n}\n"
            + "function respawn()\n{\n    helper( DEFAULTS );\n}\n";

        Assert.Empty(Lint(source));
    }

    [Fact]
    public void ANonSelfArrowCall_StandsDownWhenATopLevelFunctionSharesTheName()
    {
        // FunctionResolutionLint documents the real shape: `[[self.classObj]]->onBeginUse( player )`
        // calls a top-level function that other files assign to that field with `&onBeginUse` — the
        // arrow form dispatches through a function-POINTER field as much as through a class. Here
        // exactly one class ALSO declares a method of that name, so Canonicalize resolved to IT
        // alone and the call was judged against a 1-parameter method that might not be the target
        // the pointer actually reaches.
        string source = "#namespace zm;\n"
            + "class cFoo\n{\n"
            + "    function onBeginUse( player )\n    {\n    }\n"
            + "}\n"
            + "function onBeginUse( player, extra )\n{\n}\n"
            + "function respawn()\n{\n    [[ level.classObj ]]->onBeginUse( player, extra );\n}\n";

        Assert.Empty(Lint(source));
    }

    [Fact]
    public void ANonSelfArrowCall_IsStillJudgedWhenNoTopLevelFunctionSharesTheName()
    {
        // The control: without a same-named top-level function, exactly one class declaring the
        // name is still the unambiguous case the rule exists to judge.
        string source = "#namespace zm;\n"
            + "class cFoo\n{\n"
            + "    function onBeginUse( player )\n    {\n    }\n"
            + "}\n"
            + "function respawn()\n{\n    [[ level.classObj ]]->onBeginUse( player, extra );\n}\n";

        Diagnostic reported = Assert.Single(Lint(source));

        Assert.Equal(GscDiagnosticCode.TooManyArguments, reported.Code);
    }
}
