using GSCode.Parser;
using GSCode.Server.Handlers;
using GSCode.Workspace.Documents;
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
    private const string RelativePath = @"scripts\shared\resolve_fresh.gsc";
    private const string Source = "#namespace resolve;\nfunction main()\n{\n    a = 1;\n}\n";

    private static DocumentUri Uri => HandlerWorkspace.Identify(RelativePath).Uri;

    [Fact]
    public async Task ACancelledRequestDoesNotAnalyseAStaleDocument()
    {
        using HandlerWorkspace workspace = await HandlerWorkspace.BuildAsync([]);
        OpenDocument document = workspace.Open(RelativePath, Source);

        // The text moves on, so the next resolve has real work to do rather than handing back the
        // published snapshot.
        workspace.Documents.ApplyChange(document, range: null, Source + "\nfunction second()\n{\n}\n", version: 2);

        using CancellationTokenSource source = new();
        await source.CancelAsync();

        Assert.Throws<OperationCanceledException>(() => workspace.Navigation.ResolveFresh(Uri, source.Token));
    }

    [Fact]
    public async Task AnUncancelledRequestStillFreshens()
    {
        // The control, so the assertion above cannot pass by the resolve simply never working.
        using HandlerWorkspace workspace = await HandlerWorkspace.BuildAsync([]);
        OpenDocument document = workspace.Open(RelativePath, Source);

        workspace.Documents.ApplyChange(document, range: null, Source + "\nfunction second()\n{\n}\n", version: 2);

        NavigationTarget? target = workspace.Navigation.ResolveFresh(Uri, CancellationToken.None);

        Assert.NotNull(target);
        Assert.Equal(2, document.Analysis?.Version);
    }

    [Fact]
    public async Task AnUnchangedDocumentIsNotReanalysedAtAll()
    {
        // The cached path takes no notice of the token because it does no work: the published
        // snapshot is handed straight back, which callers assert on by instance.
        using HandlerWorkspace workspace = await HandlerWorkspace.BuildAsync([]);
        OpenDocument document = workspace.Open(RelativePath, Source);
        ParseResult first = workspace.Documents.AnalyzeIfStale(document);

        NavigationTarget? target = workspace.Navigation.ResolveFresh(Uri, CancellationToken.None);

        Assert.NotNull(target);
        Assert.Same(first, target.Result);
    }
}
