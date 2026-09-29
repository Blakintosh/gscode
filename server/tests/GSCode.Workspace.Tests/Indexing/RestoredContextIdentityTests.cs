using GSCode.Core;
using GSCode.Core.Paths;
using GSCode.Workspace.Cache;
using GSCode.Workspace.Database;
using GSCode.Workspace.Indexing;
using GSCode.Workspace.Resolution;
using Xunit;

namespace GSCode.Workspace.Tests.Indexing;

/// <summary>
/// The reported bug: a restored cache hit in <see cref="WorkspaceIndexer"/>'s <c>ProcessFile</c>
/// commits the CACHED blob's <see cref="ScriptRecord.ContextId"/> and
/// <see cref="ScriptRecord.RelativePath"/> unconditionally, even though both are pure functions of
/// (absolute path, current <see cref="RootConfig"/>) that the fresh-analysis path below always
/// recomputes. When a workspace folder is added or removed between two sessions of the SAME cache,
/// a file whose bytes never changed can be reclassified into a different context — raw becoming a
/// mod, or one mod folder becoming another — but a cache hit keeps reporting the STALE
/// classification from last session, silently breaking overlay shadowing and go-to-definition
/// scoping for that file until it is edited.
/// </summary>
public class RestoredContextIdentityTests
{
    private const string ScriptPath = TestPaths.ModsRoot + @"\zm_grief\maps\_util.gsc";

    private static async Task<ScriptDatabase> RunSessionAsync(FakeFileSystem files, SqliteCache cache, RootConfig config)
    {
        PathResolver resolver = new(config, files);
        ScriptDatabase database = new();
        WorkspaceIndexer indexer = new(database, () => resolver, files, new NameTable());

        indexer.UseCache(cache, cache.LoadAll());
        await indexer.IndexAsync(IndexingMode.Full, NullIndexProgressListener.Instance, CancellationToken.None);
        await cache.WaitForIdleAsync(CancellationToken.None);

        return database;
    }

    [Fact]
    public async Task ARestoredFile_UsesTheCurrentSessionsContextNotTheCachedOne()
    {
        string dbPath = Path.Combine(Path.GetTempPath(), $"gscode-restored-context-test-{Guid.NewGuid():N}.db");
        SqliteCache cache = SqliteCache.Open(dbPath, "test-identity");
        try
        {
            FakeFileSystem files = new FakeFileSystem()
                .AddFile(TestPaths.Raw(@"scripts\shared\placeholder.gsh"), "")
                .AddFile(ScriptPath, "function util()\n{\n}\n");

            // Session 1: raw/mods detection is off entirely, so this file resolves as an ordinary
            // open workspace folder rather than a mod — the "mods\<name>" shape of its path is not
            // even looked at.
            RootConfig sessionOneConfig = RootConfig.Create(
                rawEnabled: false, rawPath: null, modsPath: null, [TestPaths.ModsRoot + @"\zm_grief"], files);
            ScriptDatabase firstSession = await RunSessionAsync(files, cache, sessionOneConfig);

            firstSession.Gsc.TryGet(PathUtil.NormalizeAbsolute(ScriptPath), out ScriptRecord firstRecord);
            Assert.NotEqual("mod:zm_grief", firstRecord.ContextId);

            // Session 2: the mods root is now configured (e.g. a setting changed), so the SAME file,
            // with the SAME bytes, is reclassified as belonging to mod "zm_grief" — but it restores
            // from cache since its content hash is unchanged.
            RootConfig sessionTwoConfig = TestPaths.Config(files);
            ScriptDatabase secondSession = await RunSessionAsync(files, cache, sessionTwoConfig);

            secondSession.Gsc.TryGet(PathUtil.NormalizeAbsolute(ScriptPath), out ScriptRecord secondRecord);

            Assert.Equal("mod:zm_grief", secondRecord.ContextId);
            Assert.Equal(@"maps\_util.gsc", secondRecord.RelativePath);
        }
        finally
        {
            await cache.DisposeAsync();
            SqliteCache.DeleteDatabase(dbPath);
        }
    }
}
