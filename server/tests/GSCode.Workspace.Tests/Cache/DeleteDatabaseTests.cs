using GSCode.Workspace.Cache;
using Xunit;

namespace GSCode.Workspace.Tests.Cache;

/// <summary>
/// Clearing the cache must remove exactly one workspace's database.
///
/// The client used to do this by recursively force-deleting the whole shared cache directory,
/// which discarded every other workspace's cache to reindex one — and computed that directory
/// from an environment variable whose fallback did not cover the empty-string case, so a
/// misconfigured host aimed that recursive delete at a relative path.
/// </summary>
public class DeleteDatabaseTests : IDisposable
{
    private readonly string _directory;

    public DeleteDatabaseTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), "gscode-cache-tests-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(_directory);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch ( IOException )
        {
        }
    }

    private string WriteDatabase(string name)
    {
        string path = Path.Combine(_directory, name + ".db");
        File.WriteAllText(path, "not really sqlite");
        return path;
    }

    [Fact]
    public void DeletesTheNamedDatabase()
    {
        string path = WriteDatabase("aaaa1111");

        Assert.True(SqliteCache.DeleteDatabase(path));
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void LeavesOtherWorkspacesCachesAlone()
    {
        // The whole point: caches for other workspaces share the directory.
        string mine = WriteDatabase("aaaa1111");
        string theirs = WriteDatabase("bbbb2222");

        SqliteCache.DeleteDatabase(mine);

        Assert.False(File.Exists(mine));
        Assert.True(File.Exists(theirs));
        Assert.True(Directory.Exists(_directory));
    }

    [Fact]
    public void RemovesTheWriteAheadSidecars()
    {
        // SQLite leaves -wal and -shm beside the database; a leftover WAL can resurrect the data
        // the user asked to discard.
        string path = WriteDatabase("aaaa1111");
        File.WriteAllText(path + "-wal", "wal");
        File.WriteAllText(path + "-shm", "shm");

        SqliteCache.DeleteDatabase(path);

        Assert.False(File.Exists(path + "-wal"));
        Assert.False(File.Exists(path + "-shm"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("gscode/cache/aaaa.db")]
    [InlineData(@"gscode\cache\aaaa.db")]
    public void RefusesAnythingNotFullyQualified(string path)
    {
        // A relative path resolves against the process's working directory, which is never where
        // a cache lives. This is the exact shape an empty APPDATA produced.
        Assert.False(SqliteCache.DeleteDatabase(path));
    }

    [Fact]
    public void AnAbsentDatabaseIsNotAnError()
    {
        // Clearing a cache that was never created is a no-op, not a failure.
        Assert.False(SqliteCache.DeleteDatabase(Path.Combine(_directory, "never-existed.db")));
    }
}
