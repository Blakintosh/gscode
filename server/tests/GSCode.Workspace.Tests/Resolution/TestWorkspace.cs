using GSCode.Core;
using GSCode.Parser;
using GSCode.Workspace.Database;
using GSCode.Workspace.Indexing;
using GSCode.Workspace.Resolution;

namespace GSCode.Workspace.Tests.Resolution;

/// <summary>
/// An indexed in-memory workspace for ONE dialect: files, a resolver over them, and a store holding
/// every one of them parsed as that game.
///
/// It exists because getting this wrong is silent. <see cref="WorkspaceIndexer"/> defers to
/// <see cref="GameProfile.Active"/> when given no profile, and Active is BO3 in a test run — under
/// which a keyword-less <c>is_coop()</c> is not a declaration at all. The store then comes back
/// empty, every "is it offered?" assertion fails for a reason that looks like the thing under test,
/// and every "is it absent?" assertion passes without proving anything. Two test files had already
/// worked around it by building records by hand, which is the workaround this replaces.
/// </summary>
public static class TestWorkspace
{
    /// <summary>The pieces a query needs: what to ask, and what to ask it about.</summary>
    public sealed record Built(ScriptDatabase Database, PathResolver Resolver, FakeFileSystem Files);

    /// <summary>
    /// Indexes <paramref name="files"/> under <paramref name="profile"/>, rooted at
    /// <paramref name="rawRoot"/>. Paths are normalized on the way in, since a raw spelling reaches
    /// the resolver and comes back with an empty relative path.
    /// </summary>
    public static Built Build(
        GameProfile profile,
        string rawRoot,
        params (string Path, string Text)[] files)
    {
        FakeFileSystem fileSystem = new();
        foreach ( (string path, string text) in files )
        {
            fileSystem.AddFile(path, text);
        }

        RootConfig config = RootConfig.Create(true, rawRoot, null, [], fileSystem);
        PathResolver resolver = new(config, fileSystem);
        ScriptDatabase database = new();

        WorkspaceIndexer indexer = new(database, () => resolver, fileSystem, new NameTable(), profile: profile);
        indexer.IndexAsync(IndexingMode.Full, NullIndexProgressListener.Instance, CancellationToken.None)
            .GetAwaiter().GetResult();

        return new Built(database, resolver, fileSystem);
    }
}
