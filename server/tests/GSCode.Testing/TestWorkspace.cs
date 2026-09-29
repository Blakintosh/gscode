using GSCode.Core;
using GSCode.Core.Symbols;
using GSCode.Core.Text;
using GSCode.Parser;
using GSCode.Parser.Preprocessing;
using GSCode.Workspace.Database;
using GSCode.Workspace.Indexing;
using GSCode.Workspace.Resolution;

namespace GSCode.Testing;

/// <summary>
/// An indexed in-memory workspace for ONE dialect: files under <see cref="TestPaths.RawRoot"/>, a
/// resolver over them, and a store holding every one of them parsed as that game.
///
/// It exists because getting this wrong is silent. <see cref="WorkspaceIndexer"/> defers to
/// <see cref="GameProfile.Active"/> when given no profile, and Active is BO3 in a test run — under
/// which a keyword-less <c>is_coop()</c> is not a declaration at all. The store then comes back
/// empty, every "is it offered?" assertion fails for a reason that looks like the thing under test,
/// and every "is it absent?" assertion passes without proving anything. So the one profile goes to
/// the indexer, to <see cref="Analyze(string)"/>, AND to Active for as long as the workspace lives, since
/// the queries a test runs afterwards read Active for themselves. Dispose it.
/// </summary>
public sealed class TestWorkspace : IDisposable
{
    private readonly ProfileScope _profileScope;

    private TestWorkspace(ProfileScope profileScope, GameProfile profile, FakeFileSystem files)
    {
        _profileScope = profileScope;
        Profile = profile;
        Files = files;

        Resolver = new PathResolver(TestPaths.Config(files), files);
        Database = new ScriptDatabase();
    }

    public GameProfile Profile { get; }

    public FakeFileSystem Files { get; }

    public PathResolver Resolver { get; }

    public ScriptDatabase Database { get; }

    /// <summary>
    /// Indexes <paramref name="files"/> under <paramref name="profile"/>, or
    /// <see cref="ProfileScope.Default"/> when the game is not what the test is about. Partial mode
    /// is what startup runs: parse-level diagnostics only, no cross-file lints stored.
    /// </summary>
    public static TestWorkspace Build(
        IEnumerable<TestFile> files, GameProfile? profile = null, IndexingMode mode = IndexingMode.Full)
    {
        FakeFileSystem fileSystem = new();
        foreach ( TestFile file in files )
        {
            fileSystem.AddFile(TestPaths.Raw(file.RelativePath), file.Text);
        }

        return Build(fileSystem, profile, mode);
    }

    /// <summary>
    /// The same over a tree the test has already filled, for the suites whose facts each add their
    /// own files to a <see cref="FakeFileSystem"/> before asking.
    /// </summary>
    public static TestWorkspace Build(
        FakeFileSystem fileSystem, GameProfile? profile = null, IndexingMode mode = IndexingMode.Full)
    {
        GameProfile chosen = profile ?? ProfileScope.Default;
        ProfileScope scope = ProfileScope.Use(chosen);
        try
        {
            TestWorkspace workspace = new(scope, chosen, fileSystem);
            WorkspaceIndexer indexer = new(
                workspace.Database, () => workspace.Resolver, fileSystem, new NameTable(), profile: chosen);
            indexer.IndexAsync(mode, NullIndexProgressListener.Instance, CancellationToken.None)
                .GetAwaiter().GetResult();
            return workspace;
        }
        catch
        {
            scope.Dispose();
            throw;
        }
    }

    /// <summary>
    /// A fresh analysis of one of the workspace's scripts under its game, standing in for the file
    /// the editor has open. No insert provider: the tests using this ask about functions, not macros.
    /// </summary>
    public ParseResult Analyze(string relativePath)
    {
        return Analyze(relativePath, Files.ReadAllText(TestPaths.Raw(relativePath)));
    }

    /// <summary>
    /// The same for a script the workspace does NOT hold: a file being written that the index has
    /// not seen, which is a different question from one it has.
    /// </summary>
    public ParseResult Analyze(string relativePath, string text)
    {
        return ScriptAnalysis.Analyze(
            TestPaths.Raw(relativePath), ScriptLanguage.Gsc, SourceText.From(text), NullInsertProvider.Instance,
            new NameTable(), Profile);
    }

    public void Dispose()
    {
        _profileScope.Dispose();
    }
}
