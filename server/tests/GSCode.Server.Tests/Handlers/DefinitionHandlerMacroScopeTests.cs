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
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using Xunit;
using LspPosition = OmniSharp.Extensions.LanguageServer.Protocol.Models.Position;

namespace GSCode.Server.Tests.Handlers;

/// <summary>
/// Go-to-definition on a macro USE, for the reported bug: <c>animation_shared.gsc</c> and its
/// sibling <c>animation_shared.csc</c> each define their own <c>CF_CRACKS_ALL</c>, and F12 from
/// the .gsc listed both. Go-to-def on a macro widened its store scope to both language worlds
/// (<see cref="DatabaseQueries.FindAllReferences"/>) so that a RENAME started from either file
/// reaches the other — <see cref="MacroRenameAcrossLanguagesTests"/> covers that, and is exactly
/// right for a macro that is genuinely SHARED via a #insert'ed header. But
/// <see cref="DatabaseQueries.PreferIncludeScope"/> compares paths with the extension stripped, so
/// two SEPARATE macros that merely happen to share a name and a stem — one per language file, each
/// with its own local #define, nothing inserted — collapsed into "the same file" and both
/// definitions came back.
///
/// The fix answers a macro go-to-definition from the asking file's OWN preprocessor result
/// (<c>ParseResult.Preprocessed.Macros</c>), which already knows, per file, exactly which
/// definition is in effect — local shadowing a header's, or the header's own — with no workspace
/// query at all. Rename stays wide; only definition narrows.
/// </summary>
public class DefinitionHandlerMacroScopeTests
{
    private const string Raw = @"C:\bo3\share\raw";
    private static string GscPath => Path.Combine(Raw, @"scripts\shared\animation_shared.gsc");
    private static string CscPath => Path.Combine(Raw, @"scripts\shared\animation_shared.csc");

    // Each file defines its OWN CF_CRACKS_ALL, independently — no #insert, no shared header.
    // Different values, so a wrong answer is not just "extra" but the WRONG constant.
    private const string GscSource =
        "#define CF_CRACKS_ALL 4\nfunction f()\n{\n    x = CF_CRACKS_ALL;\n}\n";
    private const string CscSource =
        "#define CF_CRACKS_ALL 9\nfunction f()\n{\n    x = CF_CRACKS_ALL;\n}\n";

    /// <summary>The CF_CRACKS_ALL use on line 3 of either source, inside the name.</summary>
    private static LspPosition MacroUse => new(3, 10);

    private static ParseResult AnalyzeAt(string source, string path, ScriptLanguage language)
    {
        return ScriptAnalysis.Analyze(
            path, language, SourceText.From(source), NullInsertProvider.Instance, new NameTable());
    }

    private static DefinitionHandler BuildHandler(string askingPath, string askingSource, ScriptLanguage askingLanguage)
    {
        ScriptDatabase database = new();
        database.Commit(
            AnalyzeAt(GscSource, GscPath, ScriptLanguage.Gsc),
            ResolutionContext.RawContext, false, @"scripts\shared\animation_shared.gsc");
        database.Commit(
            AnalyzeAt(CscSource, CscPath, ScriptLanguage.Csc),
            ResolutionContext.RawContext, false, @"scripts\shared\animation_shared.csc");

        DocumentStore documents = new(static _ => NullInsertProvider.Instance, new NameTable());
        documents.AnalyzeIfStale(documents.Open(askingPath, askingSource, 1));

        NavigationSupport support = new(documents, database, new ResolverHolder(new PhysicalFileSystem()));

        return new DefinitionHandler(
            support, TextDocumentSelector.ForLanguage(askingLanguage == ScriptLanguage.Csc ? "csc" : "gsc"));
    }

    private static async Task<LocationOrLocationLinks?> DefinitionAsync(
        string askingPath, string askingSource, ScriptLanguage language, LspPosition position)
    {
        DefinitionHandler handler = BuildHandler(askingPath, askingSource, language);

        return await handler.Handle(
            new DefinitionParams
            {
                TextDocument = new TextDocumentIdentifier { Uri = DocumentUri.FromFileSystemPath(askingPath) },
                Position = position,
            },
            CancellationToken.None);
    }

    [Fact]
    public async Task MacroDefinitionFromTheGsc_ReturnsOnlyTheGscsOwnDefine()
    {
        LocationOrLocationLinks? result = await DefinitionAsync(GscPath, GscSource, ScriptLanguage.Gsc, MacroUse);

        Assert.NotNull(result);
        LocationOrLocationLink[] locations = [.. result!];
        Location single = Assert.Single(locations).Location!;

        Assert.Equal(
            DocumentUri.FromFileSystemPath(GscPath).ToString(),
            single.Uri.ToString());
        Assert.False(
            single.Uri.ToString().EndsWith(".csc", StringComparison.OrdinalIgnoreCase),
            "the .csc's unrelated same-named macro must not appear");
    }

    [Fact]
    public async Task MacroDefinitionFromTheCsc_ReturnsOnlyTheCscsOwnDefine()
    {
        LocationOrLocationLinks? result = await DefinitionAsync(CscPath, CscSource, ScriptLanguage.Csc, MacroUse);

        Assert.NotNull(result);
        LocationOrLocationLink[] locations = [.. result!];
        Location single = Assert.Single(locations).Location!;

        Assert.Equal(
            DocumentUri.FromFileSystemPath(CscPath).ToString(),
            single.Uri.ToString());
    }
}
