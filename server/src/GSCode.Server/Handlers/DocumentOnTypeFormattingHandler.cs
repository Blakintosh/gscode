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
/// formatter but returns only the edits that fall in the contiguous block around the cursor, so a
/// keystroke tidies what you are working on rather than the whole file. Because the formatter
/// refuses files with syntax errors, a half-typed document is simply left alone until it parses.
///
/// Scoping to the block is what lets consecutive alignment work here: an alignment group is bounded
/// by blank lines, so the block always contains the whole group — the lines re-pad together as you
/// type the next member, and no partial alignment escapes.
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

        // Keep only edits touching the contiguous run of non-blank lines around the cursor. That
        // run is the unit the user is editing, and it fully contains any alignment group, which is
        // bounded by blank lines — so alignment lands whole while distant code is left alone.
        (int top, int bottom) = BlockAround(document.Text.Text, request.Position.Line);

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
            // Alignment is welcome: the edits are then clipped to the block around the cursor, so a
            // group re-aligns as you type its next member without touching anything else.
            AlignConsecutive: _settings.FormatAlignConsecutive);
    }

    /// <summary>
    /// The half-open... no — the inclusive line range of the contiguous non-blank run containing
    /// <paramref name="line"/>. A blank line bounds it on each side; that is exactly where an
    /// alignment group ends too.
    /// </summary>
    internal static (int Top, int Bottom) BlockAround(string text, int line)
    {
        string[] lines = text.Split('\n');
        if ( lines.Length == 0 )
        {
            return (line, line);
        }

        int here = Math.Clamp(line, 0, lines.Length - 1);

        int top = here;
        while ( top > 0 && lines[top - 1].Trim().Length > 0 )
        {
            top--;
        }

        int bottom = here;
        while ( bottom < lines.Length - 1 && lines[bottom + 1].Trim().Length > 0 )
        {
            bottom++;
        }

        return (top, bottom);
    }
}
