using GSCode.Workspace.Documents;
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
        if ( !_documents.TryGet(identifier.TextDocument.Uri.GetFileSystemPath(), out OpenDocument document) || document.LatestResult is null )
        {
            return Task.CompletedTask;
        }

        foreach ( GscToken token in GscTokenBuilder.Build(document.LatestResult) )
        {
            builder.Push(token.Line, token.StartChar, token.Length, (int)token.Type, 0);
        }

        return Task.CompletedTask;
    }

    protected override Task<SemanticTokensDocument> GetSemanticTokensDocument(
        ITextDocumentIdentifierParams @params, CancellationToken cancellationToken)
    {
        return Task.FromResult(new SemanticTokensDocument(s_legend));
    }
}
