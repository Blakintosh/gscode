using System.Collections.Immutable;
using GSCode.Core.Symbols;
using GSCode.Server.Handlers;
using GSCode.Workspace.Database;
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
    private const string GscRelativePath = @"scripts\shared\animation_shared.gsc";
    private const string CscRelativePath = @"scripts\shared\animation_shared.csc";

    // Each file defines and uses its OWN CF_CRACKS_ALL, independently.
    private const string GscSource =
        "#define CF_CRACKS_ALL 4\nfunction f()\n{\n    x = CF_CRACKS_ALL;\n}\n";
    private const string CscSource =
        "#define CF_CRACKS_ALL 9\nfunction f()\n{\n    x = CF_CRACKS_ALL;\n}\n";

    /// <summary>
    /// The references to <paramref name="macro"/> as the .gsc sees them, over a workspace holding
    /// both scripts and whatever <paramref name="extraFiles"/> adds.
    /// </summary>
    private static async Task<ImmutableArray<(ScriptRecord Record, ReferenceEntry Entry)>> ReferencesFromTheGscAsync(
        string gscSource, string cscSource, string macro, params TestFile[] extraFiles)
    {
        List<TestFile> files = [.. extraFiles];
        files.Add(new TestFile(GscRelativePath, gscSource));
        files.Add(new TestFile(CscRelativePath, cscSource));

        using HandlerWorkspace workspace = await HandlerWorkspace.BuildAsync(files);
        workspace.Open(GscRelativePath);

        NavigationTarget target = workspace.Navigation.Resolve(
            HandlerWorkspace.Identify(GscRelativePath).Uri, CancellationToken.None)!;
        return workspace.Navigation.FindAllReferences(target, MacroKey(macro));
    }

    private static SymbolKey MacroKey(string name)
    {
        return new SymbolKey(null, name, SymbolKind.Macro);
    }

    [Fact]
    public async Task CodeLensCount_OnTheGscsOwnMacro_DoesNotCountTheCscsUnrelatedOne()
    {
        ImmutableArray<(ScriptRecord Record, ReferenceEntry Entry)> found =
            await ReferencesFromTheGscAsync(GscSource, CscSource, "CF_CRACKS_ALL");

        Assert.DoesNotContain(found, entry => entry.Record.Path.EndsWith(".csc", StringComparison.OrdinalIgnoreCase));
        // Exactly the .gsc's own definition plus its one use — not the two-file total.
        Assert.Equal(2, found.Length);
    }

    [Fact]
    public async Task CodeLensCount_StillSpansBothLanguages_ForAGenuinelySharedHeaderMacro()
    {
        // The width must not disappear for the case it exists for: a macro #insert'ed from a
        // shared .gsh really is used from both worlds, and CodeLens/rename/find-references must
        // still see all of it.
        const string headerSource = "#define MAX_FLAGS 8\n";
        const string gscSource = "#insert scripts\\shared\\flags.gsh;\nfunction f()\n{\n    x = MAX_FLAGS;\n}\n";
        const string cscSource = "#insert scripts\\shared\\flags.gsh;\nfunction f()\n{\n    x = MAX_FLAGS;\n}\n";

        ImmutableArray<(ScriptRecord Record, ReferenceEntry Entry)> found = await ReferencesFromTheGscAsync(
            gscSource, cscSource, "MAX_FLAGS", new TestFile(@"scripts\shared\flags.gsh", headerSource));

        Assert.Contains(found, entry => entry.Record.Path.EndsWith(".csc", StringComparison.OrdinalIgnoreCase));
    }
}
