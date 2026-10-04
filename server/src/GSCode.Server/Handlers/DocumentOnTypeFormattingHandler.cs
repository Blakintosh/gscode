using GSCode.Server.Formatting;
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
    private readonly FormattingSupport _formatting;
    private readonly TextDocumentSelector _selector;

    public DocumentOnTypeFormattingHandler(FormattingSupport formatting, TextDocumentSelector selector)
    {
        _formatting = formatting;
        _selector = selector;
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
        FormatOptions options = _formatting.OptionsFor(request.Options) with { SortDirectives = false };

        if ( _formatting.Prepare(request.TextDocument.Uri, options, cancellationToken) is not FormatRequest prepared
            || prepared.Edits.IsEmpty )
        {
            return Task.FromResult<TextEditContainer?>(null);
        }

        // Keep only edits WITHIN the alignment GROUP around the cursor — the run of lines that
        // actually re-flow together when this one is edited. Editing an assignment tidies its run
        // of assignments and stops at the next statement of a different kind, rather than the whole
        // function body. An edit that reaches outside the group is dropped, not applied whole.
        (int Top, int Bottom) group = FormatScope.GroupAround(prepared.Document.Text.Text, request.Position.Line);

        List<TextEdit> textEdits = FormattingSupport.ToLspEdits(
            prepared.Edits.Where(edit => FormattingSupport.WithinLines(edit, group.Top, group.Bottom)));

        if ( textEdits.Count == 0 )
        {
            return Task.FromResult<TextEditContainer?>(null);
        }

        return Task.FromResult<TextEditContainer?>(new TextEditContainer(textEdits));
    }
}
