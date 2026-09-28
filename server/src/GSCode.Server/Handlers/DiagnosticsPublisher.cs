using System.Collections.Immutable;
using GSCode.Core.Paths;
using GSCode.Server.Mapping;
using OmniSharp.Extensions.LanguageServer.Protocol;
using OmniSharp.Extensions.LanguageServer.Protocol.Document;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using OmniSharp.Extensions.LanguageServer.Protocol.Server;
using Serilog;

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
/// there is exactly one spelling per file on the wire. Two — the client's URI and one built from a
/// path <see cref="PathUtil.NormalizeAbsolute"/> lowercases on Windows — would be two independent
/// marker sets in the client: every problem shown twice, and close taking back only one set.
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
    /// <remarks>
    /// Guarded by <see cref="_gate"/> rather than concurrent, so that opening and closing a
    /// document are ordered against the publishes they decide: the check and the send have to be
    /// one step, or they straddle the close.
    /// </remarks>
    private readonly Dictionary<string, DocumentUri> _clientUris = new(StringComparer.Ordinal);

    /// <summary>
    /// Documents that have CLOSED and not been reopened.
    ///
    /// A versioned set describes an open document — only the sync handler and the dependent
    /// refresher stamp one — and an analysis in flight when the document closes still finishes.
    /// Its caller checks that the document is still live before publishing, but the check and the
    /// publish are two steps: a didClose landing between them runs Clear and Forget FIRST, and the
    /// late publish then puts diagnostics back on a closed file under the normalized URI, where
    /// nothing ever takes them away again.
    ///
    /// A record of what CLOSED rather than a test of what is open, so a path the publisher was
    /// never told about still publishes. Only the close is evidence; silence is not.
    /// </summary>
    private readonly HashSet<string> _closed = new(StringComparer.Ordinal);

    /// <summary>
    /// The newest document version each file has on screen, so a set that describes older text
    /// cannot land on top of it.
    ///
    /// Analyses do not finish in the order they started: the debounced pass, the save path and a
    /// request thread's freshen can all be running at once, and the version CAS in
    /// <c>OpenDocument.Publish</c> only decides which PARSE stands — it does not stop the
    /// losing caller from pushing its own, correctly stamped, older set afterwards. That is what
    /// put problems for text the user had already undone back in front of them.
    ///
    /// Not a substitute for the client honouring <c>PublishDiagnosticsParams.Version</c>; it is what
    /// makes the answer right regardless of whether the client does.
    /// </summary>
    private readonly Dictionary<string, int> _lastPublishedVersion = new(StringComparer.Ordinal);

    /// <summary>
    /// Held across the ledger check AND the send, not just the check. Two threads that each decide
    /// they are newest and then race into the send reorder on the wire in exactly the way this
    /// exists to stop. Nothing slow happens under it: the payload is converted before it is taken,
    /// and a send is one write to the connection.
    /// </summary>
    private readonly Lock _gate = new();

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
        string key = PathUtil.NormalizeAbsolute(path);

        lock ( _gate )
        {
            _clientUris[key] = uri;
            _closed.Remove(key);
        }
    }

    /// <summary>Drops a closed document's spelling. Call after <see cref="Clear"/>, not before.</summary>
    public void Forget(string path)
    {
        string key = PathUtil.NormalizeAbsolute(path);

        lock ( _gate )
        {
            _clientUris.Remove(key);
            _closed.Add(key);
        }
    }

    /// <summary>
    /// The one seam from a path to the URI the client is addressed by. A path never opened falls
    /// back to its on-disk spelling, which is right for the workspace publisher: its files are
    /// closed by definition, so the disk is the only spelling anybody has.
    /// </summary>
    public DocumentUri UriFor(string path)
    {
        string key = PathUtil.NormalizeAbsolute(path);

        lock ( _gate )
        {
            return UriForKey(key);
        }
    }

    /// <summary>The same answer for an already-normalized key, with the gate already held.</summary>
    private DocumentUri UriForKey(string key)
    {
        if ( _clientUris.TryGetValue(key, out DocumentUri? remembered) )
        {
            return remembered;
        }

        return DocumentUri.FromFileSystemPath(key);
    }

    /// <summary>
    /// Pushes a set, unless one describing newer text is already on screen.
    ///
    /// An EQUAL version passes rather than being treated as stale: the dependent refresher
    /// republishes the same version with a RICHER set once a neighbour's exports move, which is the
    /// whole reason it exists. A NULL version passes too, and is not recorded — that is the
    /// workspace publisher speaking for a closed file, where a document version means nothing, so
    /// it must neither join the ordering nor be blocked by it.
    /// </summary>
    public void Publish(string path, int? version, ImmutableArray<GSCode.Core.Diagnostics.Diagnostic> diagnostics)
    {
        string key = PathUtil.NormalizeAbsolute(path);
        Container<Diagnostic> converted = new(diagnostics.Select(diagnostic => diagnostic.ToLsp()));

        lock ( _gate )
        {
            if ( version is int stamped )
            {
                // See _closed: an analysis that was in flight when the document closed must not
                // put its diagnostics back afterwards, and the caller cannot rule that out because
                // its liveness check and its publish are two steps.
                if ( _closed.Contains(key) )
                {
                    Log.Verbose("Dropped a v{Stale} diagnostic publish for {Path}; it has closed", stamped, key);
                    return;
                }

                if ( _lastPublishedVersion.TryGetValue(key, out int current) && stamped < current )
                {
                    Log.Verbose(
                        "Dropped a v{Stale} diagnostic publish for {Path}; v{Current} is already on screen",
                        stamped,
                        key,
                        current);
                    return;
                }

                _lastPublishedVersion[key] = stamped;
            }

            Send(key, version, converted);
        }
    }

    /// <summary>
    /// Clears diagnostics when a document closes.
    ///
    /// Forgets the version alongside them: a reopened document starts again at version 0 or 1, and
    /// a ledger that still remembered v68 would silence it for the rest of the session.
    /// </summary>
    public void Clear(string path)
    {
        string key = PathUtil.NormalizeAbsolute(path);

        lock ( _gate )
        {
            _lastPublishedVersion.Remove(key);
            Send(key, version: null, new Container<Diagnostic>());
        }
    }

    private void Send(string key, int? version, Container<Diagnostic> diagnostics)
    {
        _sink.Send(new PublishDiagnosticsParams
        {
            Uri = UriForKey(key),
            Version = version,
            Diagnostics = diagnostics,
        });
    }
}
