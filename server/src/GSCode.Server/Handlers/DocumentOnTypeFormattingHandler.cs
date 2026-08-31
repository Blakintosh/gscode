using GSCode.Workspace.Api;
using GSCode.Workspace.Documents;
using GSCode.Server.Configuration;
using GSCode.Server.Formatting;
using OmniSharp.Extensions.LanguageServer.Protocol;
using OmniSharp.Extensions.LanguageServer.Protocol.Client.Capabilities;
using OmniSharp.Extensions.LanguageServer.Protocol.Document;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;

namespace GSCode.Server.Handlers;

/// <summary>
/// On-type formatting, triggered after a closing brace or semicolon. Reuses the whole-document
/// formatter but returns only the edits that fall in the alignment GROUP around the cursor, so a
/// keystroke tidies the run you are editing rather than the whole function. Because the formatter
/// refuses files with syntax errors, a half-typed document is simply left alone until it parses.
///
/// Scoping to the group is what makes consecutive alignment feel local: editing one of a run of
/// assignments re-aligns that run and stops at the next statement of a different kind. See
/// <see cref="FormatScope"/> for how the group is found.
/// </summary>
public sealed class DocumentOnTypeFormattingHandler : DocumentOnTypeFormattingHandlerBase
{
    private readonly DocumentStore _documents;
    private readonly TextDocumentSelector _selector;
    private readonly ServerSettings _settings;
    private readonly ResolverHolder _resolver;
    private readonly StockScripts _stockScripts;

    public DocumentOnTypeFormattingHandler(
        DocumentStore documents, TextDocumentSelector selector, ServerSettings settings,
        ResolverHolder resolver, StockScripts stockScripts)
    {
        _resolver = resolver;
        _stockScripts = stockScripts;
        _documents = documents;
        _selector = selector;
        _settings = settings;
    }

    protected override DocumentOnTypeFormattingRegistrationOptions CreateRegistrationOptions(
        DocumentOnTypeFormattingCapability capability, ClientCapabilities clientCapabilities)
    {
        return new DocumentOnTypeFormattingRegistrationOptions
        {
            DocumentSelector = _selector,
            FirstTriggerCharacter = "}",
            MoreTriggerCharacter = new Container<string>(";"),
        };
    }

    public override Task<TextEditContainer?> Handle(DocumentOnTypeFormattingParams request, CancellationToken cancellationToken)
    {
        // SortDirectives is never on here: this formats a FRAGMENT, and hoisting the whole file's
        // directive block out from under a partial edit would be startling. Alignment stays on —
        // the edits are then clipped to the group around the cursor, so a run re-aligns as you
        // type its next member without touching anything else.
        FormatOptions options = FormatOptions.From(
            (int)request.Options.TabSize, request.Options.InsertSpaces, _settings) with { SortDirectives = false };

        if ( FormattingSupport.Prepare(_documents, _resolver, _stockScripts, request.TextDocument.Uri, options) is not FormatRequest prepared
            || prepared.Edits.IsEmpty )
        {
            return Task.FromResult<TextEditContainer?>(null);
        }

        // Keep only edits touching the alignment GROUP around the cursor — the run of lines that
        // actually re-flow together when this one is edited. Editing an assignment tidies its run
        // of assignments and stops at the next statement of a different kind, rather than the whole
        // function body.
        //
        // A LINE-level test, not TextRange.Overlaps: the group is a span of whole lines, and an
        // edit that starts mid-line still belongs to the line it sits on.
        (int top, int bottom) = FormatScope.GroupAround(prepared.Document.Text.Text, request.Position.Line);

        List<TextEdit> textEdits = FormattingSupport.ToLspEdits(
            prepared.Edits.Where(edit => edit.Range.Start.Line <= bottom && edit.Range.End.Line >= top));

        if ( textEdits.Count == 0 )
        {
            return Task.FromResult<TextEditContainer?>(null);
        }

        return Task.FromResult<TextEditContainer?>(new TextEditContainer(textEdits));
    }
}
