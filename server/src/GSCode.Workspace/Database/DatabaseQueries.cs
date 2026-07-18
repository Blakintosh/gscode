using System.Collections.Immutable;
using GSCode.Core.Symbols;

namespace GSCode.Workspace.Database;

/// <summary>A resolved function with the record that declares it (for locations/paths).</summary>
public sealed record ResolvedFunction(FunctionSymbol Function, ScriptRecord Record);

/// <summary>A resolved class with its declaring record.</summary>
public sealed record ResolvedClass(ClassSymbol Class, ScriptRecord Record);

/// <summary>
/// Shared query logic over one LanguageStore. Namespace lookup MERGES across every
/// visible record contributing to the namespace; overlay shadowing dedups by
/// script-relative identity with the asking context's priority. Language never appears
/// here — the store was chosen at the entry point.
/// </summary>
public static class DatabaseQueries
{
    /// <summary>
    /// Every visible function matching namespace+name, merged across contributing files.
    /// Private functions are only visible from their own file.
    /// </summary>
    public static ImmutableArray<ResolvedFunction> LookupFunctions(
        LanguageStore store,
        string askingContextId,
        string askingPath,
        string? namespaceName,
        string keyName)
    {
        ImmutableArray<ResolvedFunction>.Builder matches = ImmutableArray.CreateBuilder<ResolvedFunction>();

        foreach ( ScriptRecord record in store.AllRecords )
        {
            if ( !ScriptDatabase.CanSee(askingContextId, record.ContextId) )
            {
                continue;
            }

            foreach ( FunctionSymbol function in record.Functions )
            {
                if ( function.KeyName != keyName )
                {
                    continue;
                }

                if ( namespaceName is not null && function.Namespace != namespaceName )
                {
                    continue;
                }

                if ( function.IsPrivate && record.Path != askingPath )
                {
                    continue;
                }

                matches.Add(new ResolvedFunction(function, record));
            }
        }

        return ApplyShadowing(matches.ToImmutable());
    }

    /// <summary>
    /// Overlay shadowing: when a mod/workspace copy and the raw copy of the SAME
    /// script-relative file both match, the overlay wins and the raw copy drops out.
    /// </summary>
    private static ImmutableArray<ResolvedFunction> ApplyShadowing(ImmutableArray<ResolvedFunction> matches)
    {
        if ( matches.Length < 2 )
        {
            return matches;
        }

        HashSet<string> overlayIdentities = new(StringComparer.Ordinal);
        foreach ( ResolvedFunction match in matches )
        {
            if ( match.Record.ContextId != "raw" && match.Record.RelativePath.Length > 0 )
            {
                overlayIdentities.Add(match.Record.RelativePath + "|" + match.Function.KeyName);
            }
        }

        if ( overlayIdentities.Count == 0 )
        {
            return matches;
        }

        ImmutableArray<ResolvedFunction>.Builder kept = ImmutableArray.CreateBuilder<ResolvedFunction>();
        foreach ( ResolvedFunction match in matches )
        {
            bool shadowedOut = match.Record.ContextId == "raw"
                && overlayIdentities.Contains(match.Record.RelativePath + "|" + match.Function.KeyName);

            if ( !shadowedOut )
            {
                kept.Add(match);
            }
        }

        return kept.ToImmutable();
    }

    /// <summary>Every visible function in a namespace (for completion), deduplicated by name.</summary>
    public static ImmutableArray<FunctionSymbol> FunctionsInNamespace(
        LanguageStore store,
        string askingContextId,
        string askingPath,
        string namespaceName)
    {
        Dictionary<string, FunctionSymbol> byName = new(StringComparer.Ordinal);

        foreach ( ScriptRecord record in store.AllRecords )
        {
            if ( !ScriptDatabase.CanSee(askingContextId, record.ContextId) )
            {
                continue;
            }

            foreach ( FunctionSymbol function in record.Functions )
            {
                if ( function.Namespace != namespaceName )
                {
                    continue;
                }

                if ( function.IsPrivate && record.Path != askingPath )
                {
                    continue;
                }

                byName.TryAdd(function.KeyName, function);
            }
        }

        return [.. byName.Values];
    }

    /// <summary>Every visible class (for completion), deduplicated by name.</summary>
    public static ImmutableArray<ClassSymbol> AllVisibleClasses(LanguageStore store, string askingContextId)
    {
        Dictionary<string, ClassSymbol> byName = new(StringComparer.Ordinal);

        foreach ( ScriptRecord record in store.AllRecords )
        {
            if ( !ScriptDatabase.CanSee(askingContextId, record.ContextId) )
            {
                continue;
            }

            foreach ( ClassSymbol classSymbol in record.Classes )
            {
                byName.TryAdd(classSymbol.KeyName, classSymbol);
            }
        }

        return [.. byName.Values];
    }

    /// <summary>Every visible class matching the name (namespace optional).</summary>
    public static ImmutableArray<ResolvedClass> LookupClasses(
        LanguageStore store,
        string askingContextId,
        string? namespaceName,
        string keyName)
    {
        ImmutableArray<ResolvedClass>.Builder matches = ImmutableArray.CreateBuilder<ResolvedClass>();

        foreach ( ScriptRecord record in store.AllRecords )
        {
            if ( !ScriptDatabase.CanSee(askingContextId, record.ContextId) )
            {
                continue;
            }

            foreach ( ClassSymbol classSymbol in record.Classes )
            {
                if ( classSymbol.KeyName != keyName )
                {
                    continue;
                }

                if ( namespaceName is not null && classSymbol.Namespace != namespaceName )
                {
                    continue;
                }

                matches.Add(new ResolvedClass(classSymbol, record));
            }
        }

        return matches.ToImmutable();
    }

    /// <summary>All visible files (with exact ranges) referencing a key.</summary>
    public static ImmutableArray<(ScriptRecord Record, ReferenceEntry Entry)> FindReferences(
        LanguageStore store,
        string askingContextId,
        SymbolKey key)
    {
        ImmutableArray<(ScriptRecord, ReferenceEntry)>.Builder results =
            ImmutableArray.CreateBuilder<(ScriptRecord, ReferenceEntry)>();

        foreach ( string path in store.FilesReferencing(key) )
        {
            if ( !store.TryGet(path, out ScriptRecord record) )
            {
                continue;
            }

            if ( !ScriptDatabase.CanSee(askingContextId, record.ContextId) )
            {
                continue;
            }

            foreach ( ReferenceEntry entry in record.References )
            {
                if ( entry.Key == key )
                {
                    results.Add((record, entry));
                }
            }
        }

        return results.ToImmutable();
    }
}
