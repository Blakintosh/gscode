using System.Collections.Immutable;
using GSCode.Core.Diagnostics;
using GSCode.Core.Paths;
using GSCode.Core.Text;
using GSCode.Server.Handlers;
using OmniSharp.Extensions.LanguageServer.Protocol;
using Xunit;
using Diagnostic = GSCode.Core.Diagnostics.Diagnostic;
using DiagnosticSeverity = GSCode.Core.Diagnostics.DiagnosticSeverity;
using Position = GSCode.Core.Text.Position;

namespace GSCode.Server.Tests.Handlers;

/// <summary>
/// One file, one URI spelling.
///
/// The sync handler published under the CLIENT's URI while the dependent refresher and the
/// workspace publisher used <c>DocumentUri.FromFileSystemPath(document.Path)</c> — and
/// <see cref="PathUtil.NormalizeAbsolute"/> lowercases on Windows. Two spellings are two
/// independent marker sets in the client, neither replacing the other, so a file under a path
/// with any uppercase letter in it showed its problems twice.
/// </summary>
public class DiagnosticsUriTests
{
    private static ImmutableArray<Diagnostic> OneDiagnostic()
    {
        return
        [
            new Diagnostic(
                new TextRange(new Position(0, 0), new Position(0, 1)),
                DiagnosticSeverity.Warning,
                GscDiagnosticCode.UnreachableCode,
                "unreachable"),
        ];
    }

    [Fact]
    public void OneOpenDocumentHasOneUriForPublishAndClear()
    {
        // The reported shape: the edit path and the fan-out path disagreed about the spelling,
        // and close only took back one of the two.
        RecordingDiagnosticsSink sink = new();
        DiagnosticsPublisher publisher = new(sink);

        DocumentUri clientUri = DocumentUri.From(@"G:\Games\Black Ops\raw\maps\_Menus.gsc");
        string path = PathUtil.NormalizeAbsolute(clientUri.GetFileSystemPath());

        publisher.Remember(path, clientUri);
        publisher.Publish(path, version: 1, OneDiagnostic());
        publisher.Clear(path);

        Assert.Equal(2, sink.Sent.Count);
        Assert.Equal(sink.Sent[0].Uri, sink.Sent[1].Uri);
    }

    [Fact]
    public void TheClientsCaseIsPreserved()
    {
        DiagnosticsPublisher publisher = new(new RecordingDiagnosticsSink());

        DocumentUri clientUri = DocumentUri.From(@"G:\Games\Black Ops\raw\maps\_Menus.gsc");
        string path = PathUtil.NormalizeAbsolute(clientUri.GetFileSystemPath());

        publisher.Remember(path, clientUri);

        Assert.Equal(clientUri, publisher.UriFor(path));

        if ( OperatingSystem.IsWindows() )
        {
            // Proves the premise rather than assuming it: on Windows the normalized path really
            // does spell the file differently from what the client sent.
            //
            // Compared as STRINGS deliberately. DocumentUri's own equality ignores case, so the two
            // spellings look identical to anything inside the server — which is most of why this
            // went unnoticed. What the client receives is the serialized text, and the client keys
            // its marker collection off exactly that.
            Assert.NotEqual(DocumentUri.FromFileSystemPath(path).ToString(), clientUri.ToString());
        }
    }

    [Fact]
    public void AFileThatWasNeverOpenedFallsBackToItsPath()
    {
        // The workspace publisher's case: its files are closed by definition, so the on-disk
        // spelling is the only one anybody knows.
        DiagnosticsPublisher publisher = new(new RecordingDiagnosticsSink());
        string path = PathUtil.NormalizeAbsolute(@"G:\Games\Black Ops\raw\maps\_utility.gsc");

        Assert.Equal(DocumentUri.FromFileSystemPath(path), publisher.UriFor(path));
    }

    [Fact]
    public void ClosingForgetsTheUri()
    {
        DiagnosticsPublisher publisher = new(new RecordingDiagnosticsSink());

        DocumentUri clientUri = DocumentUri.From(@"G:\Games\Black Ops\raw\maps\_Menus.gsc");
        string path = PathUtil.NormalizeAbsolute(clientUri.GetFileSystemPath());

        publisher.Remember(path, clientUri);
        publisher.Forget(path);

        Assert.Equal(DocumentUri.FromFileSystemPath(path), publisher.UriFor(path));
    }

    [Fact]
    public void AnUntitledBufferPublishesToItsOwnUri()
    {
        // An untitled buffer's path is synthetic — a name resolved against the process directory.
        // Publishing to a file: URI built from that names a file nothing has open.
        RecordingDiagnosticsSink sink = new();
        DiagnosticsPublisher publisher = new(sink);

        DocumentUri clientUri = DocumentUri.Parse("untitled:Untitled-1.gsc");
        string path = PathUtil.NormalizeAbsolute(clientUri.GetFileSystemPath());

        publisher.Remember(path, clientUri);
        publisher.Publish(path, version: 1, OneDiagnostic());

        Assert.Equal(clientUri, Assert.Single(sink.Sent).Uri);
        Assert.Equal("untitled", sink.Sent[0].Uri.Scheme);
    }
}
