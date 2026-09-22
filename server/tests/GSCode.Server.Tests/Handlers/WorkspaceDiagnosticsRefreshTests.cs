using System.Collections.Immutable;
using GSCode.Core;
using GSCode.Core.Diagnostics;
using GSCode.Core.Symbols;
using GSCode.Core.Text;
using GSCode.Parser.Preprocessing;
using GSCode.Server.Configuration;
using GSCode.Server.Handlers;
using GSCode.Workspace.Database;
using GSCode.Workspace.Documents;
using GSCode.Workspace.Resolution;
using PublishDiagnosticsParams = OmniSharp.Extensions.LanguageServer.Protocol.Models.PublishDiagnosticsParams;
using Xunit;

namespace GSCode.Server.Tests.Handlers;

/// <summary>
/// <see cref="WorkspaceDiagnosticsPublisher.Refresh"/> runs after the startup index, after every
/// scope change, and after every re-lint of an edit's closed dependents. It used to send every
/// in-scope file's diagnostics every time, so re-linting three files in a large workspace sent
/// thousands of notifications for files that had not changed. It now sends a file only when what it
/// would send differs from what it last sent — and still takes back anything no longer reported.
/// </summary>
public class WorkspaceDiagnosticsRefreshTests
{
    private sealed class CountingSink : IDiagnosticsSink
    {
        public List<PublishDiagnosticsParams> Sent { get; } = [];

        public void Send(PublishDiagnosticsParams parameters)
        {
            Sent.Add(parameters);
        }
    }

    private static readonly TextRange SomeRange = TextRange.FromCoordinates(0, 0, 0, 1);

    private static ScriptRecord WithProblem(string path, string message)
    {
        return new ScriptRecord
        {
            Path = path,
            Language = ScriptLanguage.Gsc,
            ContextId = @"workspace:c:\ws",
            ContentHash = 7,
            Diagnostics = [new Diagnostic(SomeRange, DiagnosticSeverity.Warning, GscDiagnosticCode.UnusedLocal, message)],
        };
    }

    private sealed class Fixture
    {
        public ScriptDatabase Database { get; } = new();
        public CountingSink Sink { get; } = new();
        public WorkspaceDiagnosticsPublisher Publisher { get; }

        public Fixture()
        {
            DocumentStore documents = new(static _ => NullInsertProvider.Instance, new NameTable());
            Publisher = new WorkspaceDiagnosticsPublisher(Database, documents, new DiagnosticsPublisher(Sink), new ServerSettings());
        }
    }

    [Fact]
    public void ASecondRefreshWithNothingChanged_SendsNothing()
    {
        Fixture fixture = new();
        ScriptDatabase database = fixture.Database;
        WorkspaceDiagnosticsPublisher publisher = fixture.Publisher;
        CountingSink sink = fixture.Sink;
        database.CommitRecord(WithProblem(@"c:\ws\a.gsc", "a"));
        database.CommitRecord(WithProblem(@"c:\ws\b.gsc", "b"));

        publisher.Refresh();
        Assert.Equal(2, sink.Sent.Count);

        sink.Sent.Clear();
        publisher.Refresh();
        Assert.Empty(sink.Sent);
    }

    [Fact]
    public void AFileWhoseDiagnosticsChanged_IsTheOnlyOneSentAgain()
    {
        Fixture fixture = new();
        ScriptDatabase database = fixture.Database;
        WorkspaceDiagnosticsPublisher publisher = fixture.Publisher;
        CountingSink sink = fixture.Sink;
        database.CommitRecord(WithProblem(@"c:\ws\a.gsc", "a"));
        database.CommitRecord(WithProblem(@"c:\ws\b.gsc", "b"));
        publisher.Refresh();
        sink.Sent.Clear();

        database.SetDiagnostics(
            @"c:\ws\a.gsc", ScriptLanguage.Gsc, 7,
            [new Diagnostic(SomeRange, DiagnosticSeverity.Warning, GscDiagnosticCode.UnusedLocal, "a, changed")]);
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
        CountingSink sink = fixture.Sink;
        database.CommitRecord(WithProblem(@"c:\ws\a.gsc", "a"));
        publisher.Refresh();
        sink.Sent.Clear();

        database.SetDiagnostics(@"c:\ws\a.gsc", ScriptLanguage.Gsc, 7, []);
        publisher.Refresh();

        PublishDiagnosticsParams cleared = Assert.Single(sink.Sent);
        Assert.Empty(cleared.Diagnostics);
    }
}
