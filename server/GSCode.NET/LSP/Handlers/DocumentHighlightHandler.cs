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
        _logger.LogDebug("DocumentHighlight request start");
        var sw = Stopwatch.StartNew();

        var script = _scriptManager.GetParsedEditor(request.TextDocument);
        if (script is null)
        {
            sw.Stop();
            _logger.LogDebug("DocumentHighlight abort (no script) in {ElapsedMs} ms", sw.ElapsedMilliseconds);
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
            _logger.LogDebug("DocumentHighlight local variable in {ElapsedMs} ms (count={Count})", sw.ElapsedMilliseconds, localHighlights.Count);
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
            _logger.LogDebug("DocumentHighlight none (no identifier) in {ElapsedMs} ms", sw.ElapsedMilliseconds);
            return new DocumentHighlightContainer();
        }

        var highlights = new List<DocumentHighlight>();
        var decl = script.DefinitionsTable?.GetFunctionLocation(qid.Value.qualifier ?? string.Empty, qid.Value.name);
        if (decl is not null)
        {
            highlights.Add(new DocumentHighlight { Range = decl.Value.Range, Kind = DocumentHighlightKind.Write });
        }

        // Add all reference ranges from current script
        var keys = new List<GSCode.Parser.SA.SymbolKey>
        {
            new(GSCode.Parser.SA.SymbolKind.Function, qid.Value.qualifier ?? string.Empty, qid.Value.name),
            new(GSCode.Parser.SA.SymbolKind.Class, qid.Value.qualifier ?? string.Empty, qid.Value.name)
        };

        foreach (var key in keys)
        {
            if (script.References.TryGetValue(key, out var ranges))
            {
                foreach (var r in ranges)
                {
                    // Skip if it's the declaration range already added as write
                    if (decl is not null)
                    {
                        var dRange = decl.Value.Range;
                        if (r.Start.Line == dRange.Start.Line && r.Start.Character == dRange.Start.Character &&
                            r.End.Line == dRange.End.Line && r.End.Character == dRange.End.Character)
                        {
                            continue; // skip duplicate of declaration
                        }
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
        _logger.LogDebug("DocumentHighlight finished in {ElapsedMs} ms (count={Count})", sw.ElapsedMilliseconds, highlights.Count);
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
