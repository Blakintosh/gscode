using GSCode.Server.Handlers;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;

namespace GSCode.Server.Tests.Handlers;

/// <summary>
/// Keeps every publish, in order, for a test that asserts on what reached the client. Safe to send
/// to from the refresh fan-out's worker threads; read <see cref="Sent"/> once the work under test
/// has finished.
/// </summary>
internal sealed class RecordingDiagnosticsSink : IDiagnosticsSink
{
    private readonly object _gate = new();

    public List<PublishDiagnosticsParams> Sent { get; } = [];

    public void Send(PublishDiagnosticsParams parameters)
    {
        lock ( _gate )
        {
            Sent.Add(parameters);
        }
    }
}

/// <summary>Drops every publish, for a test that needs a publisher but asserts on something else.</summary>
internal sealed class DiscardingDiagnosticsSink : IDiagnosticsSink
{
    public static DiscardingDiagnosticsSink Instance { get; } = new();

    public void Send(PublishDiagnosticsParams parameters)
    {
    }
}
