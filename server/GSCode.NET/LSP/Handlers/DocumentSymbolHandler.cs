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

        // Fix leading slash on drive letter (e.g. "/g:/...")
        if (path.Length >= 3 && path[0] == '/' && char.IsLetter(path[1]) && path[2] == ':')
        {
            path = path.Substring(1);
        }
        // Normalize separators
        if (Path.DirectorySeparatorChar == '\\')
        {
            path = path.Replace('/', Path.DirectorySeparatorChar);
        }
        try { return Path.GetFullPath(path); } catch { return path; }
    }

    public override async Task<SymbolInformationOrDocumentSymbolContainer?> Handle(DocumentSymbolParams request, CancellationToken cancellationToken)
    {
        _logger.LogInformation("DocumentSymbol (outline) request received");

        Script? script = _script_manager.GetParsedEditor(request.TextDocument);
        if (script is null || script.DefinitionsTable is null)
        {
            return new SymbolInformationOrDocumentSymbolContainer(new Container<SymbolInformationOrDocumentSymbol>());
        }

        // Normalize current document path
        string currentPath = NormalizePath(request.TextDocument.Uri.ToUri().LocalPath);

        List<DocumentSymbol> symbols = new();

        // Add classes
        foreach (var kv in script.DefinitionsTable.GetAllClassLocations())
        {
            var key = kv.Key; // (namespace, name)
            var val = kv.Value; // (filePath, range)
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

        // Add top-level functions
        foreach (var kv in script.DefinitionsTable.GetAllFunctionLocations())
        {
            var key = kv.Key;
            var val = kv.Value;
            string filePath = NormalizePath(val.FilePath ?? string.Empty);
            if (!string.Equals(filePath, currentPath, System.StringComparison.OrdinalIgnoreCase))
                continue;

            var funcSymbol = new DocumentSymbol
            {
                Name = key.Name,
                Detail = key.Namespace,
                Kind = SymbolKind.Function,
                Range = val.Range,
                SelectionRange = val.Range
            };

            symbols.Add(funcSymbol);
        }

        _logger.LogInformation("DocumentSymbol: returning {Count} symbols", symbols.Count);

        // Convert DocumentSymbol list to the union type expected by handler return
        List<SymbolInformationOrDocumentSymbol> union = symbols.Select(ds => new SymbolInformationOrDocumentSymbol(ds)).ToList();
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
