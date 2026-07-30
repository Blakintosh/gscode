using System.Collections.Immutable;
using GSCode.Core.Text;
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
/// Range ("Format Selection") formatting. GSC formatting is holistic (whitespace-only, whole
/// document), so this runs the same formatter and returns the minimal edit only when the
/// changed region overlaps the requested range — a clean selection then does nothing.
/// </summary>
public sealed class DocumentRangeFormattingHandler : DocumentRangeFormattingHandlerBase
{
    private readonly DocumentStore _documents;
    private readonly TextDocumentSelector _selector;
    private readonly ServerSettings _settings;

    public DocumentRangeFormattingHandler(DocumentStore documents, TextDocumentSelector selector, ServerSettings settings)
    {
        _documents = documents;
        _selector = selector;
        _settings = settings;
    }

    protected override DocumentRangeFormattingRegistrationOptions CreateRegistrationOptions(
        DocumentRangeFormattingCapability capability, ClientCapabilities clientCapabilities)
    {
        return new DocumentRangeFormattingRegistrationOptions { DocumentSelector = _selector };
    }

    public override Task<TextEditContainer> Handle(DocumentRangeFormattingParams request, CancellationToken cancellationToken)
    {
        if ( !_documents.TryGet(request.TextDocument.Uri.GetFileSystemPath(), out OpenDocument document)
            || document.LatestResult is null )
        {
            return Task.FromResult<TextEditContainer>(new TextEditContainer());
        }

        // Analyse fresh. FormatMinimal diffs the formatted output against the analysed text and
        // returns a MINIMAL edit, so its range indexes into that text — applying it to a document
        // that has since changed points the range at unrelated characters and corrupts the file.
        // Every other stale read shows something wrong; this one writes something wrong.
        ParseResult analysis = _documents.AnalyzeIfStale(document);

        // Per-region edits, keeping only those that touch the requested range. Multiple small
        // edits also let the editor hold the caret on an unchanged line.
        ImmutableArray<GscFormatter.FormatEdit> edits =
            GscFormatter.FormatMinimalEdits(analysis, OptionsFrom(request.Options));
        if ( edits.IsEmpty )
        {
            return Task.FromResult<TextEditContainer>(new TextEditContainer());
        }

        TextRange requested = request.Range.ToCore();
        List<TextEdit> textEdits = [.. edits
            .Where(edit => Overlaps(edit.Range, requested))
            .Select(static edit => new TextEdit
            {
                Range = edit.Range.ToLsp(),
                NewText = edit.NewText,
            })];

        return Task.FromResult<TextEditContainer>(new TextEditContainer(textEdits));
    }

    private static bool Overlaps(TextRange edit, TextRange requested)
    {
        return edit.Start <= requested.End && requested.Start <= edit.End;
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
        // Same reasoning as the on-type handler: a fragment format must not move the file's
        // directive block. Alignment is left to the setting.
        return FormatOptions.From((int)requested.TabSize, requested.InsertSpaces, _settings)
            with { SortDirectives = false };
    }
}
