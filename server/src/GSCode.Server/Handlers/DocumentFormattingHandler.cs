using System.Collections.Immutable;
using GSCode.Parser;
using GSCode.Workspace.Documents;
using GSCode.Server.Configuration;
using GSCode.Server.Formatting;
using GSCode.Server.Mapping;
using OmniSharp.Extensions.LanguageServer.Protocol.Client.Capabilities;
using OmniSharp.Extensions.LanguageServer.Protocol.Document;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;

namespace GSCode.Server.Handlers;

/// <summary>
/// Whole-document formatting. Runs GscFormatter over the open document and, when it produces
/// a change, returns a single full-range text edit. Refused formatting (syntax errors or an
/// unsafe reflow) yields no edits.
/// </summary>
public sealed class DocumentFormattingHandler : DocumentFormattingHandlerBase
{
    private readonly DocumentStore _documents;
    private readonly TextDocumentSelector _selector;
    private readonly ServerSettings _settings;

    public DocumentFormattingHandler(DocumentStore documents, TextDocumentSelector selector, ServerSettings settings)
    {
        _documents = documents;
        _selector = selector;
        _settings = settings;
    }

    protected override DocumentFormattingRegistrationOptions CreateRegistrationOptions(
        DocumentFormattingCapability capability, ClientCapabilities clientCapabilities)
    {
        return new DocumentFormattingRegistrationOptions { DocumentSelector = _selector };
    }

    public override Task<TextEditContainer?> Handle(DocumentFormattingParams request, CancellationToken cancellationToken)
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

        // Per-region edits rather than one document-spanning replacement, so the editor can keep
        // the caret on whatever unchanged line it started on instead of dropping it at the end of
        // a whole-file edit.
        ImmutableArray<GscFormatter.FormatEdit> edits =
            GscFormatter.FormatMinimalEdits(analysis, OptionsFrom(request.Options));
        if ( edits.IsEmpty )
        {
            return Task.FromResult<TextEditContainer?>(null);
        }

        List<TextEdit> textEdits = [.. edits.Select(static edit => new TextEdit
        {
            Range = edit.Range.ToLsp(),
            NewText = edit.NewText,
        })];

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
            SortDirectives: _settings.FormatSortDirectives);
    }
}
