using GSCode.Core;
using GSCode.Core.Symbols;
using GSCode.Core.Text;
using GSCode.Parser;
using GSCode.Parser.Preprocessing;
using GSCode.Server.Configuration;
using GSCode.Server.Handlers;
using GSCode.Workspace.Database;
using GSCode.Workspace.Documents;
using GSCode.Workspace.Resolution;
using OmniSharp.Extensions.LanguageServer.Protocol;
using Xunit;

namespace GSCode.Server.Tests.Handlers;

/// <summary>
/// CodeLens' reference count and Find References' peek list — both driven by
/// <see cref="NavigationSupport.FindAllReferences(NavigationTarget, GSCode.Core.Symbols.SymbolKey, GSCode.Core.Symbols.ReferenceKind)"/> —
/// for the same bug
/// <see cref="DefinitionHandlerMacroScopeTests"/> covers for go-to-definition: two INDEPENDENT
/// same-named macros, one per language file, no #insert between them.
/// <c>DatabaseQueries.FindAllReferences</c> widens every macro key to both language stores
/// unconditionally, which is right for a macro genuinely reached through a shared header
/// (<c>MacroRenameAcrossLanguagesTests</c>) but conflated the .gsc's own CF_CRACKS_ALL with the
/// .csc's unrelated one: the CodeLens on <c>animation_shared.gsc</c> read "6 references" —
/// counting the .csc's own definitions and uses — even after the go-to-definition fix.
/// </summary>
public class MacroReferenceScopeTests
{
    private const string Raw = @"C:\bo3\share\raw";
    private static string GscPath => Path.Combine(Raw, @"scripts\shared\animation_shared.gsc");
    private static string CscPath => Path.Combine(Raw, @"scripts\shared\animation_shared.csc");

    // Each file defines and uses its OWN CF_CRACKS_ALL, independently.
    private const string GscSource =
        "#define CF_CRACKS_ALL 4\nfunction f()\n{\n    x = CF_CRACKS_ALL;\n}\n";
    private const string CscSource =
        "#define CF_CRACKS_ALL 9\nfunction f()\n{\n    x = CF_CRACKS_ALL;\n}\n";

    private static ParseResult AnalyzeAt(string source, string path, ScriptLanguage language)
    {
        return ScriptAnalysis.Analyze(
            path, language, SourceText.From(source), NullInsertProvider.Instance, new NameTable());
    }

    private static NavigationSupport BuildSupport(out NavigationTarget gscTarget)
    {
        ScriptDatabase database = new();
        database.Commit(
            AnalyzeAt(GscSource, GscPath, ScriptLanguage.Gsc),
            ResolutionContext.RawContext, false, @"scripts\shared\animation_shared.gsc");
        database.Commit(
            AnalyzeAt(CscSource, CscPath, ScriptLanguage.Csc),
            ResolutionContext.RawContext, false, @"scripts\shared\animation_shared.csc");

        DocumentStore documents = new(static _ => NullInsertProvider.Instance, new NameTable());
        documents.AnalyzeIfStale(documents.Open(GscPath, GscSource, 1));

        NavigationSupport support = new(documents, database, new ResolverHolder(new PhysicalFileSystem()));
        gscTarget = support.Resolve(DocumentUri.FromFileSystemPath(GscPath), CancellationToken.None)!;
        return support;
    }

    private static SymbolKey MacroKey(string name)
    {
        return new SymbolKey(null, name, SymbolKind.Macro);
    }

    [Fact]
    public void CodeLensCount_OnTheGscsOwnMacro_DoesNotCountTheCscsUnrelatedOne()
    {
        NavigationSupport support = BuildSupport(out NavigationTarget gscTarget);

        System.Collections.Immutable.ImmutableArray<(ScriptRecord Record, ReferenceEntry Entry)> found =
            support.FindAllReferences(gscTarget, MacroKey("CF_CRACKS_ALL"));

        Assert.DoesNotContain(found, entry => entry.Record.Path.EndsWith(".csc", StringComparison.OrdinalIgnoreCase));
        // Exactly the .gsc's own definition plus its one use — not the two-file total.
        Assert.Equal(2, found.Length);
    }

    [Fact]
    public void CodeLensCount_StillSpansBothLanguages_ForAGenuinelySharedHeaderMacro()
    {
        // The width must not disappear for the case it exists for: a macro #insert'ed from a
        // shared .gsh really is used from both worlds, and CodeLens/rename/find-references must
        // still see all of it.
        const string headerRawPath = @"scripts\shared\flags.gsh";
        string headerPath = Path.Combine(Raw, headerRawPath);
        const string headerSource = "#define MAX_FLAGS 8\n";
        const string gscSource = "#insert scripts\\shared\\flags.gsh;\nfunction f()\n{\n    x = MAX_FLAGS;\n}\n";
        const string cscSource = "#insert scripts\\shared\\flags.gsh;\nfunction f()\n{\n    x = MAX_FLAGS;\n}\n";

        HeaderInsertProvider provider = new(headerPath, headerSource);

        ParseResult AnalyzeWithHeader(string source, string path, ScriptLanguage language)
        {
            return ScriptAnalysis.Analyze(path, language, SourceText.From(source), provider, new NameTable());
        }

        ScriptDatabase database = new();
        database.Commit(
            AnalyzeWithHeader(headerSource, headerPath, ScriptLanguage.Gsh),
            ResolutionContext.RawContext, false, headerRawPath);
        database.Commit(
            AnalyzeWithHeader(gscSource, GscPath, ScriptLanguage.Gsc),
            ResolutionContext.RawContext, false, @"scripts\shared\animation_shared.gsc");
        database.Commit(
            AnalyzeWithHeader(cscSource, CscPath, ScriptLanguage.Csc),
            ResolutionContext.RawContext, false, @"scripts\shared\animation_shared.csc");

        DocumentStore documents = new(_ => provider, new NameTable());
        documents.AnalyzeIfStale(documents.Open(GscPath, gscSource, 1));

        NavigationSupport support = new(documents, database, new ResolverHolder(new PhysicalFileSystem()));
        NavigationTarget target = support.Resolve(DocumentUri.FromFileSystemPath(GscPath), CancellationToken.None)!;

        System.Collections.Immutable.ImmutableArray<(ScriptRecord Record, ReferenceEntry Entry)> found =
            support.FindAllReferences(target, MacroKey("MAX_FLAGS"));

        Assert.Contains(found, entry => entry.Record.Path.EndsWith(".csc", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Serves one header, the same shape <c>MacroRenameAcrossLanguagesTests</c> uses.</summary>
    private sealed class HeaderInsertProvider : IInsertProvider
    {
        private readonly string _resolvedPath;
        private readonly GSCode.Core.Text.SourceText _text;
        private readonly System.Collections.Immutable.ImmutableArray<GSCode.Parser.Lexing.Token> _tokens;
        private readonly string _rawPath;

        public HeaderInsertProvider(string resolvedPath, string source)
        {
            _resolvedPath = GSCode.Core.Paths.PathUtil.NormalizeAbsolute(resolvedPath);
            _text = GSCode.Core.Text.SourceText.From(source);
            _tokens = GSCode.Parser.Lexing.Lexer.Lex(_text).Tokens;
            _rawPath = @"scripts\shared\flags.gsh";
        }

        public bool TryGetInsert(string rawInsertPath, out InsertedFile inserted)
        {
            inserted = new InsertedFile(_resolvedPath, _text, _tokens);
            return string.Equals(rawInsertPath, _rawPath, StringComparison.OrdinalIgnoreCase);
        }

        public bool TryResolveInsertPath(string rawInsertPath, out string resolvedPath)
        {
            resolvedPath = _resolvedPath;
            return string.Equals(rawInsertPath, _rawPath, StringComparison.OrdinalIgnoreCase);
        }
    }
}
