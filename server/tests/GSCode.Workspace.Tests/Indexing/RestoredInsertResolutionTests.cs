using GSCode.Core;
using GSCode.Workspace.Cache;
using GSCode.Workspace.Database;
using GSCode.Workspace.Indexing;
using GSCode.Workspace.Resolution;
using Xunit;

namespace GSCode.Workspace.Tests.Indexing;

/// <summary>
/// Whether phase two's "restored file whose inserted header changed" check survives a full server
/// RESTART with a persistent cache — as opposed to <see cref="HeaderChangeReachTests"/>, which
/// covers the same insert graph reached through the live file watcher within one running session.
///
/// A restored record's insert edge carries the RESOLUTION FROM LAST SESSION. Phase two decided
/// staleness by checking whether that stored resolution was a key in <c>changedHeaders</c> — which
/// is keyed by headers this pass actually re-analysed. A header that did not exist last session
/// resolved to "" back then, so the stored edge says "", and "" is never a real header's path — so
/// the restored inserter was never marked stale once the header appeared. The mirror case (a header
/// deleted between sessions) never gets INTO changedHeaders at all, since it is no longer among the
/// files this pass even sees.
/// </summary>
public class RestoredInsertResolutionTests
{
    private static readonly string s_headerPath = TestPaths.Raw(@"scripts\shared\base.gsh");
    private static readonly string s_scriptPath = TestPaths.Raw(@"scripts\uses_it.gsc");

    private static bool Declares(ScriptDatabase database, string functionName)
    {
        return DatabaseQueries.LookupFunctions(database.Gsc, "raw", "", null, functionName).Any();
    }

    /// <summary>One indexing pass, backed by a real SqliteCache, as one server "session" would run it.</summary>
    private static async Task<ScriptDatabase> RunSessionAsync(FakeFileSystem files, SqliteCache cache)
    {
        RootConfig config = TestPaths.Config(files);
        PathResolver resolver = new(config, files);
        ScriptDatabase database = new();
        WorkspaceIndexer indexer = new(database, () => resolver, files, new NameTable());

        indexer.UseCache(cache, cache.LoadAll());
        await indexer.IndexAsync(IndexingMode.Full, NullIndexProgressListener.Instance, CancellationToken.None);
        await cache.WaitForIdleAsync(CancellationToken.None);

        return database;
    }

    [Fact]
    public async Task AHeaderCreatedBetweenSessions_ReachesTheRestoredScriptThatInsertsIt()
    {
        string dbPath = Path.Combine(Path.GetTempPath(), $"gscode-restored-insert-test-{Guid.NewGuid():N}.db");
        SqliteCache cache = SqliteCache.Open(dbPath, "test-identity");
        try
        {
            FakeFileSystem files = new FakeFileSystem()
                .AddFile(s_scriptPath, "#insert scripts\\shared\\base.gsh;\nfunction FN_NAME()\n{\n}\n");

            // Session 1: base.gsh does not exist yet, so the insert never resolves and the name is
            // never macro-expanded. Its record — including the unresolved edge — is cached.
            await RunSessionAsync(files, cache);

            // The header is created while the server is off (a branch switch, another tool, or
            // simply the file arriving after this workspace was last opened).
            files.AddFile(s_headerPath, "#define FN_NAME arrived\n");

            // Session 2: uses_it.gsc's bytes are unchanged, so it restores from cache — but the
            // header it inserts now exists, and macro expansion should follow.
            ScriptDatabase secondSession = await RunSessionAsync(files, cache);

            Assert.True(Declares(secondSession, "arrived"));
            Assert.False(Declares(secondSession, "fn_name"));
        }
        finally
        {
            await cache.DisposeAsync();
            SqliteCache.DeleteDatabase(dbPath);
        }
    }

    [Fact]
    public async Task AHeaderDeletedBetweenSessions_StopsReachingTheRestoredScript()
    {
        string dbPath = Path.Combine(Path.GetTempPath(), $"gscode-restored-insert-test-{Guid.NewGuid():N}.db");
        SqliteCache cache = SqliteCache.Open(dbPath, "test-identity");
        try
        {
            FakeFileSystem files = new FakeFileSystem()
                .AddFile(s_headerPath, "#define FN_NAME arrived\n")
                .AddFile(s_scriptPath, "#insert scripts\\shared\\base.gsh;\nfunction FN_NAME()\n{\n}\n");

            // Session 1: the header resolves and its macro expands into the script's declaration.
            await RunSessionAsync(files, cache);

            // The header is deleted while the server is off.
            files.RemoveFile(s_headerPath);

            // Session 2: uses_it.gsc's bytes are unchanged, so it restores from cache — but the
            // header it used to insert is gone, so the macro must stop applying.
            ScriptDatabase secondSession = await RunSessionAsync(files, cache);

            Assert.True(Declares(secondSession, "fn_name"));
            Assert.False(Declares(secondSession, "arrived"));
        }
        finally
        {
            await cache.DisposeAsync();
            SqliteCache.DeleteDatabase(dbPath);
        }
    }
}
