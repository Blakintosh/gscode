using System.Collections.Concurrent;
using GSCode.Parser;
using GSCode.Workspace.Documents;
using OmniSharp.Extensions.LanguageServer.Protocol;
using OmniSharp.Extensions.LanguageServer.Protocol.Client.Capabilities;
using OmniSharp.Extensions.LanguageServer.Protocol.Document;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using GscToken = GSCode.Parser.Extraction.SemanticToken;
using GscTokenBuilder = GSCode.Parser.Extraction.SemanticTokenBuilder;

namespace GSCode.Server.Handlers;

/// <summary>
/// Full-document semantic highlighting. The legend order matches
/// <see cref="SemanticTokenType"/>; the base class handles the delta encoding once we push
/// each token's line/char/length/type in document order.
/// </summary>
public sealed class SemanticTokensHandler : SemanticTokensHandlerBase
{
    // Order MUST match GSCode.Parser.Extraction.SemanticTokenType's integer values.
    private static readonly SemanticTokensLegend s_legend = new()
    {
        TokenTypes = new Container<SemanticTokenType>(
            SemanticTokenType.Namespace,
            SemanticTokenType.Type,
            SemanticTokenType.Function,
            SemanticTokenType.Macro,
            SemanticTokenType.Parameter,
            SemanticTokenType.Variable,
            SemanticTokenType.Property,
            SemanticTokenType.Keyword,
            SemanticTokenType.String,
            SemanticTokenType.Number,
            SemanticTokenType.Comment),
        TokenModifiers = new Container<SemanticTokenModifier>(),
    };

    private readonly DocumentStore _documents;
    private readonly TextDocumentSelector _selector;

    /// <summary>
    /// The per-file delta baseline: what was last sent to the client for each document.
    ///
    /// The base class computes <c>semanticTokens/full/delta</c> against this, so it has to be the
    /// SAME instance across requests for one file. Handing back a fresh one each time — which is
    /// what this did — left the server with no memory of what it had sent, so every delta was
    /// computed against nothing while the client applied it on top of what it already had. That is
    /// the whole shape of the reported bug: correct on open, because the first request is a full
    /// one, and wrong after an edit, because every request after that is a delta.
    /// </summary>
    private readonly ConcurrentDictionary<DocumentUri, SemanticTokensDocument> _baselines = new();

    public SemanticTokensHandler(DocumentStore documents, TextDocumentSelector selector)
    {
        _documents = documents;
        _selector = selector;
    }

    protected override SemanticTokensRegistrationOptions CreateRegistrationOptions(
        SemanticTokensCapability capability, ClientCapabilities clientCapabilities)
    {
        return new SemanticTokensRegistrationOptions
        {
            DocumentSelector = _selector,
            Legend = s_legend,
            Full = new SemanticTokensCapabilityRequestFull { Delta = true },
            Range = true,
        };
    }

    protected override Task Tokenize(SemanticTokensBuilder builder, ITextDocumentIdentifierParams identifier, CancellationToken cancellationToken)
    {
        if ( !_documents.TryGet(identifier.TextDocument.Uri.GetFileSystemPath(), out OpenDocument document) )
        {
            return Task.CompletedTask;
        }

        // Freshened, not the last analysis to have finished. Analysis is debounced 250 ms behind
        // the keystrokes, so LatestResult routinely describes text the client has already changed —
        // and a token is a LINE, CHARACTER and LENGTH, so colouring computed against stale text
        // lands on the wrong characters. It then looks correct again after the next edit, when a
        // fresh analysis happens to have caught up, which is exactly how the desync presented.
        ParseResult? result = _documents.AnalyzeIfStale(document);
        if ( result is null )
        {
            return Task.CompletedTask;
        }

        foreach ( GscToken token in GscTokenBuilder.Build(result) )
        {
            builder.Push(token.Line, token.StartChar, token.Length, (int)token.Type, 0);
        }

        return Task.CompletedTask;
    }

    protected override Task<SemanticTokensDocument> GetSemanticTokensDocument(
        ITextDocumentIdentifierParams @params, CancellationToken cancellationToken)
    {
        // One per file, kept: see _baselines. A new instance per call silently broke every delta.
        return Task.FromResult(_baselines.GetOrAdd(
            @params.TextDocument.Uri, static _ => new SemanticTokensDocument(s_legend)));
    }

    /// <summary>Drops a closed file's baseline so the cache cannot grow for the whole session.</summary>
    public void Forget(DocumentUri uri)
    {
        _baselines.TryRemove(uri, out _);
    }
}
