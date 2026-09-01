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
using GSCode.Workspace.Tests.Resolution;
using Xunit;

namespace GSCode.Workspace.Tests.Analysis;

/// <summary>
/// The #include counterpart to <see cref="NamespaceUsageLintTests"/>: a call that resolves to a
/// function in a file this one never included.
///
/// Built the way <see cref="UnusedIncludeLintTests"/> is — the workspace is committed directly
/// rather than indexed, because the default indexer runs as BO3 and would not parse a bare CoD4
/// function declaration.
/// </summary>
public class IncludeUsageLintTests
{
    private const string Raw = @"C:\bo3\share\raw";
    private static readonly GameProfile Cod4 = GameProfile.ByName("cod4")!;

    private const string UtilitySource = "scriptPrintln( channel, msg )\n{\n}\n";

    /// <summary>An unrelated file the asking script DOES include, so a pass is never suppressed.</summary>
    private const string LoadSource = "main()\n{\n}\n";

    /// <summary>A file that includes utility itself — the second hop of the transitive walk.</summary>
    private const string ChainSource = "#include common_scripts\\utility;\n\nchain_helper()\n{\n}\n";

    /// <summary>
    /// One engine name, which is all the gate needs: the lint stands down entirely when the set is
    /// empty, so an empty one would make every test pass for the wrong reason.
    /// </summary>
    private static readonly FrozenSet<string> EngineNames =
        new[] { "println" }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    private static (ScriptDatabase Database, PathResolver Resolver) BuildWorkspace()
    {
        FakeFileSystem files = new FakeFileSystem()
            .AddFile(@$"{Raw}\common_scripts\utility.gsc", UtilitySource)
            .AddFile(@$"{Raw}\maps\mp\_load.gsc", LoadSource)
            .AddFile(@$"{Raw}\maps\_chain.gsc", ChainSource);

        RootConfig config = RootConfig.Create(true, @"C:\bo3\share\raw", @"C:\bo3\mods", [], files);
        PathResolver resolver = new(config, files);
        ScriptDatabase database = new();

        Commit(database, @$"{Raw}\common_scripts\utility.gsc", @"common_scripts\utility.gsc", UtilitySource);
        Commit(database, @$"{Raw}\maps\mp\_load.gsc", @"maps\mp\_load.gsc", LoadSource);
        Commit(database, @$"{Raw}\maps\_chain.gsc", @"maps\_chain.gsc", ChainSource);

        return (database, resolver);
    }

    private static void Commit(ScriptDatabase database, string path, string relativePath, string source)
    {
        ParseResult parsed = ScriptAnalysis.Analyze(
            path, ScriptLanguage.Gsc, SourceText.From(source), NullInsertProvider.Instance, new NameTable(), Cod4);

        database.Commit(parsed, ResolutionContext.RawContext, isDirty: false, relativePath);
    }

    private static ImmutableArray<Diagnostic> Lint(string askingSource, GameProfile? profile = null)
    {
        (ScriptDatabase database, PathResolver resolver) = BuildWorkspace();
        GameProfile game = profile ?? Cod4;
        string askingPath = @$"{Raw}\maps\mp\gametypes\_menus.gsc";

        ParseResult result = ScriptAnalysis.Analyze(
            askingPath, ScriptLanguage.Gsc, SourceText.From(askingSource),
            NullInsertProvider.Instance, new NameTable(), game);

        return IncludeUsageLint.Analyze(
            result, database.Gsc, ScriptLanguage.Gsc, resolver, askingPath, EngineNames, "raw", game);
    }

    [Fact]
    public void ReportsACallIntoAFileThatIsNotIncluded()
    {
        // The reported case: _menus.gsc calls scriptPrintln(), which only common_scripts\utility
        // declares, and includes something else entirely. Resolution finds it, the engine will not.
        Diagnostic reported = Assert.Single(Lint(
            "#include maps\\mp\\_load;\ninit()\n{\n\tscriptPrintln();\n}\n"));

        Assert.Equal(GscDiagnosticCode.FunctionNotIncluded, reported.Code);
        Assert.Equal(DiagnosticSeverity.Error, reported.Severity);
        // Case-insensitively: the message carries SymbolKey.Name, which is the lowercase key, the
        // same spelling 5013 and 5014 report a name under.
        Assert.Contains("scriptPrintln", reported.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(@"common_scripts\utility", reported.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void SaysNothingWhenTheDeclaringFileIsIncluded()
    {
        Assert.Empty(Lint("#include common_scripts\\utility;\ninit()\n{\n\tscriptPrintln();\n}\n"));
    }

    [Fact]
    public void SaysNothingWhenTheDeclaringFileIsReachedThroughAnIncludeChain()
    {
        // The include graph is followed transitively, and this is the case that proved it has to be:
        // maps\_createpath.gsc ships calling flag_init while including only maps\_utility, which
        // includes common_scripts\utility on its first line. A direct-includes-only rule reported 36
        // such calls across the stock scripts.
        Assert.Empty(Lint("#include maps\\_chain;\ninit()\n{\n\tscriptPrintln();\n}\n"));
    }

    [Fact]
    public void SaysNothingForAFunctionTheFileDeclaresItself()
    {
        // From the parse in hand, not the store — a function written a moment ago is not indexed yet.
        Assert.Empty(Lint(
            "#include maps\\mp\\_load;\ninit()\n{\n\tscriptPrintln();\n}\n\nscriptPrintln()\n{\n}\n"));
    }

    [Fact]
    public void SaysNothingForAPathCall()
    {
        // A path call names its file outright, so no import makes it legal or illegal.
        Assert.Empty(Lint(
            "#include maps\\mp\\_load;\ninit()\n{\n\tcommon_scripts\\utility::scriptPrintln();\n}\n"));
    }

    [Fact]
    public void SaysNothingForANameNothingDeclares()
    {
        // 5013/5014's verdict. Reporting here as well would blame one call twice, and would tell the
        // reader to import a file that does not have it.
        Assert.Empty(Lint("#include maps\\mp\\_load;\ninit()\n{\n\tno_such_function();\n}\n"));
    }

    [Fact]
    public void SaysNothingForABuiltin()
    {
        Assert.Empty(Lint("#include maps\\mp\\_load;\ninit()\n{\n\tprintln( \"x\" );\n}\n"));
    }

    [Fact]
    public void AnUnresolvableIncludeSuppressesThePass()
    {
        // The file we cannot read might be the one declaring the name.
        Assert.Empty(Lint("#include scripts\\does_not_exist;\ninit()\n{\n\tscriptPrintln();\n}\n"));
    }

    [Fact]
    public void AFileWithNoIncludesAtAllIsLeftAlone()
    {
        Assert.Empty(Lint("init()\n{\n\tscriptPrintln();\n}\n"));
    }

    [Fact]
    public void SaysNothingOnANamespaceDialect()
    {
        // BO3 writes the same mistake as a qualified call into an unimported namespace, which is
        // 5000's to report. Running both would double-report it.
        Assert.Empty(Lint(
            "#include maps\\mp\\_load;\ninit()\n{\n\tscriptPrintln();\n}\n", GameProfile.BlackOps3));
    }

    [Fact]
    public void ReportsACallAMacroExpandedInto()
    {
        // Nothing in the file spells scriptPrintln — a macro does — and the engine still links the
        // expansion, so the include is just as required. Theoretical-looking on a dialect with no
        // preprocessor, but a #define in a CoD4 file is reported as 2016 and then expanded anyway,
        // which is deliberate: suppression has to leave a working file behind.
        Diagnostic reported = Assert.Single(Lint(
            "#include maps\\mp\\_load;\n#define HELP() scriptPrintln()\ninit()\n{\n\tHELP();\n}\n"));

        Assert.Equal(GscDiagnosticCode.FunctionNotIncluded, reported.Code);
    }

    [Fact]
    public void ReportsOnceWhenAMacroBodyCallsTheSameFunctionTwice()
    {
        // Both calls key to the invocation range, so without the (range, name) guard this is the
        // same Error twice on one word.
        Assert.Single(Lint(
            "#include maps\\mp\\_load;\n#define HELP() scriptPrintln(); scriptPrintln()\ninit()\n{\n\tHELP();\n}\n"));
    }
}
