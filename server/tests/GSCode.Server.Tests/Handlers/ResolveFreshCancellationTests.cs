using GSCode.Core;
using GSCode.Parser;
using GSCode.Parser.Preprocessing;
using GSCode.Server.Configuration;
using GSCode.Server.Handlers;
using GSCode.Workspace.Database;
using GSCode.Workspace.Documents;
using GSCode.Workspace.Resolution;
using OmniSharp.Extensions.LanguageServer.Protocol;
using Xunit;

namespace GSCode.Server.Tests.Handlers;

/// <summary>
/// The freshening resolve is cancellable.
///
/// <c>ResolveFresh</c> runs a full lex, preprocess, parse and extract on the REQUEST thread, and
/// every caller of it — completion, signature help, code lens, inlay hints, formatting — is a read
/// path with no debounce in front of it. It took no token at all, so an analysis started for a
/// request the client had already cancelled ran to the end regardless, on a file the user was still
/// typing into.
/// </summary>
public class ResolveFreshCancellationTests
{
    private const string Path = @"C:\bo3\share\raw\scripts\shared\resolve_fresh.gsc";
    private const string Source = "#namespace resolve;\nfunction main()\n{\n    a = 1;\n}\n";

    private static NavigationSupport BuildSupport(out DocumentStore documents)
    {
        documents = new DocumentStore(static _ => NullInsertProvider.Instance, new NameTable());
        return new NavigationSupport(documents, new ScriptDatabase(), new ResolverHolder(new PhysicalFileSystem()));
    }

    private static DocumentUri Uri => DocumentUri.FromFileSystemPath(Path);

    [Fact]
    public async Task ACancelledRequestDoesNotAnalyseAStaleDocument()
    {
        NavigationSupport support = BuildSupport(out DocumentStore documents);
        OpenDocument document = documents.Open(Path, Source, version: 1);
        documents.AnalyzeIfStale(document);

        // The text moves on, so the next resolve has real work to do rather than handing back the
        // published snapshot.
        documents.ApplyChange(document, range: null, Source + "\nfunction second()\n{\n}\n", version: 2);

        using CancellationTokenSource source = new();
        await source.CancelAsync();

        Assert.Throws<OperationCanceledException>(() => support.ResolveFresh(Uri, source.Token));
    }

    [Fact]
    public void AnUncancelledRequestStillFreshens()
    {
        // The control, so the assertion above cannot pass by the resolve simply never working.
        NavigationSupport support = BuildSupport(out DocumentStore documents);
        OpenDocument document = documents.Open(Path, Source, version: 1);
        documents.AnalyzeIfStale(document);

        documents.ApplyChange(document, range: null, Source + "\nfunction second()\n{\n}\n", version: 2);

        NavigationTarget? target = support.ResolveFresh(Uri, CancellationToken.None);

        Assert.NotNull(target);
        Assert.Equal(2, document.AnalyzedVersion);
    }

    [Fact]
    public void AnUnchangedDocumentIsNotReanalysedAtAll()
    {
        // The cached path takes no notice of the token because it does no work: the published
        // snapshot is handed straight back, which callers assert on by instance.
        NavigationSupport support = BuildSupport(out DocumentStore documents);
        OpenDocument document = documents.Open(Path, Source, version: 1);
        ParseResult first = documents.AnalyzeIfStale(document);

        NavigationTarget? target = support.ResolveFresh(Uri, CancellationToken.None);

        Assert.NotNull(target);
        Assert.Same(first, target.Result);
    }
}
