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
internal readonly record struct FormatRequest(
    OpenDocument Document, ImmutableArray<GscFormatter.FormatEdit> Edits);

/// <summary>
/// The steps all three formatting handlers take before they differ.
///
/// Whole-document, range and on-type formatting run the SAME formatter over the same document and
/// diverge only in which of its edits they keep — everything up to that point was written out three
/// times, including the stale-analysis reasoning below, which is the one comment in the group that
/// must not be allowed to drift.
/// </summary>
internal static class FormattingSupport
{
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
    public static FormatRequest? Prepare(
        DocumentStore documents, ResolverHolder resolver, StockScripts stockScripts, DocumentUri uri, FormatOptions options)
    {
        string path = uri.GetFileSystemPath();

        // A script that ships with the game is never formatted. It is reference material that a
        // modder opens to read, and a formatter that rewrites it -- on a stray Format Document, or
        // on save -- leaves the install differing from every other player's. The check is by
        // identity (is this one of the game's own files) rather than by folder, so a modder's own
        // new script placed under raw still formats.
        if ( IsStockScript(resolver, stockScripts, path) )
        {
            Log.Information("Formatting refused for stock script {Path}", path);
            return null;
        }

        // Only that an analysis EXISTS is asked here — a document nothing has parsed yet has
        // nothing to format. The one actually formatted is taken fresh on the next line, for the
        // reason the remarks above give, so the result this hands back is deliberately discarded.
        if ( !documents.TryGetAnalyzed(path, out OpenDocument document, out ParseResult _) )
        {
            return null;
        }

        ParseResult analysis = documents.AnalyzeIfStale(document);

        return new FormatRequest(document, GscFormatter.FormatMinimalEdits(analysis, options));
    }

    private static bool IsStockScript(ResolverHolder resolver, StockScripts stockScripts, string path)
    {
        PathResolver current = resolver.Current;
        ResolutionContext context = current.GetContext(path);
        return stockScripts.Contains(current.GetScriptRelativePath(path, context));
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
