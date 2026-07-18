using System.Collections.Concurrent;
using GSCode.Core;
using GSCode.Core.Paths;
using GSCode.Core.Symbols;
using GSCode.Core.Text;
using GSCode.Parser;
using GSCode.Parser.Preprocessing;

namespace GSCode.Workspace.Documents;

/// <summary>One open editor document: its live text and latest analysis.</summary>
public sealed class OpenDocument
{
    public required string Path { get; init; }
    public required ScriptLanguage Language { get; init; }
    public required SourceText Text { get; set; }
    public int Version { get; set; }

    /// <summary>Latest completed analysis; null only before the first run finishes.</summary>
    public ParseResult? LatestResult { get; set; }

    /// <summary>Cancels the in-flight debounced analysis when a newer edit arrives.</summary>
    public CancellationTokenSource? PendingAnalysis { get; set; }
}

/// <summary>
/// Tracks open documents and runs their analyses. Text updates are synchronous and
/// cheap; analysis is triggered by the sync handler (debounced for didChange,
/// immediate for open/save). Closed files fall out entirely.
/// </summary>
public sealed class DocumentStore
{
    private readonly ConcurrentDictionary<string, OpenDocument> _documents = new(StringComparer.Ordinal);
    private readonly Func<string, IInsertProvider> _insertProviderFactory;
    private readonly NameTable _names;

    public DocumentStore(Func<string, IInsertProvider> insertProviderFactory, NameTable names)
    {
        _insertProviderFactory = insertProviderFactory;
        _names = names;
    }

    public OpenDocument Open(string path, string text, int version)
    {
        string normalized = PathUtil.NormalizeAbsolute(path);
        OpenDocument document = new()
        {
            Path = normalized,
            Language = ScriptAnalysis.LanguageFromPath(normalized),
            Text = SourceText.From(text),
            Version = version,
        };

        _documents[normalized] = document;
        return document;
    }

    public bool TryGet(string path, out OpenDocument document)
    {
        return _documents.TryGetValue(PathUtil.NormalizeAbsolute(path), out document!);
    }

    public void Close(string path)
    {
        if ( _documents.TryRemove(PathUtil.NormalizeAbsolute(path), out OpenDocument? document) )
        {
            document.PendingAnalysis?.Cancel();
        }
    }

    /// <summary>Applies one LSP incremental change (or a full replacement when range is null).</summary>
    public void ApplyChange(OpenDocument document, TextRange? range, string newText, int version)
    {
        if ( range is null )
        {
            document.Text = SourceText.From(newText);
        }
        else
        {
            int start = document.Text.GetOffset(range.Value.Start);
            int end = document.Text.GetOffset(range.Value.End);
            string existing = document.Text.Text;
            document.Text = SourceText.From(string.Concat(existing.AsSpan(0, start), newText, existing.AsSpan(end)));
        }

        document.Version = version;
    }

    /// <summary>Runs the full per-file pipeline on the document's current text.</summary>
    public ParseResult Analyze(OpenDocument document)
    {
        ParseResult result = ScriptAnalysis.Analyze(
            document.Path,
            document.Language,
            document.Text,
            _insertProviderFactory(document.Path),
            _names);

        document.LatestResult = result;
        return result;
    }
}
