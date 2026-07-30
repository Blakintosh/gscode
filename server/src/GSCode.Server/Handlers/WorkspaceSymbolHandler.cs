using GSCode.Core.Symbols;
using GSCode.Workspace.Database;
using GSCode.Server.Mapping;
using OmniSharp.Extensions.LanguageServer.Protocol;
using OmniSharp.Extensions.LanguageServer.Protocol.Client.Capabilities;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using OmniSharp.Extensions.LanguageServer.Protocol.Workspace;
using SymbolKind = OmniSharp.Extensions.LanguageServer.Protocol.Models.SymbolKind;

namespace GSCode.Server.Handlers;

/// <summary>
/// Workspace-wide symbol search. Unlike document-anchored queries it has no asking file,
/// so it spans BOTH language stores; each result is tagged with its file so the client
/// can tell GSC from CSC. Matches functions and classes by case-insensitive substring.
/// </summary>
public sealed class WorkspaceSymbolHandler : WorkspaceSymbolsHandlerBase
{
    private const int MaxResults = 256;

    private readonly ScriptDatabase _database;

    public WorkspaceSymbolHandler(ScriptDatabase database)
    {
        _database = database;
    }

    protected override WorkspaceSymbolRegistrationOptions CreateRegistrationOptions(
        WorkspaceSymbolCapability capability, ClientCapabilities clientCapabilities)
    {
        return new WorkspaceSymbolRegistrationOptions();
    }

    public override Task<Container<WorkspaceSymbol>?> Handle(WorkspaceSymbolParams request, CancellationToken cancellationToken)
    {
        string query = request.Query ?? "";
        List<WorkspaceSymbol> results = [];

        foreach ( ScriptRecord record in _database.AllRecords )
        {
            if ( results.Count >= MaxResults )
            {
                break;
            }

            foreach ( FunctionSymbol function in record.Functions )
            {
                if ( Matches(function.Name, query) )
                {
                    results.Add(Make(function.Name, SymbolKind.Function, record, function.NameRange));
                }
            }

            foreach ( ClassSymbol classSymbol in record.Classes )
            {
                if ( Matches(classSymbol.Name, query) )
                {
                    results.Add(Make(classSymbol.Name, SymbolKind.Class, record, classSymbol.NameRange));
                }
            }
        }

        return Task.FromResult<Container<WorkspaceSymbol>?>(new Container<WorkspaceSymbol>(results));
    }


    private static bool Matches(string name, string query)
    {
        return query.Length == 0 || name.Contains(query, StringComparison.OrdinalIgnoreCase);
    }

    private static WorkspaceSymbol Make(string name, SymbolKind kind, ScriptRecord record, GSCode.Core.Text.TextRange range)
    {
        return new WorkspaceSymbol
        {
            Name = name,
            Kind = kind,
            Location = new Location
            {
                Uri = DocumentUri.FromFileSystemPath(record.Path),
                Range = range.ToLsp(),
            },
        };
    }
}
