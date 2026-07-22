using GSCode.Core.Text;
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
/// formatter and returns its minimal edit; because the formatter refuses files with syntax
/// errors, a half-typed document is simply left alone until it parses again.
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

        // Typing ';' where one already sits: take the duplicate back rather than let it stand.
        // Completion inserts the semicolon for a statement call, and finishing the line with ");"
        // out of habit is exactly what people do — the ')' is handled by the editor's own
        // autoClosingOvertype, but nothing does the same for ';'.
        if ( request.Character == ";" )
        {
            TextEdit? deduplicated = RemoveDuplicateSemicolon(document, request.Position.ToCore());
            if ( deduplicated is not null )
            {
                return Task.FromResult<TextEditContainer?>(new TextEditContainer(deduplicated));
            }
        }

        // Analyse fresh. FormatMinimal diffs the formatted output against the analysed text and
        // returns a MINIMAL edit, so its range indexes into that text — applying it to a document
        // that has since changed points the range at unrelated characters and corrupts the file.
        // Every other stale read shows something wrong; this one writes something wrong.
        ParseResult analysis = _documents.AnalyzeIfStale(document);

        GscFormatter.FormatEdit? edit = GscFormatter.FormatMinimal(analysis, OptionsFrom(request.Options));
        if ( edit is null )
        {
            return Task.FromResult<TextEditContainer?>(null);
        }

        TextEdit textEdit = new()
        {
            Range = edit.Value.Range.ToLsp(),
            NewText = edit.Value.NewText,
        };

        return Task.FromResult<TextEditContainer?>(new TextEditContainer(textEdit));
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
            MaxBlankLines: Math.Max(0, _settings.FormatMaxBlankLines));
    }

    /// <summary>
    /// The edit that deletes a just-typed ';' when the very next character is another one.
    ///
    /// Guards `for ( ;; )`, where a doubled semicolon is the language rather than a mistake. The
    /// check is structural — the enclosing construct — not a scan for the literal text, because
    /// `for ( i = 0 ;; )` and a stray `;;` after a statement look identical to a pattern match.
    /// </summary>
    private TextEdit? RemoveDuplicateSemicolon(OpenDocument document, GSCode.Core.Text.Position position)
    {
        SourceText text = document.Text;
        int offset = text.GetOffset(position);

        // The character just typed sits before the cursor; the one to keep would be after it.
        if ( offset <= 0 || offset >= text.Length )
        {
            return null;
        }

        if ( text.Text[offset - 1] != ';' || text.Text[offset] != ';' )
        {
            return null;
        }

        if ( IsInsideForHeader(text, offset) )
        {
            return null;
        }

        // Delete the one just typed, leaving the cursor where the user expects it.
        GSCode.Core.Text.Position start = text.GetPosition(offset - 1);
        return new TextEdit { Range = new GSCode.Core.Text.TextRange(start, position).ToLsp(), NewText = "" };
    }

    /// <summary>
    /// Whether the offset sits inside a `for ( … )` header, where `;;` is legal.
    ///
    /// Walks back over balanced parentheses to the '(' that opens the enclosing group, then looks
    /// at the word before it. Bounded by the line, since a `for` header does not span lines in
    /// any code this formatter would be asked about.
    /// </summary>
    private static bool IsInsideForHeader(SourceText text, int offset)
    {
        int depth = 0;
        int scan = offset - 1;
        int lineStart = text.GetLineStart(text.GetPosition(offset).Line);

        while ( scan >= lineStart )
        {
            char c = text.Text[scan];

            if ( c == ')' )
            {
                depth++;
            }
            else if ( c == '(' )
            {
                if ( depth == 0 )
                {
                    ReadOnlySpan<char> before = text.Text.AsSpan(lineStart, scan - lineStart).TrimEnd();
                    return before.EndsWith("for", StringComparison.Ordinal);
                }

                depth--;
            }

            scan--;
        }

        return false;
    }
}
