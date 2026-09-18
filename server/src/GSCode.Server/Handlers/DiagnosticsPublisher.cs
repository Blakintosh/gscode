using System.Collections.Concurrent;
using System.Collections.Immutable;
using GSCode.Core.Paths;
using GSCode.Server.Mapping;
using OmniSharp.Extensions.LanguageServer.Protocol;
using OmniSharp.Extensions.LanguageServer.Protocol.Document;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using OmniSharp.Extensions.LanguageServer.Protocol.Server;

namespace GSCode.Server.Handlers;

/// <summary>
/// Where a publish actually goes. One interface with one production implementation, so a test can
/// observe what reached the client: the facade cannot be substituted, and nothing else in this
/// server needed mocking badly enough to take a mocking library on.
/// </summary>
internal interface IDiagnosticsSink
{
    void Send(PublishDiagnosticsParams parameters);
}

/// <summary>The real sink: the LSP connection.</summary>
internal sealed class LanguageServerDiagnosticsSink : IDiagnosticsSink
{
    private readonly ILanguageServerFacade _server;

    public LanguageServerDiagnosticsSink(ILanguageServerFacade server)
    {
        _server = server;
    }

    public void Send(PublishDiagnosticsParams parameters)
    {
        _server.TextDocument.PublishDiagnostics(parameters);
    }
}

/// <summary>
/// Pushes a document's diagnostics to the client.
///
/// Push, because the files this server reports on are mostly CLOSED — a problem in a script nobody
/// has a tab for has no puller to ask for it (see <see cref="WorkspaceDiagnosticsPublisher"/>).
/// OPEN documents are a migration candidate to LSP 3.17 <c>textDocument/diagnostic</c>, which the
/// pinned protocol library and the client both support; that would let the client own the
/// scheduling and bind each answer to the request that asked for it.
///
/// Everything addresses a document by its NORMALIZED path and this class turns that into a URI, so
/// there is exactly one spelling per file on the wire. It used to be two: the sync handler
/// published under the client's URI while the fan-out and the workspace publisher built one from
/// the path, and <see cref="PathUtil.NormalizeAbsolute"/> lowercases on Windows. Two spellings are
/// two independent marker sets in the client, so a file under a path with an uppercase letter in it
/// showed every problem twice and close took back only one of the sets.
/// </summary>
public sealed class DiagnosticsPublisher
{
    private readonly IDiagnosticsSink _sink;

    /// <summary>
    /// The spelling the CLIENT used for each open document, which is the spelling it is told back.
    /// The normalized path is the key everything else in the server uses; it is not necessarily a
    /// URI the client recognises — an <c>untitled:</c> buffer normalizes to a synthetic path whose
    /// <c>file:</c> URI names nothing that exists.
    /// </summary>
    private readonly ConcurrentDictionary<string, DocumentUri> _clientUris = new(StringComparer.Ordinal);

    public DiagnosticsPublisher(ILanguageServerFacade server)
        : this(new LanguageServerDiagnosticsSink(server))
    {
    }

    internal DiagnosticsPublisher(IDiagnosticsSink sink)
    {
        _sink = sink;
    }

    /// <summary>Records how the client spelled a document it just opened.</summary>
    public void Remember(string path, DocumentUri uri)
    {
        _clientUris[PathUtil.NormalizeAbsolute(path)] = uri;
    }

    /// <summary>Drops a closed document's spelling. Call after <see cref="Clear"/>, not before.</summary>
    public void Forget(string path)
    {
        _clientUris.TryRemove(PathUtil.NormalizeAbsolute(path), out _);
    }

    /// <summary>
    /// The one seam from a path to the URI the client is addressed by. A path never opened falls
    /// back to its on-disk spelling, which is right for the workspace publisher: its files are
    /// closed by definition, so the disk is the only spelling anybody has.
    /// </summary>
    public DocumentUri UriFor(string path)
    {
        string key = PathUtil.NormalizeAbsolute(path);

        if ( _clientUris.TryGetValue(key, out DocumentUri? remembered) )
        {
            return remembered;
        }

        return DocumentUri.FromFileSystemPath(key);
    }

    public void Publish(string path, int? version, ImmutableArray<GSCode.Core.Diagnostics.Diagnostic> diagnostics)
    {
        Container<Diagnostic> converted = new(diagnostics.Select(diagnostic => diagnostic.ToLsp()));

        _sink.Send(new PublishDiagnosticsParams
        {
            Uri = UriFor(path),
            Version = version,
            Diagnostics = converted,
        });
    }

    /// <summary>Clears diagnostics when a document closes.</summary>
    public void Clear(string path)
    {
        _sink.Send(new PublishDiagnosticsParams
        {
            Uri = UriFor(path),
            Diagnostics = new Container<Diagnostic>(),
        });
    }
}
