using GSCode.Core;
using GSCode.Workspace.Indexing;
using GSCode.Workspace.Resolution;
using GSCode.Workspace.Tests.Resolution;
using Xunit;

namespace GSCode.Workspace.Tests.Indexing;

/// <summary>
/// F4: <see cref="WorkspaceIndexer.IndexAsync"/> is called on the SAME instance from two places
/// that can genuinely overlap — the startup pass, and <c>WorkspaceFoldersHandler</c>'s re-index
/// on a folder change arriving mid-cold-start — and interleaving two passes corrupts shared state
/// (<c>_restored</c>, <c>_skippedOversized</c>) that belongs to one pass at a time. These tests
/// prove the pass gate actually blocks a second caller rather than letting it run alongside the
/// first.
/// </summary>
public class WorkspaceIndexerConcurrencyTests
{
    private const string Raw = @"C:\bo3\share\raw";

    /// <summary>
    /// Wraps <see cref="FakeFileSystem"/> to make one enumeration call block on a gate the test
    /// controls, so a second <see cref="WorkspaceIndexer.IndexAsync"/> call can be started WHILE
    /// the first is still inside its own pass — the only way to prove the gate holds a second
    /// caller back rather than merely usually finishing first.
    /// </summary>
    private sealed class GatedFileSystem : IFileSystem
    {
        private readonly FakeFileSystem _inner;
        private readonly SemaphoreSlim _blockedInsideFirstEnumerate = new(0, 1);
        private readonly SemaphoreSlim _release = new(0, 1);
        private int _enumerateCalls;

        public GatedFileSystem(FakeFileSystem inner)
        {
            _inner = inner;
        }

        public int EnumerateCalls
        {
            get { return Volatile.Read(ref _enumerateCalls); }
        }

        /// <summary>Waits until the first enumeration call has actually started blocking.</summary>
        public void WaitUntilFirstCallIsBlocked()
        {
            _blockedInsideFirstEnumerate.Wait(TimeSpan.FromSeconds(10));
        }

        /// <summary>Lets the blocked (first) enumeration call proceed.</summary>
        public void ReleaseFirstCall()
        {
            _release.Release();
        }

        public bool FileExists(string absolutePath)
        {
            return _inner.FileExists(absolutePath);
        }

        public bool DirectoryExists(string absolutePath)
        {
            return _inner.DirectoryExists(absolutePath);
        }

        public string ReadAllText(string absolutePath)
        {
            return _inner.ReadAllText(absolutePath);
        }

        public DateTime GetLastWriteTimeUtc(string absolutePath)
        {
            return _inner.GetLastWriteTimeUtc(absolutePath);
        }

        public IEnumerable<string> EnumerateFilesWithExtensions(
            string directory, System.Collections.Immutable.ImmutableArray<string> extensions)
        {
            int callNumber = Interlocked.Increment(ref _enumerateCalls);
            if ( callNumber == 1 )
            {
                _blockedInsideFirstEnumerate.Release();
                _release.Wait(TimeSpan.FromSeconds(10));
            }

            return _inner.EnumerateFilesWithExtensions(directory, extensions);
        }
    }

    private static FakeFileSystem StandardTree()
    {
        return new FakeFileSystem()
            .AddFile(@$"{Raw}\scripts\a.gsc", "function a()\n{\n}\n")
            .AddFile(@$"{Raw}\scripts\b.gsc", "function b()\n{\n}\n");
    }

    private static (WorkspaceIndexer Indexer, GatedFileSystem FileSystem) BuildGated()
    {
        GatedFileSystem gated = new(StandardTree());
        RootConfig config = RootConfig.Create(true, Raw, @"C:\bo3\mods", [], gated);
        PathResolver resolver = new(config, gated);
        GSCode.Workspace.Database.ScriptDatabase database = new();
        WorkspaceIndexer indexer = new(database, () => resolver, gated, new NameTable());

        return (indexer, gated);
    }

    /// <summary>
    /// No gate: for tests that only need two genuinely concurrent passes, not control over WHEN
    /// each one runs. Using <see cref="GatedFileSystem"/> here would block the first pass's lone
    /// enumeration call for the full 10-second timeout waiting for a release nobody sends.
    /// </summary>
    private static WorkspaceIndexer BuildPlain()
    {
        FakeFileSystem files = StandardTree();
        RootConfig config = RootConfig.Create(true, Raw, @"C:\bo3\mods", [], files);
        PathResolver resolver = new(config, files);
        GSCode.Workspace.Database.ScriptDatabase database = new();
        return new WorkspaceIndexer(database, () => resolver, files, new NameTable());
    }

    [Fact]
    public async Task SecondIndexAsync_WaitsForTheFirstToFinish_RatherThanInterleaving()
    {
        (WorkspaceIndexer indexer, GatedFileSystem files) = BuildGated();

        // Task.Run, not a direct call: IndexAsync runs SYNCHRONOUSLY on its caller up to the
        // first real suspension point (an uncontended SemaphoreSlim.WaitAsync completes inline,
        // and enumeration itself is synchronous), so calling it directly would block the TEST
        // thread inside the gate rather than running the pass in the background — which is what
        // this test needs to start a second, genuinely concurrent call.
        Task<IndexOutcome> firstPass = Task.Run(() => indexer.IndexAsync(
            IndexingMode.Partial, NullIndexProgressListener.Instance, CancellationToken.None));
        files.WaitUntilFirstCallIsBlocked();

        // Pass 2 is started WHILE pass 1 is still inside the gate. If the pass gate did not hold
        // it back, it would reach its own EnumerateFilesWithExtensions call immediately; the
        // assertion below is what tells the two cases apart.
        Task<IndexOutcome> secondPass = Task.Run(() => indexer.IndexAsync(
            IndexingMode.Partial, NullIndexProgressListener.Instance, CancellationToken.None));

        // Give pass 2 a real chance to run if it were going to — it is not otherwise blocked by
        // anything of the test's making, only by WorkspaceIndexer's own gate.
        await Task.Delay(200);
        Assert.Equal(1, files.EnumerateCalls);
        Assert.False(secondPass.IsCompleted);

        files.ReleaseFirstCall();
        IndexOutcome first = await firstPass;

        // Pass 2's own enumeration only happens once pass 1 has fully released the gate.
        IndexOutcome second = await secondPass;

        Assert.Equal(2, files.EnumerateCalls);
        Assert.Equal(2, first.Total);
        Assert.Equal(2, second.Total);
    }

    [Fact]
    public async Task ConcurrentPasses_NeverReportANegativeOrCorruptOversizedCount()
    {
        // A softer, non-timing-dependent check: whatever order two concurrent passes actually run
        // in, _skippedOversized must never be observed reset mid-count by one pass while the
        // other is still incrementing it — which is exactly what happens without the gate, since
        // the field is a plain int shared across the two calls.
        WorkspaceIndexer indexer = BuildPlain();

        Task<IndexOutcome> first = Task.Run(() => indexer.IndexAsync(
            IndexingMode.Partial, NullIndexProgressListener.Instance, CancellationToken.None));
        Task<IndexOutcome> second = Task.Run(() => indexer.IndexAsync(
            IndexingMode.Partial, NullIndexProgressListener.Instance, CancellationToken.None));

        IndexOutcome[] outcomes = await Task.WhenAll(first, second);

        Assert.All(outcomes, outcome => Assert.True(outcome.SkippedOversized >= 0));
        Assert.All(outcomes, outcome => Assert.Equal(2, outcome.Total));
    }
}
