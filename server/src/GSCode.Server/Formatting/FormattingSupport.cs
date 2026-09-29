using System.Collections.Immutable;
using GSCode.Parser;
using GSCode.Server.Mapping;
using GSCode.Server.Configuration;
using GSCode.Workspace.Api;
using GSCode.Workspace.Documents;
using GSCode.Workspace.Resolution;
using Serilog;
using OmniSharp.Extensions.LanguageServer.Protocol;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;

namespace GSCode.Server.Formatting;

/// <summary>What a formatting request resolved to: the document, and the edits the formatter wants.</summary>
/// <param name="Document">
/// Carried because the on-type handler needs the document's TEXT to find the alignment group around
/// the cursor, which is a question about the buffer rather than about the edits.
/// </param>
public readonly record struct FormatRequest(
    OpenDocument Document, ImmutableArray<GscFormatter.FormatEdit> Edits);

/// <summary>
/// The steps all three formatting handlers take before they differ.
///
/// Whole-document, range and on-type formatting run the SAME formatter over the same document and
/// diverge only in which of its edits they keep — everything up to that point was written out three
/// times, including the stale-analysis reasoning below, which is the one comment in the group that
/// must not be allowed to drift.
///
/// An injected instance, like <c>NavigationSupport</c>, so it also owns the four collaborators those
/// steps need. Each handler had taken all four only to pass them straight through to here.
/// </summary>
public sealed class FormattingSupport
{
    private readonly DocumentStore _documents;
    private readonly ResolverHolder _resolver;
    private readonly StockScripts _stockScripts;
    private readonly ServerSettings _settings;

    public FormattingSupport(
        DocumentStore documents, ResolverHolder resolver, StockScripts stockScripts, ServerSettings settings)
    {
        _documents = documents;
        _resolver = resolver;
        _stockScripts = stockScripts;
        _settings = settings;
    }

    /// <summary>
    /// The formatter's options for a request: the editor's indentation, and every other flag from
    /// the settings. The fragment formatters adjust this with <c>with</c> at their own call sites.
    /// </summary>
    public FormatOptions OptionsFor(FormattingOptions requestOptions)
    {
        return FormatOptions.From((int)requestOptions.TabSize, requestOptions.InsertSpaces, _settings);
    }

    /// <summary>
    /// The formatter's edits for an open document, or null when it is unknown or has never parsed.
    /// </summary>
    /// <remarks>
    /// Analyses FRESH, and that is load-bearing. <c>FormatMinimalEdits</c> diffs the formatted
    /// output against the analysed text and returns MINIMAL edits, so their ranges index into that
    /// text — applying them to a document that has since changed points the ranges at unrelated
    /// characters and corrupts the file. Every other stale read in this server shows something
    /// wrong; this one writes something wrong.
    /// </remarks>
    /// <param name="cancellationToken">
    /// Reaches the fresh analysis below, which is a full lex, preprocess, parse and extract on the
    /// request thread. On-type formatting runs this on every <c>;</c> and <c>}</c>, so an abandoned
    /// request is one the user has already typed past.
    /// </param>
    public FormatRequest? Prepare(DocumentUri uri, FormatOptions options, CancellationToken cancellationToken)
    {
        string path = uri.GetFileSystemPath();

        // A script that ships with the game is never formatted. It is reference material that a
        // modder opens to read, and a formatter that rewrites it -- on a stray Format Document, or
        // on save -- leaves the install differing from every other player's. The check is by
        // identity (is this one of the game's own files) rather than by folder, so a modder's own
        // new script placed under raw still formats.
        if ( IsStockScript(path) )
        {
            Log.Information("Formatting refused for stock script {Path}", path);
            return null;
        }

        // Only that an analysis EXISTS is asked here — a document nothing has parsed yet has
        // nothing to format. The one actually formatted is taken fresh on the next line, for the
        // reason the remarks above give, so the result this hands back is deliberately discarded.
        if ( !_documents.TryGetAnalyzed(path, out OpenDocument document, out ParseResult _) )
        {
            return null;
        }

        ParseResult analysis = _documents.AnalyzeIfStale(document, cancellationToken);

        return new FormatRequest(document, GscFormatter.FormatMinimalEdits(analysis, options));
    }

    private bool IsStockScript(string path)
    {
        PathResolver current = _resolver.Current;
        ResolutionContext context = current.GetContext(path);
        return _stockScripts.Contains(current.GetScriptRelativePath(path, context));
    }

    /// <summary>
    /// The edits as the protocol wants them. Per-region rather than one document-spanning
    /// replacement, so the editor can hold the caret on whatever unchanged line it started on
    /// instead of dropping it at the end of a whole-file edit.
    /// </summary>
    public static List<TextEdit> ToLspEdits(IEnumerable<GscFormatter.FormatEdit> edits)
    {
        return [.. edits.Select(static edit => new TextEdit
        {
            Range = edit.Range.ToLsp(),
            NewText = edit.NewText,
        })];
    }
}
