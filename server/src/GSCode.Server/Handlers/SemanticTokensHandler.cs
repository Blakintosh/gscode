using GSCode.Parser;
using GSCode.Workspace.Database;
using GSCode.Workspace.Documents;
using OmniSharp.Extensions.LanguageServer.Protocol.Client.Capabilities;
using OmniSharp.Extensions.LanguageServer.Protocol.Document;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using GscToken = GSCode.Parser.Extraction.SemanticToken;
using GscTokenBuilder = GSCode.Parser.Extraction.SemanticTokenBuilder;

namespace GSCode.Server.Handlers;

/// <summary>
/// Full-document semantic highlighting, for IDENTIFIERS only — what a name means is the one
/// question the TextMate grammar cannot answer, and everything it can (comments, keywords,
/// strings, numbers) is left to it so the file is not coloured twice.
/// </summary>
public sealed class SemanticTokensHandler : SemanticTokensHandlerBase
{
    // Order MUST match GSCode.Parser.Extraction.SemanticTokenType's integer values: the protocol
    // identifies a type by its INDEX here, so this is an index map rather than a list of what gets
    // sent. Comment, Keyword, String and Number are no longer emitted but keep their slots, since
    // removing them would renumber every type after them.
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

            // Deltas are NOT advertised. OmniSharp's own SemanticTokensDocument.GetSemanticTokensEdits
            // throws ArgumentOutOfRangeException while computing the edit set — reproducibly, by
            // undoing an edit — and it throws inside the library, on the far side of the boundary
            // where nothing we pass can prevent it.
            //
            // Turning them off is not much of a loss here. A delta saves resending a token set,
            // and this server's token set is now small: comments, keywords, strings and numbers
            // are all left to the TextMate grammar, so only identifiers are sent at all. Paying
            // full price for something that cannot crash beats optimising a request that can.
            Full = new SemanticTokensCapabilityRequestFull { Delta = false },
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

        // Two producers, one legend. The builder classifies what the reference index knows —
        // functions, classes, macros, fields — and LocalReferences supplies parameters and locals
        // from the AST, the two slots the legend always held and nothing filled. The reference
        // classification wins any overlap: a name the index explains is not a local, whatever the
        // body walk made of it.
        List<GscToken> tokens = [];
        HashSet<(int Line, int Char)> claimed = [];

        foreach ( GscToken token in GscTokenBuilder.Build(result) )
        {
            claimed.Add((token.Line, token.StartChar));
            tokens.Add(token);
        }

        foreach ( GscToken token in LocalReferences.SemanticTokens(result) )
        {
            if ( claimed.Add((token.Line, token.StartChar)) )
            {
                tokens.Add(token);
            }
        }

        tokens.Sort(static (left, right) =>
        {
            int lineCompare = left.Line.CompareTo(right.Line);
            return lineCompare != 0 ? lineCompare : left.StartChar.CompareTo(right.StartChar);
        });

        foreach ( GscToken token in tokens )
        {
            builder.Push(token.Line, token.StartChar, token.Length, (int)token.Type, 0);
        }

        return Task.CompletedTask;
    }

    protected override Task<SemanticTokensDocument> GetSemanticTokensDocument(
        ITextDocumentIdentifierParams @params, CancellationToken cancellationToken)
    {
        // A fresh one per request, because nothing reads it across requests: it is the delta
        // BASELINE, and deltas are not advertised. Keeping a cache of these was only ever in
        // service of the delta path, and holding one per open file for the session is not worth
        // paying for something no request asks about.
        return Task.FromResult(new SemanticTokensDocument(s_legend));
    }
}
