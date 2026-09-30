using System.Collections.Immutable;
using GSCode.Core.Symbols;
using GSCode.Workspace.Database;
using GSCode.Server.Mapping;
using OmniSharp.Extensions.LanguageServer.Protocol.Client.Capabilities;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using OmniSharp.Extensions.LanguageServer.Protocol.Workspace;
using SymbolKind = OmniSharp.Extensions.LanguageServer.Protocol.Models.SymbolKind;
using TextRange = GSCode.Core.Text.TextRange;

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

    /// <summary>
    /// Answers one workspace symbol query.
    ///
    /// Cancellation is checked per record for the same reason <see cref="CompletionHandler"/> checks
    /// it: the client sends one of these per keystroke in the symbol box and cancels the previous,
    /// and this walks EVERY record in both stores — thousands on a real install — with no index
    /// narrowing it first.
    ///
    /// Per record rather than per symbol: a record's function list is short, and the walk is the
    /// cost, not the match.
    /// </summary>
    public override Task<Container<WorkspaceSymbol>?> Handle(WorkspaceSymbolParams request, CancellationToken cancellationToken)
    {
        string query = request.Query ?? "";

        ImmutableArray<(ScriptRecord Record, FunctionSymbol Function)>.Builder functionMatches =
            ImmutableArray.CreateBuilder<(ScriptRecord, FunctionSymbol)>();
        ImmutableArray<(ScriptRecord Record, ClassSymbol Class)>.Builder classMatches =
            ImmutableArray.CreateBuilder<(ScriptRecord, ClassSymbol)>();

        foreach ( ScriptRecord record in _database.AllRecords )
        {
            cancellationToken.ThrowIfCancellationRequested();

            foreach ( FunctionSymbol function in record.Functions )
            {
                if ( Matches(function.Name, query) )
                {
                    functionMatches.Add((record, function));
                }
            }

            foreach ( ClassSymbol classSymbol in record.Classes )
            {
                if ( Matches(classSymbol.Name, query) )
                {
                    classMatches.Add((record, classSymbol));
                }
            }
        }

        // Overlay shadowing: a raw file and the mod overlay replacing it can both match the query,
        // and without this a raw copy the engine never loads showed up as a second, dead result.
        ImmutableArray<(ScriptRecord Record, FunctionSymbol Function)> functions = DatabaseQueries.ApplyShadowing(
            functionMatches.ToImmutable(), static m => m.Record, static m => m.Function.KeyName);
        ImmutableArray<(ScriptRecord Record, ClassSymbol Class)> classes = DatabaseQueries.ApplyShadowing(
            classMatches.ToImmutable(), static m => m.Record, static m => m.Class.KeyName);

        List<WorkspaceSymbol> results = [];
        foreach ( (ScriptRecord Record, FunctionSymbol Function) match in functions )
        {
            if ( results.Count >= MaxResults )
            {
                break;
            }

            results.Add(Make(match.Function.Name, SymbolKind.Function, match.Record, match.Function.NameRange));
        }

        foreach ( (ScriptRecord Record, ClassSymbol Class) match in classes )
        {
            if ( results.Count >= MaxResults )
            {
                break;
            }

            results.Add(Make(match.Class.Name, SymbolKind.Class, match.Record, match.Class.NameRange));
        }

        return Task.FromResult<Container<WorkspaceSymbol>?>(new Container<WorkspaceSymbol>(results));
    }

    private static bool Matches(string name, string query)
    {
        return query.Length == 0 || name.Contains(query, StringComparison.OrdinalIgnoreCase);
    }

    private static WorkspaceSymbol Make(string name, SymbolKind kind, ScriptRecord record, TextRange range)
    {
        return new WorkspaceSymbol
        {
            Name = name,
            Kind = kind,
            Location = LspMapping.LocationAt(record.Path, range),
        };
    }
}
