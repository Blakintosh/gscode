using GSCode.Server.Handlers;
using GSCode.Workspace.Database;
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
    private const string GscRelativePath = @"scripts\shared\animation_shared.gsc";
    private const string CscRelativePath = @"scripts\shared\animation_shared.csc";

    // Each file defines its OWN CF_CRACKS_ALL, independently — no #insert, no shared header.
    // Different values, so a wrong answer is not just "extra" but the WRONG constant.
    private const string GscSource =
        "#define CF_CRACKS_ALL 4\nfunction f()\n{\n    x = CF_CRACKS_ALL;\n}\n";
    private const string CscSource =
        "#define CF_CRACKS_ALL 9\nfunction f()\n{\n    x = CF_CRACKS_ALL;\n}\n";

    /// <summary>The CF_CRACKS_ALL use on line 3 of either source, inside the name.</summary>
    private static LspPosition MacroUse => new(3, 10);

    private static async Task<LocationOrLocationLinks?> DefinitionAsync(string askingRelativePath, LspPosition position)
    {
        using HandlerWorkspace workspace = await HandlerWorkspace.BuildAsync(
        [
            new TestFile(GscRelativePath, GscSource),
            new TestFile(CscRelativePath, CscSource),
        ]);
        workspace.Open(askingRelativePath);

        DefinitionHandler handler = new(workspace.Navigation, HandlerWorkspace.Selector);

        return await handler.Handle(
            new DefinitionParams
            {
                TextDocument = HandlerWorkspace.Identify(askingRelativePath),
                Position = position,
            },
            CancellationToken.None);
    }

    [Fact]
    public async Task MacroDefinitionFromTheGsc_ReturnsOnlyTheGscsOwnDefine()
    {
        LocationOrLocationLinks? result = await DefinitionAsync(GscRelativePath, MacroUse);

        Assert.NotNull(result);
        LocationOrLocationLink[] locations = [.. result!];
        Location single = Assert.Single(locations).Location!;

        Assert.Equal(
            HandlerWorkspace.Identify(GscRelativePath).Uri.ToString(),
            single.Uri.ToString());
        Assert.False(
            single.Uri.ToString().EndsWith(".csc", StringComparison.OrdinalIgnoreCase),
            "the .csc's unrelated same-named macro must not appear");
    }

    [Fact]
    public async Task MacroDefinitionFromTheCsc_ReturnsOnlyTheCscsOwnDefine()
    {
        LocationOrLocationLinks? result = await DefinitionAsync(CscRelativePath, MacroUse);

        Assert.NotNull(result);
        LocationOrLocationLink[] locations = [.. result!];
        Location single = Assert.Single(locations).Location!;

        Assert.Equal(
            HandlerWorkspace.Identify(CscRelativePath).Uri.ToString(),
            single.Uri.ToString());
    }
}
