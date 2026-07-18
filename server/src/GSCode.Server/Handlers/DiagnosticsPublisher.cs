using System.Collections.Immutable;
using GSCode.Server.Mapping;
using OmniSharp.Extensions.LanguageServer.Protocol;
using OmniSharp.Extensions.LanguageServer.Protocol.Document;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using OmniSharp.Extensions.LanguageServer.Protocol.Server;

namespace GSCode.Server.Handlers;

/// <summary>Pushes a document's diagnostics to the client (the push model, per design).</summary>
public sealed class DiagnosticsPublisher
{
    private readonly ILanguageServerFacade _server;

    public DiagnosticsPublisher(ILanguageServerFacade server)
    {
        _server = server;
    }

    public void Publish(DocumentUri uri, int? version, ImmutableArray<GSCode.Core.Diagnostics.Diagnostic> diagnostics)
    {
        Container<Diagnostic> converted = new(diagnostics.Select(diagnostic => diagnostic.ToLsp()));

        _server.TextDocument.PublishDiagnostics(new PublishDiagnosticsParams
        {
            Uri = uri,
            Version = version,
            Diagnostics = converted,
        });
    }

    /// <summary>Clears diagnostics when a document closes.</summary>
    public void Clear(DocumentUri uri)
    {
        _server.TextDocument.PublishDiagnostics(new PublishDiagnosticsParams
        {
            Uri = uri,
            Diagnostics = new Container<Diagnostic>(),
        });
    }
}
