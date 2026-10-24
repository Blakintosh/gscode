using System.Collections.Immutable;
using GSCode.Core;
using GSCode.Core.Symbols;
using GSCode.Parser.Extraction;
using GSCode.Workspace.Database;
using GSCode.Workspace.Indexing;
using GSCode.Workspace.Resolution;
using Xunit;
using Xunit.Abstractions;

namespace GSCode.Server.Tests.Corpus;

/// <summary>
/// A path call names its file outright: <c>animscripts\cover_prone::main()</c> means cover_prone's
/// <c>main</c>, even in combat.gsc, which declares a <c>main</c> of its own. Every path call in the
/// cod4 scripts is resolved here the way hover resolves it, and the declaration found has to be the
/// one the written file declares ITSELF — a far call never sees what that file includes. A call into
/// a file that does not declare the name resolves to nothing, and is allowed
/// only in a script nothing loads (<see cref="UnloadedStockScripts"/>).
///
/// Under <c>#include</c> every same-named function shares one key, so nothing in the key itself
/// says which file a call means; only the written path does. The corpus is what makes this worth
/// checking per call rather than in a fixture: the shipped animscripts path-call each other's
/// <c>main</c> from files that declare their own.
/// </summary>
[Trait("Category", "Corpus")]
[Collection(GameProfileCollection.Name)]
public class PathCallResolutionCorpusTests
{
    private readonly ITestOutputHelper _output;

    public PathCallResolutionCorpusTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public async Task EveryPathCallResolvesToTheFileItNames()
    {
        GameCorpus? cod4 = GameCorpusFixture.For(GameProfile.Cod4);
        if ( cod4 is null )
        {
            _output.WriteLine("SKIPPED: %GSCODE_CORPUS_COD4% not found.");
            return;
        }

        GameProfile previous = GameProfile.Active;
        try
        {
            GameProfile.Select(cod4.Profile.ShortName);
            PathResolver resolver = GameCorpusFixture.Resolver(cod4);
            ScriptDatabase database = new();
            WorkspaceIndexer indexer = new(database, () => resolver, new PhysicalFileSystem(), new NameTable());
            await indexer.IndexAsync(IndexingMode.Full, NullIndexProgressListener.Instance, CancellationToken.None);

            int checkedCalls = 0;
            int unindexedTargets = 0;
            int wrongWithoutPath = 0;
            List<string> wrong = [];
            foreach ( ScriptRecord record in database.Gsc.AllRecords )
            {
                foreach ( PathCallReference pathCall in record.PathCallTargets )
                {
                    ReferenceEntry? call = CallAt(record, pathCall);
                    if ( call is not ReferenceEntry entry )
                    {
                        continue;
                    }

                    string written = RelativePathIndex.Normalize(pathCall.Path);
                    if ( database.Gsc.FilesAt(written).IsEmpty )
                    {
                        unindexedTargets++;
                        continue;
                    }

                    checkedCalls++;
                    bool declaredThere = Declares(database.Gsc, written, entry.Key.Name);
                    ImmutableArray<ResolvedFunction> resolved = MethodResolution.ResolveCall(
                        database.Gsc, record.ContextId, record.Path, entry.Key, entry.Kind, pathCallTarget: pathCall.Path);
                    bool right = declaredThere
                        ? !resolved.IsEmpty && RelativePathIndex.Normalize(resolved[0].Record.RelativePath) == written
                        : resolved.IsEmpty && UnloadedStockScripts.Contains(cod4.Profile, record.RelativePath);
                    if ( !right )
                    {
                        string landed = resolved.IsEmpty ? "nothing" : resolved[0].Record.RelativePath;
                        wrong.Add($"{record.RelativePath}: {pathCall.Path}::{entry.Key.Name} -> {landed}");
                    }

                    // What the resolution gave before the written path reached it, for the record.
                    ImmutableArray<ResolvedFunction> unpinned = MethodResolution.ResolveCall(
                        database.Gsc, record.ContextId, record.Path, entry.Key, entry.Kind);
                    if ( unpinned.IsEmpty || RelativePathIndex.Normalize(unpinned[0].Record.RelativePath) != written )
                    {
                        wrongWithoutPath++;
                    }
                }
            }

            _output.WriteLine(
                $"cod4: {checkedCalls:N0} path calls checked, {unindexedTargets:N0} naming a file the index lacks; "
                + $"{wrongWithoutPath:N0} would resolve elsewhere without the written path");
            foreach ( string line in wrong.Take(20) )
            {
                _output.WriteLine("  WRONG " + line);
            }

            Assert.True(checkedCalls > 0, "no path calls were checked");
            Assert.Empty(wrong);
        }
        finally
        {
            GameProfile.Select(previous.ShortName);
        }
    }

    /// <summary>Whether a file at the written path declares the function itself.</summary>
    private static bool Declares(LanguageStore store, string relative, string keyName)
    {
        foreach ( string path in store.FilesAt(relative) )
        {
            if ( !store.TryGet(path, out ScriptRecord record) )
            {
                continue;
            }

            foreach ( FunctionSymbol function in record.Functions )
            {
                if ( function.KeyName == keyName )
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static ReferenceEntry? CallAt(ScriptRecord record, PathCallReference pathCall)
    {
        foreach ( ReferenceEntry entry in record.References )
        {
            if ( entry.Range == pathCall.NameRange && entry.Key.Kind == SymbolKind.Function
                && entry.Kind is ReferenceKind.Call or ReferenceKind.AddressOf )
            {
                return entry;
            }
        }

        return null;
    }
}
