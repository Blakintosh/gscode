using System.Collections.Immutable;
using GSCode.Parser;
using GSCode.Workspace.Documents;
using GSCode.Server.Configuration;
using GSCode.Server.Formatting;
using GSCode.Server.Mapping;
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

    public DocumentOnTypeFormattingHandler(DocumentStore documents, TextDocumentSelector selector, ServerSettings settings)
    {
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
        if ( !_documents.TryGet(request.TextDocument.Uri.GetFileSystemPath(), out OpenDocument document)
            || document.LatestResult is null )
        {
            return Task.FromResult<TextEditContainer?>(null);
        }

        // Analyse fresh. FormatMinimal diffs the formatted output against the analysed text and
        // returns a MINIMAL edit, so its range indexes into that text — applying it to a document
        // that has since changed points the range at unrelated characters and corrupts the file.
        // Every other stale read shows something wrong; this one writes something wrong.
        ParseResult analysis = _documents.AnalyzeIfStale(document);

        // Per-region edits, so a format triggered mid-edit does not haul the caret away from
        // where the keystroke left it.
        ImmutableArray<GscFormatter.FormatEdit> edits =
            GscFormatter.FormatMinimalEdits(analysis, OptionsFrom(request.Options));
        if ( edits.IsEmpty )
        {
            return Task.FromResult<TextEditContainer?>(null);
        }

        // Keep only edits touching the alignment GROUP around the cursor — the run of lines that
        // actually re-flow together when this one is edited. Editing an assignment tidies its run
        // of assignments and stops at the next statement of a different kind, rather than the whole
        // function body.
        (int top, int bottom) = FormatScope.GroupAround(document.Text.Text, request.Position.Line);

        List<TextEdit> textEdits = [.. edits
            .Where(edit => edit.Range.Start.Line <= bottom && edit.Range.End.Line >= top)
            .Select(static edit => new TextEdit
            {
                Range = edit.Range.ToLsp(),
                NewText = edit.NewText,
            })];

        if ( textEdits.Count == 0 )
        {
            return Task.FromResult<TextEditContainer?>(null);
        }

        return Task.FromResult<TextEditContainer?>(new TextEditContainer(textEdits));
    }

    /// <summary>
    /// Combines the editor's per-request indentation with the configured GSC knobs.
    ///
    /// tabSize/insertSpaces arrive in the LSP payload on EVERY formatting request, because the
    /// editor resolves them per document (language overrides, .editorconfig, detected indentation).
    /// They were being dropped entirely, so the formatter reindented every file to four spaces no
    /// matter what the editor had been told.
    /// </summary>
    private FormatOptions OptionsFrom(FormattingOptions requested)
    {
        return new FormatOptions(
            IndentWidth: requested.TabSize > 0 ? (int)requested.TabSize : 4,
            UseTabs: !requested.InsertSpaces,
            PadParens: _settings.FormatPadParens,
            MaxBlankLines: Math.Max(0, _settings.FormatMaxBlankLines),
            // Never here: this formats a fragment, and hoisting the whole file's
            // directive block from under a partial edit would be startling.
            SortDirectives: false,
            // Alignment is welcome: the edits are then clipped to the group around the cursor, so a
            // run re-aligns as you type its next member without touching anything else.
            AlignConsecutive: _settings.FormatAlignConsecutive);
    }
}
