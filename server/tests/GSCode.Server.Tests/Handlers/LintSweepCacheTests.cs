using GSCode.Core.Diagnostics;
using GSCode.Server.Handlers;
using GSCode.Workspace.Cache;
using GSCode.Workspace.Database;
using GSCode.Workspace.Indexing;
using Xunit;

namespace GSCode.Server.Tests.Handlers;

/// <summary>
/// The lint sweep's results kept for the next start: used only when the workspace is the one they
/// were computed against.
///
/// Two sessions over the same files, each a workspace of its own, as two starts are. The first
/// sweeps and captures; the second either restores the capture or must sweep again. What decides it
/// is the subject: a cross-file result depends on files other than its own, so a caller whose own
/// text did not change can still owe a new diagnostic — and restoring the old set would hide it.
/// </summary>
public sealed class LintSweepCacheTests
{
    // "lib::helper( 1, 2 );" calls a one-parameter helper with two arguments: TooManyArguments.
    private const string CallerSource = "#using scripts\\lib;\n#namespace game;\nfunction run()\n{\n\tlib::helper( 1, 2 );\n}\n";

    private const string OneParameter = "#namespace lib;\nfunction helper( a )\n{\n}\n";

    private const string TwoParameters = "#namespace lib;\nfunction helper( a, b )\n{\n}\n";

    private static string CallerPath => TestPaths.Raw(@"scripts\caller.gsc");

    /// <summary>A session: the workspace indexed in full mode, and its sweep.</summary>
    private static async Task<HandlerWorkspace> StartAsync(string libSource)
    {
        return await HandlerWorkspace.BuildAsync(
            [new TestFile(@"scripts\lib.gsc", libSource), new TestFile(@"scripts\caller.gsc", CallerSource)],
            mode: IndexingMode.Full);
    }

    private static async Task SweepAsync(HandlerWorkspace workspace)
    {
        DocumentLinter linter = new(workspace.Database, workspace.ResolverHolder, workspace.Builtins, workspace.ObjectFields);
        WorkspaceLintSweep sweep = new(workspace.Database, workspace.Documents, workspace.Indexer, linter);
        await sweep.RunFullSweepAsync(CancellationToken.None);
    }

    /// <summary>The first session's sweep, captured as the next start would read it.</summary>
    private static async Task<StoredSweep> FirstSessionAsync(string libSource)
    {
        using HandlerWorkspace first = await StartAsync(libSource);
        await SweepAsync(first);
        SweepCapture? capture = LintSweepCache.Capture(first.Database, first.Resolver);
        Assert.NotNull(capture);
        return new StoredSweep(capture!.Fingerprint, capture.Entries.ToDictionary(entry => entry.Path, StringComparer.Ordinal));
    }

    private static List<GscDiagnosticCode> CodesAt(HandlerWorkspace workspace, string path)
    {
        Assert.True(workspace.Database.TryGetAnyRecord(path, out ScriptRecord record));
        return [.. record.Diagnostics.Select(diagnostic => diagnostic.Code)];
    }

    [Fact]
    public async Task AnUnchangedWorkspaceGetsTheSweepsOwnDiagnosticsBack()
    {
        StoredSweep stored = await FirstSessionAsync(OneParameter);

        using HandlerWorkspace second = await StartAsync(OneParameter);
        Assert.DoesNotContain(GscDiagnosticCode.TooManyArguments, CodesAt(second, CallerPath));

        List<string>? unrestored = LintSweepCache.TryRestore(second.Database, second.Resolver, stored, second.Names, out int restored);

        Assert.NotNull(unrestored);
        Assert.Empty(unrestored!);
        Assert.Equal(2, restored);
        Assert.Contains(GscDiagnosticCode.TooManyArguments, CodesAt(second, CallerPath));
    }

    [Fact]
    public async Task AWorkspaceWithAChangedSignatureIsSweptAgain()
    {
        // The caller's own text is the same in both sessions; only the function it calls changed.
        // Restoring would put back the TooManyArguments the new signature no longer earns.
        StoredSweep stored = await FirstSessionAsync(OneParameter);

        using HandlerWorkspace second = await StartAsync(TwoParameters);
        Assert.Null(LintSweepCache.TryRestore(second.Database, second.Resolver, stored, second.Names, out _));

        await SweepAsync(second);
        Assert.DoesNotContain(GscDiagnosticCode.TooManyArguments, CodesAt(second, CallerPath));
    }

    [Fact]
    public async Task ANewProblemInAnUnchangedCallerIsNotHiddenByTheStoredSweep()
    {
        // The other direction: the caller was clean, and the signature it calls lost a parameter.
        StoredSweep stored = await FirstSessionAsync(TwoParameters);

        using HandlerWorkspace second = await StartAsync(OneParameter);
        Assert.Null(LintSweepCache.TryRestore(second.Database, second.Resolver, stored, second.Names, out _));

        await SweepAsync(second);
        Assert.Contains(GscDiagnosticCode.TooManyArguments, CodesAt(second, CallerPath));
    }

    [Fact]
    public async Task AFileTheStoredSweepDoesNotCoverIsReturnedToBeLinted()
    {
        StoredSweep stored = await FirstSessionAsync(OneParameter);
        Dictionary<string, SweptEntry> partial = new(stored.Entries, StringComparer.Ordinal);
        Assert.True(partial.Remove(PathUtilNormalized(CallerPath)));

        using HandlerWorkspace second = await StartAsync(OneParameter);
        List<string>? unrestored = LintSweepCache.TryRestore(
            second.Database, second.Resolver, new StoredSweep(stored.Fingerprint, partial), second.Names, out int restored);

        Assert.Equal([PathUtilNormalized(CallerPath)], unrestored);
        Assert.Equal(1, restored);
    }

    [Fact]
    public async Task AnImportTargetThatVanishesWithoutARecordChangeIsSweptAgain()
    {
        // UsingNotFound asks the resolver whether the target exists on disk, not the index: a file
        // the index skips (oversized, or outside the folders it walks) can vanish between sessions
        // and change no record at all. The records here are the same; only the disk answer moves.
        using HandlerWorkspace workspace = await StartAsync(OneParameter);
        string before = LintSweepCache.Fingerprint(workspace.Database, workspace.Resolver);

        workspace.Files.RemoveFile(TestPaths.Raw(@"scripts\lib.gsc"));
        workspace.Resolver.InvalidateResolutionCache();

        Assert.NotEqual(before, LintSweepCache.Fingerprint(workspace.Database, workspace.Resolver));
    }

    private static string PathUtilNormalized(string path)
    {
        return GSCode.Core.Paths.PathUtil.NormalizeAbsolute(path);
    }
}
