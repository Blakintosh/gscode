using System.Collections.Immutable;
using GSCode.Core;
using GSCode.Core.Paths;
using GSCode.Workspace.Database;
using GSCode.Workspace.Indexing;
using GSCode.Workspace.Resolution;
using Xunit;

namespace GSCode.Workspace.Tests.Indexing;

/// <summary>
/// The reported bug: the server's startup index runs on a background task while the LSP keeps
/// handling requests, including watched-file changes (Program.cs); <see cref="WorkspaceIndexer.IndexFile"/>
/// — the watcher's own entry point — carries no gate against a concurrent full pass. A file can
/// change again WHILE one call is still lexing/parsing/extracting an earlier version of it, and
/// the watched-file event for that later change can commit before the earlier, slower call
/// reaches its own commit — at which point the slower call's stale write wins the store's plain
/// last-write-wins <c>Upsert</c> and silently clobbers the fresher one.
///
/// Reproduced deterministically, without real thread timing, by hooking the FIRST disk read of a
/// slow call: as a side effect of that read returning, the file is edited on disk and a second,
/// independent, fully synchronous <c>IndexFile</c> call — standing in for the watcher's own call —
/// runs to completion and commits the edit. Control then returns to the slow call, which is still
/// holding the OLD content it read a moment ago.
/// </summary>
public class WatcherRaceTests
{
    private static readonly string ScriptPath = TestPaths.Raw(@"scripts\util.gsc");
    private const string BeforeEdit = "function util()\n{\n}\n";
    private const string AfterEdit = "function util()\n{\n}\nfunction other()\n{\n}\n";

    private static bool Declares(ScriptDatabase database, string functionName)
    {
        return DatabaseQueries.LookupFunctions(database.Gsc, "raw", "", null, functionName).Any();
    }

    [Fact]
    public void ASlowIndexCallLosesToAFasterConcurrentEditOfTheSameFile()
    {
        FakeFileSystem inner = new FakeFileSystem().AddFile(ScriptPath, BeforeEdit);
        RootConfig config = TestPaths.Config(inner);
        PathResolver resolver = new(config, inner);
        ScriptDatabase database = new();

        WorkspaceIndexer? indexer = null;
        RaceFileSystem race = new(
            inner,
            ScriptPath,
            onFirstRead: () =>
            {
                // The concurrent edit — happens strictly between the slow call's read (already
                // captured BeforeEdit) and its eventual commit.
                inner.AddFile(ScriptPath, AfterEdit);

                // The watcher's own, independent, fully synchronous call for that same edit —
                // runs to completion and commits AfterEdit before the slow call resumes.
                indexer!.IndexFile(ScriptPath);
            });

        indexer = new WorkspaceIndexer(database, () => resolver, race, new NameTable());

        // The slow call: its read is intercepted above, so it analyses BeforeEdit while the
        // faster concurrent call (triggered from inside that very read) analyses and commits
        // AfterEdit first.
        indexer.IndexFile(ScriptPath);

        // AfterEdit must win: it is both the true current content of the file AND the fresher
        // commit. A stale write from the slow call clobbering it back to BeforeEdit is exactly
        // the race this test exists to catch.
        Assert.True(Declares(database, "other"));
    }

    /// <summary>Delegates everything to the same in-memory tree, but fires a hook on the FIRST read of one tracked path.</summary>
    private sealed class RaceFileSystem : IFileSystem
    {
        private readonly FakeFileSystem _inner;
        private readonly string _racedPath;
        private readonly Action _onFirstRead;
        private bool _fired;

        public RaceFileSystem(FakeFileSystem inner, string racedPath, Action onFirstRead)
        {
            _inner = inner;
            _racedPath = PathUtil.NormalizeAbsolute(racedPath);
            _onFirstRead = onFirstRead;
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
            string content = _inner.ReadAllText(absolutePath);

            if ( !_fired && PathUtil.NormalizeAbsolute(absolutePath) == _racedPath )
            {
                _fired = true;
                _onFirstRead();
            }

            return content;
        }

        public DateTime GetLastWriteTimeUtc(string absolutePath)
        {
            return _inner.GetLastWriteTimeUtc(absolutePath);
        }

        public IEnumerable<string> EnumerateFilesWithExtensions(string directory, ImmutableArray<string> extensions)
        {
            return _inner.EnumerateFilesWithExtensions(directory, extensions);
        }
    }
}
