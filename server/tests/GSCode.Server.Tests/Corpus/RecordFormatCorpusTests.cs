using System.Text.Json;
using GSCode.Core;
using GSCode.Workspace.Cache;
using GSCode.Workspace.Database;
using GSCode.Workspace.Indexing;
using GSCode.Workspace.Resolution;
using Xunit;
using Xunit.Abstractions;

namespace GSCode.Server.Tests.Corpus;

/// <summary>
/// Every record a real index produces must come back from the cache exactly as it went in.
///
/// The binary layout names fields by position, so a field it drops or misreads does not fail — the
/// record restores, a little wrong, and every warm start after that serves the wrong answer. The
/// unit tests pin one hand-built record with every field set; this pins the shapes real scripts
/// actually produce, over both dialect families. Equality is judged on the JSON of each record,
/// since JSON writes every property and the record types' own equality compares arrays by reference.
/// </summary>
[Trait("Category", "Corpus")]
[Collection(GameProfileCollection.Name)]
public class RecordFormatCorpusTests
{
    private readonly ITestOutputHelper _output;

    public RecordFormatCorpusTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public async Task EveryIndexedRecord_SurvivesTheCacheUnchanged()
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

            List<ScriptRecord> records = [.. database.AllRecords, .. database.AllGshRecords];
            List<string> mismatched = [];
            long blobBytes = 0;

            foreach ( ScriptRecord record in records )
            {
                byte[] blob = RecordSerializer.Serialize(record);
                blobBytes += blob.Length;

                ScriptRecord? restored = RecordSerializer.Deserialize(blob);
                if ( restored is null || JsonSerializer.Serialize(record) != JsonSerializer.Serialize(restored) )
                {
                    mismatched.Add(record.Path);
                }
            }

            _output.WriteLine(
                $"{profile.ShortName}: {records.Count} records, {mismatched.Count} changed by the round trip, "
                + $"{blobBytes / 1048576.0:F1} MB of blobs");

            Assert.True(records.Count > 0, "The index produced no records, so nothing was checked.");
            Assert.Empty(mismatched);
        }
        finally
        {
            GameProfile.Select(previous.ShortName);
        }
    }
}
