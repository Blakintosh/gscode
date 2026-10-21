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
        List<WorkspaceSymbol> results = [];

        // Functions first, then classes, in record order, stopping at the cap — the answer the
        // collect-then-shadow-then-cut version gave, without collecting. That version gathered
        // every match before keeping 256: an empty or one-letter query matches nearly every
        // function, 2.1 s per keystroke at 20,000 heavy BO3 files (PERF.md, the scale section).
        foreach ( ScriptRecord record in _database.AllRecords )
        {
            cancellationToken.ThrowIfCancellationRequested();

            foreach ( FunctionSymbol function in record.Functions )
            {
                if ( Matches(function.Name, query) && !ShadowedOut(record, function.KeyName, declaresClass: false) )
                {
                    results.Add(Make(function.Name, SymbolKind.Function, record, function.NameRange));
                    if ( results.Count >= MaxResults )
                    {
                        return Task.FromResult<Container<WorkspaceSymbol>?>(new Container<WorkspaceSymbol>(results));
                    }
                }
            }
        }

        foreach ( ScriptRecord record in _database.AllRecords )
        {
            cancellationToken.ThrowIfCancellationRequested();

            foreach ( ClassSymbol classSymbol in record.Classes )
            {
                if ( Matches(classSymbol.Name, query) && !ShadowedOut(record, classSymbol.KeyName, declaresClass: true) )
                {
                    results.Add(Make(classSymbol.Name, SymbolKind.Class, record, classSymbol.NameRange));
                    if ( results.Count >= MaxResults )
                    {
                        return Task.FromResult<Container<WorkspaceSymbol>?>(new Container<WorkspaceSymbol>(results));
                    }
                }
            }
        }

        return Task.FromResult<Container<WorkspaceSymbol>?>(new Container<WorkspaceSymbol>(results));
    }

    /// <summary>
    /// Overlay shadowing, decided per match: a raw file and the mod overlay replacing it can both
    /// match the query, and without this a raw copy the engine never loads showed up as a second,
    /// dead result. The rule <c>DatabaseQueries.ApplyShadowing</c> applied over the finished list —
    /// a raw match is dropped when a non-raw record at its relative path declares the same name —
    /// asked of the store's path index instead, since an overlay declaring the name matches the
    /// query exactly when the raw one does.
    /// </summary>
    private bool ShadowedOut(ScriptRecord record, string keyName, bool declaresClass)
    {
        if ( record.ContextId != "raw" || record.RelativePath.Length == 0 )
        {
            return false;
        }

        LanguageStore store = _database.StoreFor(record.Language);
        foreach ( string path in store.FilesAt(RelativePathIndex.Normalize(record.RelativePath)) )
        {
            if ( !store.TryGet(path, out ScriptRecord other)
                || other.ContextId == "raw"
                || !string.Equals(other.RelativePath, record.RelativePath, StringComparison.Ordinal) )
            {
                continue;
            }

            if ( declaresClass ? Declares(other.Classes, keyName) : Declares(other.Functions, keyName) )
            {
                return true;
            }
        }

        return false;
    }

    private static bool Declares(ImmutableArray<FunctionSymbol> functions, string keyName)
    {
        foreach ( FunctionSymbol function in functions )
        {
            if ( string.Equals(function.KeyName, keyName, StringComparison.Ordinal) )
            {
                return true;
            }
        }

        return false;
    }

    private static bool Declares(ImmutableArray<ClassSymbol> classes, string keyName)
    {
        foreach ( ClassSymbol classSymbol in classes )
        {
            if ( string.Equals(classSymbol.KeyName, keyName, StringComparison.Ordinal) )
            {
                return true;
            }
        }

        return false;
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
