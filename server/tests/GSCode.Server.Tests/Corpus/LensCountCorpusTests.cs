using GSCode.Core;
using GSCode.Server.Configuration;
using GSCode.Server.Handlers;
using GSCode.Workspace.Database;
using GSCode.Workspace.Documents;
using GSCode.Workspace.Indexing;
using GSCode.Workspace.Resolution;
using OmniSharp.Extensions.LanguageServer.Protocol;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using Xunit;
using Xunit.Abstractions;

namespace GSCode.Server.Tests.Corpus;

/// <summary>
/// A CodeLens count kept by <see cref="LensCountCache"/> is the other files' part of the full query
/// plus the open file's part, counted by the same query narrowed to it. That sum has to be the full
/// count for every lens a real workspace shows — the narrowing is where a disagreement would hide —
/// so this opens every script in a real index and requires the same titles from a handler that keeps
/// nothing, from one filling its counts, and from the same one reading them back.
/// </summary>
[Trait("Category", "Corpus")]
[Collection(GameProfileCollection.Name)]
public class LensCountCorpusTests
{
    private readonly ITestOutputHelper _output;

    public LensCountCorpusTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public async Task EveryLens_CountsTheSameWithTheCacheFilledReadOrOff()
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
            NameTable names = new();
            ScriptDatabase database = new();
            WorkspaceIndexer indexer = new(database, () => resolver, new PhysicalFileSystem(), names);
            await indexer.IndexAsync(IndexingMode.Full, NullIndexProgressListener.Instance, CancellationToken.None);

            DocumentStore documents = new(
                candidate => new ResolverInsertProvider(resolver, resolver.GetContext(candidate), new PhysicalFileSystem(), new InsertCache()),
                names);
            NavigationSupport support = new(documents, database, new ResolverHolder(new PhysicalFileSystem()) { Current = resolver });
            ServerSettings settings = new() { CodeLensEnabled = true };
            TextDocumentSelector selector = TextDocumentSelector.ForLanguage("gsc");

            CodeLensHandler uncached = new(support, settings, selector, LensCountCache.Disabled);
            CodeLensHandler cached = new(support, settings, selector, new LensCountCache());

            int files = 0;
            int lenses = 0;
            List<string> differing = [];

            foreach ( LanguageStore store in new[] { database.Gsc, database.Csc } )
            {
                foreach ( ScriptRecord record in store.AllRecords )
                {
                    string text;
                    try
                    {
                        text = File.ReadAllText(record.Path);
                    }
                    catch ( IOException )
                    {
                        continue;
                    }

                    OpenDocument document = documents.Open(record.Path, text, version: 1);
                    documents.AnalyzeIfStale(document);
                    CodeLensParams request = new() { TextDocument = new TextDocumentIdentifier { Uri = DocumentUri.FromFileSystemPath(record.Path) } };

                    List<string> expected = Titles(await uncached.Handle(request, CancellationToken.None));
                    List<string> filled = Titles(await cached.Handle(request, CancellationToken.None));
                    List<string> read = Titles(await cached.Handle(request, CancellationToken.None));
                    documents.Close(record.Path);

                    files++;
                    lenses += expected.Count;
                    if ( !expected.SequenceEqual(filled) || !expected.SequenceEqual(read) )
                    {
                        differing.Add($"{record.RelativePath}: {Diff(expected, filled)} / {Diff(expected, read)}");
                    }
                }
            }

            _output.WriteLine($"{profile.ShortName}: {files:N0} files, {lenses:N0} lenses, {differing.Count} differing");
            foreach ( string line in differing.Take(20) )
            {
                _output.WriteLine("  " + line);
            }

            Assert.True(lenses > 0, "No lens was produced, so nothing was compared.");
            Assert.Empty(differing);
        }
        finally
        {
            GameProfile.Select(previous.ShortName);
        }
    }

    private static List<string> Titles(CodeLensContainer? container)
    {
        return container is null ? [] : [.. container.Select(static lens => $"{lens.Range.Start.Line}:{lens.Command?.Title}")];
    }

    private static string Diff(List<string> expected, List<string> actual)
    {
        string? first = expected.Zip(actual).Where(static pair => pair.First != pair.Second).Select(static pair => $"{pair.First} vs {pair.Second}").FirstOrDefault();
        return first ?? (expected.Count == actual.Count ? "same" : $"{expected.Count} vs {actual.Count} lenses");
    }
}
