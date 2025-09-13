using GSCode.Parser;
using Microsoft.Extensions.Logging;
using OmniSharp.Extensions.LanguageServer.Protocol.Client.Capabilities;
using OmniSharp.Extensions.LanguageServer.Protocol.Document;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using System.Diagnostics;
using System.Linq;

namespace GSCode.NET.LSP.Handlers;

internal sealed class DocumentHighlightHandler(
    ScriptManager scriptManager,
    ILogger<DocumentHighlightHandler> logger,
    TextDocumentSelector documentSelector) : DocumentHighlightHandlerBase
{
    private readonly ScriptManager _scriptManager = scriptManager;
    private readonly ILogger<DocumentHighlightHandler> _logger = logger;
    private readonly TextDocumentSelector _documentSelector = documentSelector;

    public override async Task<DocumentHighlightContainer?> Handle(DocumentHighlightParams request, CancellationToken cancellationToken)
    {
        _logger.LogInformation("DocumentHighlight request received, processing...");
        var sw = Stopwatch.StartNew();

        var script = _scriptManager.GetParsedEditor(request.TextDocument);
        if (script is null)
        {
            sw.Stop();
            _logger.LogInformation("DocumentHighlight finished in {ElapsedMs} ms: no script", sw.ElapsedMilliseconds);
            return new DocumentHighlightContainer();
        }

        // Normalise position to handle caret at end-of-identifier (common in editors)
        var pos = request.Position;
        async Task<IReadOnlyList<Range>> GetLocalAsync(Position p)
        {
            return await script.GetLocalVariableReferencesAsync(p, includeDeclaration: true, cancellationToken);
        }

        // First, try highlighting local variable references within the enclosing function scope
        var localRefs = await GetLocalAsync(pos);
        if (localRefs.Count == 0 && pos.Character > 0)
        {
            var leftPos = new Position(pos.Line, pos.Character - 1);
            localRefs = await GetLocalAsync(leftPos);
        }

        if (localRefs.Count > 0)
        {
            var localHighlights = new List<DocumentHighlight>(localRefs.Count);
            foreach (var r in localRefs)
            {
                localHighlights.Add(new DocumentHighlight { Range = r, Kind = DocumentHighlightKind.Read });
            }

            // Deduplicate by range just in case
            if (localHighlights.Count > 1)
            {
                localHighlights = localHighlights
                    .GroupBy(h => new { SLine = h.Range.Start.Line, SChar = h.Range.Start.Character, ELine = h.Range.End.Line, EChar = h.Range.End.Character })
                    .Select(g => g.First())
                    .ToList();
            }

            sw.Stop();
            _logger.LogInformation("DocumentHighlight finished in {ElapsedMs} ms (local variable). Highlights: {Count}", sw.ElapsedMilliseconds, localHighlights.Count);
            return new DocumentHighlightContainer(localHighlights);
        }

        // If not a local, try to resolve a qualified identifier; also consider caret-left fallback
        var qid = await script.GetQualifiedIdentifierAtAsync(pos, cancellationToken);
        if (qid is null && pos.Character > 0)
        {
            var leftPos = new Position(pos.Line, pos.Character - 1);
            qid = await script.GetQualifiedIdentifierAtAsync(leftPos, cancellationToken);
        }

        if (qid is null)
        {
            sw.Stop();
            _logger.LogInformation("DocumentHighlight finished in {ElapsedMs} ms: no identifier", sw.ElapsedMilliseconds);
            return new DocumentHighlightContainer();
        }

        string ns = qid.Value.qualifier ?? (script.DefinitionsTable?.CurrentNamespace ?? string.Empty);
        string name = qid.Value.name;

        // Collect declaration highlight if in this document
        Range? declRange = script.DefinitionsTable?.GetFunctionLocation(ns, name)?.Range
                        ?? script.DefinitionsTable?.GetClassLocation(ns, name)?.Range
                        ?? script.DefinitionsTable?.GetFunctionLocationAnyNamespace(name)?.Range
                        ?? script.DefinitionsTable?.GetClassLocationAnyNamespace(name)?.Range;

        var highlights = new List<DocumentHighlight>();
        if (declRange is Range dr)
        {
            highlights.Add(new DocumentHighlight { Range = dr, Kind = DocumentHighlightKind.Write });
        }

        // Add all reference ranges from current script
        var keys = new List<GSCode.Parser.SA.SymbolKey>
        {
            new(GSCode.Parser.SA.SymbolKind.Function, ns, name),
            new(GSCode.Parser.SA.SymbolKind.Class, ns, name)
        };

        foreach (var key in keys)
        {
            if (script.References.TryGetValue(key, out var ranges))
            {
                foreach (var r in ranges)
                {
                    // Skip if it's the declaration range already added as write
                    if (declRange is Range d && r.Start.Line == d.Start.Line && r.Start.Character == d.Start.Character && r.End.Line == d.End.Line && r.End.Character == d.End.Character)
                    {
                        continue;
                    }
                    highlights.Add(new DocumentHighlight { Range = r, Kind = DocumentHighlightKind.Read });
                }
            }
        }

        // Deduplicate highlights by exact range
        if (highlights.Count > 1)
        {
            highlights = highlights
                .GroupBy(h => new { SLine = h.Range.Start.Line, SChar = h.Range.Start.Character, ELine = h.Range.End.Line, EChar = h.Range.End.Character })
                .Select(g => g.First())
                .ToList();
        }

        sw.Stop();
        _logger.LogInformation("DocumentHighlight finished in {ElapsedMs} ms. Highlights: {Count}", sw.ElapsedMilliseconds, highlights.Count);
        return new DocumentHighlightContainer(highlights);
    }

    protected override DocumentHighlightRegistrationOptions CreateRegistrationOptions(DocumentHighlightCapability capability, ClientCapabilities clientCapabilities)
    {
        return new DocumentHighlightRegistrationOptions
        {
            DocumentSelector = _documentSelector
        };
    }
}
