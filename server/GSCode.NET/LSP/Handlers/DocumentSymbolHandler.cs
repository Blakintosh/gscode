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
using GSCode.Parser;
using GSCode.Parser.SA;

namespace GSCode.NET.LSP.Handlers;

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

    private static string NormalizePath(string path)
    {
        if (string.IsNullOrEmpty(path)) return path;
        if (path.Length >= 3 && path[0] == '/' && char.IsLetter(path[1]) && path[2] == ':')
        {
            path = path.Substring(1);
        }
        if (Path.DirectorySeparatorChar == '\\')
        {
            path = path.Replace('/', Path.DirectorySeparatorChar);
        }
        try { return Path.GetFullPath(path); } catch { return path; }
    }

    private static string BuildFunctionLabel(string name, string ns, string[]? parameters)
    {
        if (parameters is null || parameters.Length == 0)
            return name + "()";
        return name + "(" + string.Join(", ", parameters) + ")";
    }

    public override async Task<SymbolInformationOrDocumentSymbolContainer?> Handle(DocumentSymbolParams request, CancellationToken cancellationToken)
    {
        _logger.LogInformation("DocumentSymbol (outline) request received");

        Script? script = _script_manager.GetParsedEditor(request.TextDocument);
        if (script is null || script.DefinitionsTable is null)
        {
            return new SymbolInformationOrDocumentSymbolContainer(new Container<SymbolInformationOrDocumentSymbol>());
        }

        string currentPath = NormalizePath(request.TextDocument.Uri.ToUri().LocalPath);

        List<DocumentSymbol> symbols = new();

        // Add classes first
        foreach (var kv in script.DefinitionsTable.GetAllClassLocations())
        {
            var key = kv.Key; var val = kv.Value;
            string filePath = NormalizePath(val.FilePath ?? string.Empty);
            if (!string.Equals(filePath, currentPath, System.StringComparison.OrdinalIgnoreCase))
                continue;

            var classSymbol = new DocumentSymbol
            {
                Name = key.Name,
                Detail = key.Namespace,
                Kind = SymbolKind.Class,
                Range = val.Range,
                SelectionRange = val.Range,
                Children = new List<DocumentSymbol>()
            };
            symbols.Add(classSymbol);
        }

        // Add functions (label includes parameters)
        foreach (var kv in script.DefinitionsTable.GetAllFunctionLocations())
        {
            var key = kv.Key; var val = kv.Value;
            string filePath = NormalizePath(val.FilePath ?? string.Empty);
            if (!string.Equals(filePath, currentPath, System.StringComparison.OrdinalIgnoreCase))
                continue;

            string[]? parameters = script.DefinitionsTable.GetFunctionParameters(key.Namespace, key.Name);
            var funcSymbol = new DocumentSymbol
            {
                Name = BuildFunctionLabel(key.Name, key.Namespace, parameters),
                Detail = key.Namespace,
                Kind = SymbolKind.Function,
                Range = val.Range,
                SelectionRange = val.Range
            };
            symbols.Add(funcSymbol);
        }

        _logger.LogInformation("DocumentSymbol: returning {Count} symbols", symbols.Count);
        var union = symbols.Select(ds => new SymbolInformationOrDocumentSymbol(ds)).ToList();
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
