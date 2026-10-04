using System.Collections.Immutable;
using GSCode.Core;
using GSCode.Core.Symbols;
using GSCode.Workspace.Database;
using GSCode.Workspace.Indexing;
using GSCode.Workspace.Resolution;
using Xunit;
using Xunit.Abstractions;

namespace GSCode.Server.Tests.Corpus;

/// <summary>
/// The reference query behind CodeLens, find-references and rename reads only the files that can
/// reach a declaring file (<see cref="DatabaseQueries.FindReferencesReaching"/>) instead of
/// collecting every reference to the key and scoping afterwards. It has to return exactly what the
/// old two steps did, so this asks both for every function declared anywhere in a real index —
/// the question a CodeLens asks of each one — and requires the same references back.
///
/// The same pass also proves the SAME-FILE narrowing document highlight uses. That question runs
/// the same query with a path, so the thing worth proving is that narrowing it returns exactly the
/// entries filtering the wide answer would have kept — over every declaration a real workspace has,
/// rather than over the handful a fixture can spell.
/// </summary>
[Trait("Category", "Corpus")]
[Collection(GameProfileCollection.Name)]
public class ReferenceScopeCorpusTests
{
    private readonly ITestOutputHelper _output;

    public ReferenceScopeCorpusTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public async Task EveryDeclaration_GetsTheSameReferencesFromEitherPath()
    {
        bool measured = false;

        if ( CorpusFixture.Available )
        {
            await CheckAsync(GameProfile.BlackOps3, CorpusFixture.Resolver);
            measured = true;
        }

        GameCorpus? cod4 = GameCorpusFixture.For(GameProfile.Cod4);
        if ( cod4 is not null )
        {
            GameCorpus captured = cod4;
            await CheckAsync(captured.Profile, () => GameCorpusFixture.Resolver(captured));
            measured = true;
        }

        if ( !measured )
        {
            _output.WriteLine("SKIPPED: neither %GSCODE_CORPUS_BO3% nor %GSCODE_CORPUS_COD4% found.");
        }
    }

    private async Task CheckAsync(GameProfile profile, Func<PathResolver> resolverFactory)
    {
        GameProfile previous = GameProfile.Active;
        try
        {
            GameProfile.Select(profile.ShortName);

            PathResolver resolver = resolverFactory();
            ScriptDatabase database = new();
            WorkspaceIndexer indexer = new(database, () => resolver, new PhysicalFileSystem(), new NameTable());
            await indexer.IndexAsync(IndexingMode.Full, NullIndexProgressListener.Instance, CancellationToken.None);

            int asked = 0;
            int references = 0;
            List<string> differing = [];
            List<string> differingInFile = [];

            foreach ( LanguageStore store in new[] { database.Gsc, database.Csc } )
            {
                ImmutableArray<LanguageStore> stores = [store];

                foreach ( ScriptRecord record in store.AllRecords )
                {
                    if ( record.RelativePath.Length == 0 )
                    {
                        continue;
                    }

                    foreach ( FunctionSymbol function in record.Functions )
                    {
                        SymbolKey key = new(profile.KeyNamespace(function.Namespace), function.KeyName, SymbolKind.Function);

                        ImmutableArray<(ScriptRecord Record, ReferenceEntry Entry)> old = DatabaseQueries.ScopeToIncludeGraph(
                            DatabaseQueries.FindAllReferences(database, stores, record.ContextId, key),
                            record.RelativePath,
                            profile);

                        ImmutableArray<(ScriptRecord Record, ReferenceEntry Entry)> scoped =
                            DatabaseQueries.FindReferencesReaching(stores, record.ContextId, key, record.RelativePath, profile);

                        asked++;
                        references += old.Length;

                        if ( !Describe(old).SequenceEqual(Describe(scoped)) )
                        {
                            differing.Add($"{record.RelativePath} {function.Name}: {old.Length} vs {scoped.Length}");
                        }

                        // The narrowing behind document highlight: asking for one file must equal
                        // asking wide and keeping that file.
                        ImmutableArray<(ScriptRecord Record, ReferenceEntry Entry)> inFile =
                            DatabaseQueries.FindReferencesReaching(
                                stores, record.ContextId, key, record.RelativePath, profile, record.Path);

                        List<string> expectedInFile = Describe(
                            [.. scoped.Where(hit => hit.Record.Path == record.Path)]);

                        if ( !expectedInFile.SequenceEqual(Describe(inFile)) )
                        {
                            differingInFile.Add(
                                $"{record.RelativePath} {function.Name}: {expectedInFile.Count} vs {inFile.Length}");
                        }
                    }
                }
            }

            _output.WriteLine(
                $"{profile.ShortName}: {asked:N0} declarations, {references:N0} references, "
                + $"{differing.Count} differing, {differingInFile.Count} differing same-file");
            foreach ( string line in differing.Concat(differingInFile).Take(20) )
            {
                _output.WriteLine("  " + line);
            }

            Assert.True(asked > 0, "The index produced no declarations, so nothing was compared.");
            Assert.Empty(differing);
            Assert.Empty(differingInFile);
        }
        finally
        {
            GameProfile.Select(previous.ShortName);
        }
    }

    /// <summary>Order-free: the two paths visit files in different orders, and callers sort or count.</summary>
    private static List<string> Describe(ImmutableArray<(ScriptRecord Record, ReferenceEntry Entry)> found)
    {
        List<string> lines = [];
        foreach ( (ScriptRecord Record, ReferenceEntry Entry) item in found )
        {
            lines.Add($"{item.Record.Path}|{item.Entry.Range}|{item.Entry.Kind}|{item.Entry.FromMacro}");
        }

        lines.Sort(StringComparer.Ordinal);
        return lines;
    }
}
