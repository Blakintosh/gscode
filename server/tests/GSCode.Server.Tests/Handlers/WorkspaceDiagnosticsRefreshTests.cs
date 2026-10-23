using GSCode.Core.Diagnostics;
using GSCode.Core.Symbols;
using GSCode.Core.Text;
using GSCode.Server.Configuration;
using GSCode.Server.Handlers;
using GSCode.Workspace.Database;
using GSCode.Workspace.Documents;
using PublishDiagnosticsParams = OmniSharp.Extensions.LanguageServer.Protocol.Models.PublishDiagnosticsParams;
using Xunit;

namespace GSCode.Server.Tests.Handlers;

/// <summary>
/// <see cref="WorkspaceDiagnosticsPublisher.Refresh"/> runs after the startup index, after every
/// scope change, and after every re-lint of an edit's closed dependents. It used to send every
/// in-scope file's diagnostics every time, so re-linting three files in a large workspace sent
/// thousands of notifications for files that had not changed. It now sends a file only when what it
/// would send differs from what it last sent — and still takes back anything no longer reported.
///
/// It also keeps a large workspace from flooding the client: no Hint for a closed file, nothing in
/// full mode until the startup sweep has finished, and a large push paced in the background without
/// losing its order or overwriting a file the editor has just opened.
/// </summary>
public class WorkspaceDiagnosticsRefreshTests
{
    private static readonly TextRange s_someRange = TextRange.FromCoordinates(0, 0, 0, 1);

    private static ScriptRecord WithProblem(string path, string message)
    {
        return TestRecords.At(path, @"workspace:c:\ws") with
        {
            ContentHash = 7,
            Diagnostics = [new Diagnostic(s_someRange, DiagnosticSeverity.Warning, GscDiagnosticCode.UnusedLocal, message)],
        };
    }

    private sealed class Fixture
    {
        public ScriptDatabase Database { get; } = new();
        public RecordingDiagnosticsSink Sink { get; } = new();
        public WorkspaceDiagnosticsPublisher Publisher { get; }

        public Fixture(ServerSettings? settings = null)
        {
            DocumentStore documents = TestDocuments.Standalone();
            Publisher = new WorkspaceDiagnosticsPublisher(
                Database, documents, new DiagnosticsPublisher(Sink), settings ?? new ServerSettings());
        }
    }

    private static List<PublishDiagnosticsParams> SentFor(RecordingDiagnosticsSink sink, string path)
    {
        lock ( sink.Sent )
        {
            return [.. sink.Sent.Where(sent => sent.Uri.ToString().EndsWith(path, StringComparison.Ordinal))];
        }
    }

    [Fact]
    public void ASecondRefreshWithNothingChanged_SendsNothing()
    {
        Fixture fixture = new();
        ScriptDatabase database = fixture.Database;
        WorkspaceDiagnosticsPublisher publisher = fixture.Publisher;
        RecordingDiagnosticsSink sink = fixture.Sink;
        database.CommitRecord(WithProblem(@"c:\ws\a.gsc", "a"));
        database.CommitRecord(WithProblem(@"c:\ws\b.gsc", "b"));

        publisher.Refresh();
        Assert.Equal(2, sink.Sent.Count);

        sink.Sent.Clear();
        publisher.Refresh();
        Assert.Empty(sink.Sent);
    }

    [Fact]
    public void AFileReindexedWithTheSameDiagnostics_IsNotSentAgain()
    {
        // A re-index builds new arrays whether or not anything in them moved: a branch switch that
        // rewrote 5,000 bodies sent 3,561 files' unchanged sets until they were compared by value.
        Fixture fixture = new();
        fixture.Database.CommitRecord(WithProblem(@"c:\ws\a.gsc", "a"));
        fixture.Publisher.Refresh();
        Assert.Single(fixture.Sink.Sent);

        fixture.Sink.Sent.Clear();
        fixture.Database.CommitRecord(WithProblem(@"c:\ws\a.gsc", "a"));
        fixture.Publisher.Refresh();
        Assert.Empty(fixture.Sink.Sent);

        // And a real change after it still goes out.
        fixture.Database.CommitRecord(WithProblem(@"c:\ws\a.gsc", "changed"));
        fixture.Publisher.Refresh();
        Assert.Single(fixture.Sink.Sent);
    }

    [Fact]
    public void AFileWhoseDiagnosticsChanged_IsTheOnlyOneSentAgain()
    {
        Fixture fixture = new();
        ScriptDatabase database = fixture.Database;
        WorkspaceDiagnosticsPublisher publisher = fixture.Publisher;
        RecordingDiagnosticsSink sink = fixture.Sink;
        database.CommitRecord(WithProblem(@"c:\ws\a.gsc", "a"));
        database.CommitRecord(WithProblem(@"c:\ws\b.gsc", "b"));
        publisher.Refresh();
        sink.Sent.Clear();

        database.SetDiagnostics(
            @"c:\ws\a.gsc", ScriptLanguage.Gsc, 7,
            [new Diagnostic(s_someRange, DiagnosticSeverity.Warning, GscDiagnosticCode.UnusedLocal, "a, changed")]);
        publisher.Refresh();

        PublishDiagnosticsParams sent = Assert.Single(sink.Sent);
        Assert.EndsWith("a.gsc", sent.Uri.ToString());
    }

    [Fact]
    public void AFileThatStoppedReportingProblems_IsStillTakenBack()
    {
        Fixture fixture = new();
        ScriptDatabase database = fixture.Database;
        WorkspaceDiagnosticsPublisher publisher = fixture.Publisher;
        RecordingDiagnosticsSink sink = fixture.Sink;
        database.CommitRecord(WithProblem(@"c:\ws\a.gsc", "a"));
        publisher.Refresh();
        sink.Sent.Clear();

        database.SetDiagnostics(@"c:\ws\a.gsc", ScriptLanguage.Gsc, 7, []);
        publisher.Refresh();

        PublishDiagnosticsParams cleared = Assert.Single(sink.Sent);
        Assert.Empty(cleared.Diagnostics);
    }

    [Fact]
    public void AClosedFilesHints_AreNotSent_AndAFileWithOnlyHints_IsNotSentAtAll()
    {
        // VS Code leaves a Hint out of the Problems panel and shows it only in an open editor, which
        // the sync handler publishes for itself.
        Fixture fixture = new();
        fixture.Database.CommitRecord(TestRecords.At(@"c:\ws\mixed.gsc", @"workspace:c:\ws") with
        {
            Diagnostics =
            [
                new Diagnostic(s_someRange, DiagnosticSeverity.Warning, GscDiagnosticCode.UnusedLocal, "shown"),
                new Diagnostic(s_someRange, DiagnosticSeverity.Hint, GscDiagnosticCode.UnusedLocal, "hidden"),
            ],
        });
        fixture.Database.CommitRecord(TestRecords.At(@"c:\ws\hints.gsc", @"workspace:c:\ws") with
        {
            Diagnostics = [new Diagnostic(s_someRange, DiagnosticSeverity.Hint, GscDiagnosticCode.UnusedLocal, "hidden")],
        });

        fixture.Publisher.Refresh();

        PublishDiagnosticsParams sent = Assert.Single(fixture.Sink.Sent);
        Assert.EndsWith("mixed.gsc", sent.Uri.ToString());
        Assert.Equal("shown", Assert.Single(sent.Diagnostics).Message);
    }

    [Fact]
    public void AFullModeRefresh_BeforeTheStartupSweepHasFinished_SendsNothing()
    {
        // Every record's set is about to be replaced by the sweep, which refreshes when it is done.
        Fixture fixture = new(new ServerSettings { WorkspaceIndexingMode = "full" });
        fixture.Database.CommitRecord(WithProblem(@"c:\ws\a.gsc", "a"));

        fixture.Publisher.Refresh();
        Assert.Empty(fixture.Sink.Sent);

        fixture.Database.MarkLintSweepComplete();
        fixture.Publisher.Refresh();
        Assert.Single(fixture.Sink.Sent);
    }

    [Fact]
    public async Task ALargeRefresh_SendsEveryFileOnce_AndALaterChangeArrivesLast()
    {
        Fixture fixture = new();
        for ( int index = 0; index < 600; index++ )
        {
            fixture.Database.CommitRecord(WithProblem($@"c:\ws\f{index:D3}.gsc", "first"));
        }

        fixture.Publisher.Refresh();

        // Decided while the push is still going out, so it has to queue behind it, not overtake it.
        fixture.Database.SetDiagnostics(
            @"c:\ws\f599.gsc", ScriptLanguage.Gsc, 7,
            [new Diagnostic(s_someRange, DiagnosticSeverity.Warning, GscDiagnosticCode.UnusedLocal, "second")]);
        fixture.Publisher.Refresh();

        await fixture.Publisher.WhenSentAsync();

        Assert.Equal(601, fixture.Sink.Sent.Count);
        Assert.Equal(600, fixture.Sink.Sent.Select(static sent => sent.Uri.ToString()).Distinct().Count());
        List<PublishDiagnosticsParams> last = SentFor(fixture.Sink, "f599.gsc");
        Assert.Equal(["first", "second"], last.Select(static sent => Assert.Single(sent.Diagnostics).Message));
    }

    [Fact]
    public async Task OpeningAFile_DuringALargePush_LeavesTheTakeBackAsItsLastMessage()
    {
        // The sync handler publishes an open file's richer set; a queued send landing after it would
        // replace that set with this one.
        Fixture fixture = new();
        for ( int index = 0; index < 600; index++ )
        {
            fixture.Database.CommitRecord(WithProblem($@"c:\ws\f{index:D3}.gsc", "closed"));
        }

        fixture.Publisher.Refresh();
        fixture.Publisher.OnDocumentOpened(@"c:\ws\f599.gsc");
        await fixture.Publisher.WhenSentAsync();

        PublishDiagnosticsParams last = SentFor(fixture.Sink, "f599.gsc")[^1];
        Assert.Empty(last.Diagnostics);
    }
}
