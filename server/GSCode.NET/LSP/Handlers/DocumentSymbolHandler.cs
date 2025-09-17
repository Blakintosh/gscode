using Microsoft.Extensions.Logging;
using OmniSharp.Extensions.LanguageServer.Protocol.Client.Capabilities;
using OmniSharp.Extensions.LanguageServer.Protocol.Document;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using OmniSharp.Extensions.LanguageServer.Protocol.Server;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Diagnostics;
using GSCode.Parser;
using GSCode.Parser.SA;
using GSCode.Parser.Data;
using GSCode.NET.Util;

namespace GSCode.NET.LSP.Handlers;

using LspSymbolKind = OmniSharp.Extensions.LanguageServer.Protocol.Models.SymbolKind;

internal class DocumentSymbolHandler : DocumentSymbolHandlerBase
{
    private readonly ScriptManager _script_manager;
    private readonly ILogger<DocumentSymbolHandler> _logger;
    private readonly TextDocumentSelector _documentSelector;

    public DocumentSymbolHandler(ScriptManager scriptManager,
        ILogger<DocumentSymbolHandler> logger,
        TextDocumentSelector documentSelector)
    {
        _script_manager = scriptManager;
        _logger = logger;
        _documentSelector = documentSelector;
    }

    private static string BuildFunctionLabel(string name, string ns, string[]? parameters, string[]? flags)
    {
        string paramText = (parameters is null || parameters.Length == 0) ? "()" : ($"(" + string.Join(", ", parameters) + ")");
        if (flags is null || flags.Length == 0) return name + paramText;
        return name + paramText + " [" + string.Join(", ", flags) + "]";
    }

    private static Range ComputeContainerRange(List<DocumentSymbol> children)
    {
        // children is never empty when called
        var start = children[0].Range.Start;
        var end = children[0].Range.End;

        for (int i = 1; i < children.Count; i++)
        {
            var s = children[i].Range.Start;
            var d = children[i].Range.End;
            if (s.Line < start.Line || (s.Line == start.Line && s.Character < start.Character)) start = s;
            if (d.Line > end.Line || (d.Line == end.Line && d.Character > end.Character)) end = d;
        }
        return new Range(start, end);
    }

    public override async Task<SymbolInformationOrDocumentSymbolContainer?> Handle(DocumentSymbolParams request, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return new SymbolInformationOrDocumentSymbolContainer(new Container<SymbolInformationOrDocumentSymbol>());
        }

        _logger.LogInformation("DocumentSymbol (outline) request received");
        var sw = Stopwatch.StartNew();

        Script? script = _script_manager.GetParsedEditor(request.TextDocument);
        if (script is null || script.DefinitionsTable is null)
        {
            sw.Stop();
            _logger.LogInformation("DocumentSymbol finished in {ElapsedMs} ms: no script or no definitions", sw.ElapsedMilliseconds);
            return new SymbolInformationOrDocumentSymbolContainer(new Container<SymbolInformationOrDocumentSymbol>());
        }

        string currentPath = PathUtils.NormalizeFilePath(request.TextDocument.Uri.ToUri().LocalPath);

        // Collect by type
        List<DocumentSymbol> classNodes = new();
        foreach (var kv in script.DefinitionsTable.GetAllClassLocations())
        {
            cancellationToken.ThrowIfCancellationRequested();

            var key = kv.Key; var val = kv.Value;
            string filePath = PathUtils.NormalizeFilePath(val.FilePath ?? string.Empty);
            if (!string.Equals(filePath, currentPath, System.StringComparison.OrdinalIgnoreCase))
                continue;

            classNodes.Add(new DocumentSymbol
            {
                Name = key.Name,
                Detail = key.Namespace,
                Kind = LspSymbolKind.Class,
                Range = val.Range,
                SelectionRange = val.Range,
                Children = new List<DocumentSymbol>()
            });
        }

        List<DocumentSymbol> functionNodes = new();
        foreach (var kv in script.DefinitionsTable.GetAllFunctionLocations())
        {
            cancellationToken.ThrowIfCancellationRequested();

            var key = kv.Key; var val = kv.Value;
            string filePath = PathUtils.NormalizeFilePath(val.FilePath ?? string.Empty);
            if (!string.Equals(filePath, currentPath, System.StringComparison.OrdinalIgnoreCase))
                continue;

            string[]? parameters = script.DefinitionsTable.GetFunctionParameters(key.Namespace, key.Name);
            string[]? flags = script.DefinitionsTable.GetFunctionFlags(key.Namespace, key.Name);
            functionNodes.Add(new DocumentSymbol
            {
                Name = BuildFunctionLabel(key.Name, key.Namespace, parameters, flags),
                Detail = key.Namespace,
                Kind = LspSymbolKind.Function,
                Range = val.Range,
                SelectionRange = val.Range
            });
        }

        List<DocumentSymbol> macroNodes = new();
        if (script.MacroOutlines.Count > 0)
        {
            foreach (var m in script.MacroOutlines)
            {
                cancellationToken.ThrowIfCancellationRequested();

                string detail = (m.SourceDisplay is null || m.SourceDisplay.Length == 0) ? "#define" : m.SourceDisplay;
                macroNodes.Add(new DocumentSymbol
                {
                    Name = m.Name,
                    Detail = detail,
                    Kind = LspSymbolKind.Constant,
                    Range = m.Range,
                    SelectionRange = m.Range
                });
            }
        }

        // Build grouped root nodes (separates by type)
        List<DocumentSymbol> root = new(capacity: 3);

        if (classNodes.Count > 0)
        {
            if (classNodes.Count > 1)
            {
                classNodes.Sort((a, b) => string.Compare(a.Name, b.Name, System.StringComparison.OrdinalIgnoreCase));
            }
            var range = ComputeContainerRange(classNodes);
            root.Add(new DocumentSymbol
            {
                Name = "Classes",
                Kind = LspSymbolKind.Namespace,
                Range = range,
                SelectionRange = range,
                Children = classNodes
            });
        }

        if (functionNodes.Count > 0)
        {
            if (functionNodes.Count > 1)
            {
                functionNodes.Sort((a, b) => string.Compare(a.Name, b.Name, System.StringComparison.OrdinalIgnoreCase));
            }
            var range = ComputeContainerRange(functionNodes);
            root.Add(new DocumentSymbol
            {
                Name = "Functions",
                Kind = LspSymbolKind.Namespace,
                Range = range,
                SelectionRange = range,
                Children = functionNodes
            });
        }

        if (macroNodes.Count > 0)
        {
            if (macroNodes.Count > 1)
            {
                macroNodes.Sort((a, b) => string.Compare(a.Name, b.Name, System.StringComparison.OrdinalIgnoreCase));
            }
            var range = ComputeContainerRange(macroNodes);
            root.Add(new DocumentSymbol
            {
                Name = "Macros",
                Kind = LspSymbolKind.Namespace,
                Range = range,
                SelectionRange = range,
                Children = macroNodes
            });
        }

        int totalSymbols = classNodes.Count + functionNodes.Count + macroNodes.Count;
        sw.Stop();
        _logger.LogInformation("DocumentSymbol finished in {ElapsedMs} ms: {Count} symbols", sw.ElapsedMilliseconds, totalSymbols);

        var union = new List<SymbolInformationOrDocumentSymbol>(root.Count);
        for (int i = 0; i < root.Count; i++)
        {
            union.Add(new SymbolInformationOrDocumentSymbol(root[i]));
        }
        return new SymbolInformationOrDocumentSymbolContainer(new Container<SymbolInformationOrDocumentSymbol>(union));
    }

    protected override DocumentSymbolRegistrationOptions CreateRegistrationOptions(DocumentSymbolCapability capability, ClientCapabilities clientCapabilities)
    {
        return new DocumentSymbolRegistrationOptions()
        {
            DocumentSelector = _documentSelector
        };
    }
}
