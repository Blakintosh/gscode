using System.Collections.Immutable;
using GSCode.Core.Diagnostics;
using GSCode.Core.Paths;
using GSCode.Core.Text;
using GSCode.Server.Handlers;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using Xunit;
using Diagnostic = GSCode.Core.Diagnostics.Diagnostic;
using DiagnosticSeverity = GSCode.Core.Diagnostics.DiagnosticSeverity;
using Position = GSCode.Core.Text.Position;

namespace GSCode.Server.Tests.Handlers;

/// <summary>
/// What is on screen is the newest analysis, whatever order the analyses finish in.
///
/// Analyses do not complete in the order they started — the debounced pass, the save path and a
/// request thread's freshen can all be running at once, and a slow one can land after a fast one
/// that was started later. OpenDocument.Publish's version CAS decides which PARSE
/// stands, but nothing stopped the losing caller from pushing its (correctly stamped, older) set
/// to the client afterwards, which is what put diagnostics for text the user had already replaced
/// back in front of them.
/// </summary>
public class DiagnosticsPublishOrderTests
{
    private sealed class RecordingSink : IDiagnosticsSink
    {
        public List<PublishDiagnosticsParams> Sent { get; } = [];

        public void Send(PublishDiagnosticsParams parameters)
        {
            Sent.Add(parameters);
        }
    }

    private static readonly string Path = PathUtil.NormalizeAbsolute(@"G:\Games\Black Ops\raw\maps\_menus.gsc");

    private static ImmutableArray<Diagnostic> Diagnostics(int count)
    {
        ImmutableArray<Diagnostic>.Builder builder = ImmutableArray.CreateBuilder<Diagnostic>(count);

        for ( int index = 0; index < count; index++ )
        {
            builder.Add(new Diagnostic(
                new TextRange(new Position(index, 0), new Position(index, 1)),
                DiagnosticSeverity.Warning,
                GscDiagnosticCode.UnreachableCode,
                "unreachable"));
        }

        return builder.MoveToImmutable();
    }

    [Fact]
    public void TheNewerSetIsWhatStaysOnScreen()
    {
        // The reported shape, from the user's own log: v53 and v54 analysed back to back, then a
        // slower run stamped v38 finishing last and putting six problems back where five stood.
        RecordingSink sink = new();
        DiagnosticsPublisher publisher = new(sink);

        publisher.Publish(Path, version: 53, Diagnostics(6));
        publisher.Publish(Path, version: 54, Diagnostics(5));
        publisher.Publish(Path, version: 38, Diagnostics(6));

        Assert.Equal(54, sink.Sent[^1].Version);
        Assert.Equal(5, sink.Sent[^1].Diagnostics.Count());
    }

    [Fact]
    public void AnOlderVersionArrivingLateIsDropped()
    {
        RecordingSink sink = new();
        DiagnosticsPublisher publisher = new(sink);

        publisher.Publish(Path, version: 53, Diagnostics(6));
        publisher.Publish(Path, version: 54, Diagnostics(5));
        publisher.Publish(Path, version: 38, Diagnostics(6));

        Assert.Equal(2, sink.Sent.Count);
    }

    [Fact]
    public void AnEqualVersionRepublishIsAllowed()
    {
        // Equal must PASS, not be treated as stale. The dependent refresher republishes the same
        // version with a RICHER set once a neighbour's exports move, and that is the whole reason
        // it exists — muting it would reintroduce the bug it was written to fix.
        RecordingSink sink = new();
        DiagnosticsPublisher publisher = new(sink);

        publisher.Publish(Path, version: 54, Diagnostics(5));
        publisher.Publish(Path, version: 54, Diagnostics(7));

        Assert.Equal(2, sink.Sent.Count);
        Assert.Equal(7, sink.Sent[^1].Diagnostics.Count());
    }

    [Fact]
    public void AClearResetsTheLedgerSoAReopenedDocumentPublishesAgain()
    {
        // A reopened document starts again at version 0 or 1. Remembering that v68 was once on
        // screen would silence it for the rest of the session.
        RecordingSink sink = new();
        DiagnosticsPublisher publisher = new(sink);

        publisher.Publish(Path, version: 68, Diagnostics(6));
        publisher.Clear(Path);
        publisher.Publish(Path, version: 1, Diagnostics(2));

        Assert.Equal(3, sink.Sent.Count);
        Assert.Equal(1, sink.Sent[^1].Version);
    }

    [Fact]
    public void AWorkspacePublishWithNoVersionAlwaysGoesThrough()
    {
        // The workspace publisher speaks for CLOSED files, where a document version means nothing.
        RecordingSink sink = new();
        DiagnosticsPublisher publisher = new(sink);

        publisher.Publish(Path, version: null, Diagnostics(3));
        publisher.Publish(Path, version: null, Diagnostics(4));

        Assert.Equal(2, sink.Sent.Count);
    }

    [Fact]
    public void ANullVersionDoesNotMuteALaterRealOne()
    {
        // Nor is it muted BY one: a versionless set must neither join the ordering nor block it.
        RecordingSink sink = new();
        DiagnosticsPublisher publisher = new(sink);

        publisher.Publish(Path, version: 9, Diagnostics(3));
        publisher.Publish(Path, version: null, Diagnostics(4));
        publisher.Publish(Path, version: 10, Diagnostics(5));

        Assert.Equal(3, sink.Sent.Count);
        Assert.Equal(10, sink.Sent[^1].Version);
    }

    [Fact]
    public void TwoDocumentsDoNotShareALedger()
    {
        RecordingSink sink = new();
        DiagnosticsPublisher publisher = new(sink);
        string other = PathUtil.NormalizeAbsolute(@"G:\Games\Black Ops\raw\maps\_utility.gsc");

        publisher.Publish(Path, version: 54, Diagnostics(5));
        publisher.Publish(other, version: 2, Diagnostics(1));

        Assert.Equal(2, sink.Sent.Count);
    }
}
