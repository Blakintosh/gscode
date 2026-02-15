using System.Collections.Concurrent;
using GSCode.Data.Models.Interfaces;
using GSCode.Parser.SA;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;

namespace GSCode.NET.LSP;

/// <summary>
/// Represents a canonical symbol definition stored in the global registry.
/// This is the single source of truth for symbol metadata across all scripts.
/// </summary>
public sealed record SymbolDefinition(
    string Namespace,
    string Name,
    ExportedSymbolType Type,
    string FilePath,
    Range Range,
    string[]? Parameters = null,
    string[]? Flags = null,
    string? Documentation = null,
    IExportedSymbol? Symbol = null
);

/// <summary>
/// A workspace-wide symbol database that provides O(1) lookup for function and class definitions.
/// This eliminates per-file duplication of symbol metadata and enables efficient cross-file resolution.
/// Implements ISymbolLocationProvider to allow DefinitionsTable to query the registry.
/// </summary>
public sealed class GlobalSymbolRegistry : ISymbolLocationProvider
{
    // Canonical symbol definitions indexed by (namespace, name) - single source of truth
    private readonly ConcurrentDictionary<(string Namespace, string Name), SymbolDefinition> _symbols = new();

    // Secondary index: file path -> set of symbol keys defined in that file
    // Enables efficient cleanup when a file is removed or reparsed
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<(string Namespace, string Name), byte>> _fileIndex = new(StringComparer.OrdinalIgnoreCase);

    // Index for fast lookup by name only (ignoring namespace)
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<(string Namespace, string Name), byte>> _nameIndex = new(StringComparer.OrdinalIgnoreCase);

    // Reader-writer lock for operations that need atomicity across multiple dictionaries
    private readonly ReaderWriterLockSlim _lock = new(LockRecursionPolicy.SupportsRecursion);

    private static (string Namespace, string Name) NormalizeKey(string ns, string name)
        => (ns?.ToLowerInvariant() ?? string.Empty, name?.ToLowerInvariant() ?? string.Empty);

    /// <summary>
    /// Adds or updates a symbol definition in the registry.
    /// </summary>
    public void AddOrUpdateSymbol(SymbolDefinition definition)
    {
        var key = NormalizeKey(definition.Namespace, definition.Name);

        _lock.EnterWriteLock();
        try
        {
            // Remove from old file index if symbol existed with different file
            if (_symbols.TryGetValue(key, out var existing) &&
                !string.Equals(existing.FilePath, definition.FilePath, StringComparison.OrdinalIgnoreCase))
            {
                RemoveFromFileIndex(existing.FilePath, key);
            }

            // Update main symbol store
            _symbols[key] = definition;

            // Update file index
            var fileSymbols = _fileIndex.GetOrAdd(definition.FilePath,
                _ => new ConcurrentDictionary<(string Namespace, string Name), byte>());
            fileSymbols[key] = 0;

            // Update name index
            var nameSymbols = _nameIndex.GetOrAdd(definition.Name.ToLowerInvariant(),
                _ => new ConcurrentDictionary<(string Namespace, string Name), byte>());
            nameSymbols[key] = 0;
        }
        finally
        {
            _lock.ExitWriteLock();
        }
    }

    /// <summary>
    /// Finds a symbol by namespace and name. If namespace is provided, searches that namespace first.
    /// Falls back to searching all namespaces if not found.
    /// </summary>
    public SymbolDefinition? FindSymbol(string? ns, string name)
    {
        _lock.EnterReadLock();
        try
        {
            // Try exact namespace match first
            if (ns is not null)
            {
                var key = NormalizeKey(ns, name);
                if (_symbols.TryGetValue(key, out var def))
                    return def;
            }

            // Fall back to name-only search
            return FindSymbolByNameOnly(name);
        }
        finally
        {
            _lock.ExitReadLock();
        }
    }

    /// <summary>
    /// Finds a symbol by name only, searching across all namespaces.
    /// </summary>
    public SymbolDefinition? FindSymbolByNameOnly(string name)
    {
        _lock.EnterReadLock();
        try
        {
            var normalizedName = name?.ToLowerInvariant() ?? string.Empty;
            if (_nameIndex.TryGetValue(normalizedName, out var keys))
            {
                foreach (var key in keys.Keys)
                {
                    if (_symbols.TryGetValue(key, out var def))
                        return def;
                }
            }
            return null;
        }
        finally
        {
            _lock.ExitReadLock();
        }
    }

    /// <summary>
    /// Finds a function symbol by namespace and name.
    /// </summary>
    public SymbolDefinition? FindFunction(string? ns, string name)
    {
        var symbol = FindSymbol(ns, name);
        return symbol?.Type == ExportedSymbolType.Function ? symbol : null;
    }

    /// <summary>
    /// Finds a class symbol by namespace and name.
    /// </summary>
    public SymbolDefinition? FindClass(string? ns, string name)
    {
        var symbol = FindSymbol(ns, name);
        return symbol?.Type == ExportedSymbolType.Class ? symbol : null;
    }

    /// <summary>
    /// Gets all symbols defined in a specific namespace.
    /// </summary>
    public IEnumerable<SymbolDefinition> GetSymbolsInNamespace(string ns)
    {
        _lock.EnterReadLock();
        try
        {
            var normalizedNs = ns?.ToLowerInvariant() ?? string.Empty;
            foreach (var kv in _symbols)
            {
                if (string.Equals(kv.Key.Namespace, normalizedNs, StringComparison.Ordinal))
                    yield return kv.Value;
            }
        }
        finally
        {
            _lock.ExitReadLock();
        }
    }

    /// <summary>
    /// Gets all symbols defined in a specific file.
    /// </summary>
    public IEnumerable<SymbolDefinition> GetSymbolsInFile(string filePath)
    {
        _lock.EnterReadLock();
        try
        {
            if (_fileIndex.TryGetValue(filePath, out var keys))
            {
                foreach (var key in keys.Keys)
                {
                    if (_symbols.TryGetValue(key, out var def))
                        yield return def;
                }
            }
        }
        finally
        {
            _lock.ExitReadLock();
        }
    }

    /// <summary>
    /// Removes all symbols defined in a specific file.
    /// Call this before reparsing a file to ensure stale symbols are removed.
    /// </summary>
    public void RemoveSymbolsFromFile(string filePath)
    {
        _lock.EnterWriteLock();
        try
        {
            if (!_fileIndex.TryRemove(filePath, out var keys))
                return;

            foreach (var key in keys.Keys)
            {
                if (_symbols.TryRemove(key, out var removed))
                {
                    // Remove from name index
                    var normalizedName = removed.Name.ToLowerInvariant();
                    if (_nameIndex.TryGetValue(normalizedName, out var nameKeys))
                    {
                        nameKeys.TryRemove(key, out _);
                        if (nameKeys.IsEmpty)
                            _nameIndex.TryRemove(normalizedName, out _);
                    }
                }
            }
        }
        finally
        {
            _lock.ExitWriteLock();
        }
    }

    /// <summary>
    /// Computes a hash of all symbols defined in a specific file.
    /// Used to detect if exported symbols changed during a reparse.
    /// </summary>
    public int GetSymbolsHashForFile(string filePath)
    {
        _lock.EnterReadLock();
        try
        {
            if (!_fileIndex.TryGetValue(filePath, out var keys))
                return 0;

            int hash = 17;
            foreach (var key in keys.Keys.OrderBy(k => k.Namespace).ThenBy(k => k.Name))
            {
                if (_symbols.TryGetValue(key, out var def))
                {
                    hash = hash * 31 + key.GetHashCode();
                    hash = hash * 31 + (def.FilePath?.GetHashCode() ?? 0);
                    hash = hash * 31 + def.Range.GetHashCode();
                    hash = hash * 31 + (def.Parameters?.Length ?? 0);
                }
            }
            return hash;
        }
        finally
        {
            _lock.ExitReadLock();
        }
    }

    /// <summary>
    /// Updates symbols for a file and returns whether any symbols actually changed.
    /// This is more efficient than RemoveSymbolsFromFile + AddOrUpdateSymbol for incremental updates.
    /// </summary>
    /// <param name="filePath">The file path being updated.</param>
    /// <param name="newSymbols">The new set of symbols for the file.</param>
    /// <returns>True if symbols changed (added, removed, or modified), false if identical.</returns>
    public bool UpdateSymbolsForFile(string filePath, IEnumerable<SymbolDefinition> newSymbols)
    {
        _lock.EnterWriteLock();
        try
        {
            var newSymbolsList = newSymbols.ToList();
            var newKeys = new HashSet<(string Namespace, string Name)>();
            bool changed = false;

            // Get existing symbols for this file
            _fileIndex.TryGetValue(filePath, out var existingKeys);
            var existingKeySet = existingKeys?.Keys.ToHashSet() ?? new HashSet<(string Namespace, string Name)>();

            // Add/update new symbols
            foreach (var def in newSymbolsList)
            {
                var key = NormalizeKey(def.Namespace, def.Name);
                newKeys.Add(key);

                // Check if symbol exists and is identical
                if (_symbols.TryGetValue(key, out var existing))
                {
                    if (!SymbolsEqual(existing, def))
                    {
                        changed = true;
                        _symbols[key] = def;
                    }
                }
                else
                {
                    // New symbol
                    changed = true;
                    _symbols[key] = def;

                    // Update name index
                    var nameSymbols = _nameIndex.GetOrAdd(def.Name.ToLowerInvariant(),
                        _ => new ConcurrentDictionary<(string Namespace, string Name), byte>());
                    nameSymbols[key] = 0;
                }
            }

            // Remove symbols that no longer exist in this file
            var removedKeys = existingKeySet.Except(newKeys).ToList();
            foreach (var key in removedKeys)
            {
                changed = true;
                if (_symbols.TryRemove(key, out var removed))
                {
                    // Remove from name index
                    var normalizedName = removed.Name.ToLowerInvariant();
                    if (_nameIndex.TryGetValue(normalizedName, out var nameKeys))
                    {
                        nameKeys.TryRemove(key, out _);
                        if (nameKeys.IsEmpty)
                            _nameIndex.TryRemove(normalizedName, out _);
                    }
                }
            }

            // Update file index
            if (newKeys.Count > 0)
            {
                var fileSymbols = _fileIndex.GetOrAdd(filePath,
                    _ => new ConcurrentDictionary<(string Namespace, string Name), byte>());
                fileSymbols.Clear();
                foreach (var key in newKeys)
                    fileSymbols[key] = 0;
            }
            else
            {
                _fileIndex.TryRemove(filePath, out _);
            }

            return changed;
        }
        finally
        {
            _lock.ExitWriteLock();
        }
    }

    /// <summary>
    /// Compares two symbol definitions for equality (ignoring IExportedSymbol reference).
    /// </summary>
    private static bool SymbolsEqual(SymbolDefinition? a, SymbolDefinition? b)
    {
        if (a is null || b is null)
        {
            return false;
        }

        // Handle potentially null Range.Start/End from default Range()
        var aStart = a.Range?.Start;
        var bStart = b.Range?.Start;
        var aEnd = a.Range?.End;
        var bEnd = b.Range?.End;

        if ((aStart is null) != (bStart is null) || (aEnd is null) != (bEnd is null))
            return false;

        bool rangeEqual = (aStart is null && bStart is null) ||
                          (aStart?.Line == bStart?.Line && aStart?.Character == bStart?.Character);
        rangeEqual &= (aEnd is null && bEnd is null) ||
                      (aEnd?.Line == bEnd?.Line && aEnd?.Character == bEnd?.Character);

        return a.Namespace == b.Namespace &&
               a.Name == b.Name &&
               a.Type == b.Type &&
               string.Equals(a.FilePath, b.FilePath, StringComparison.OrdinalIgnoreCase) &&
               rangeEqual &&
               SequenceEqual(a.Parameters, b.Parameters) &&
               SequenceEqual(a.Flags, b.Flags) &&
               a.Documentation == b.Documentation;
    }

    private static bool SequenceEqual(string[]? a, string[]? b)
    {
        if (a is null && b is null) return true;
        if (a is null || b is null) return false;
        if (a.Length != b.Length) return false;
        for (int i = 0; i < a.Length; i++)
        {
            if (a[i] != b[i]) return false;
        }
        return true;
    }

    /// <summary>
    /// Gets the total number of symbols in the registry.
    /// </summary>
    public int Count
    {
        get
        {
            _lock.EnterReadLock();
            try
            {
                return _symbols.Count;
            }
            finally
            {
                _lock.ExitReadLock();
            }
        }
    }

    /// <summary>
    /// Gets all symbols in the registry.
    /// </summary>
    public IEnumerable<SymbolDefinition> GetAllSymbols()
    {
        _lock.EnterReadLock();
        try
        {
            // Return a snapshot to avoid enumeration issues
            return _symbols.Values.ToList();
        }
        finally
        {
            _lock.ExitReadLock();
        }
    }

    /// <summary>
    /// Checks if a namespace exists in the registry.
    /// </summary>
    public bool NamespaceExists(string ns)
    {
        _lock.EnterReadLock();
        try
        {
            var normalizedNs = ns?.ToLowerInvariant() ?? string.Empty;
            return _symbols.Keys.Any(k => string.Equals(k.Namespace, normalizedNs, StringComparison.Ordinal));
        }
        finally
        {
            _lock.ExitReadLock();
        }
    }

    /// <summary>
    /// Gets all known namespaces.
    /// </summary>
    public IEnumerable<string> GetAllNamespaces()
    {
        _lock.EnterReadLock();
        try
        {
            return _symbols.Keys.Select(k => k.Namespace).Distinct().ToList();
        }
        finally
        {
            _lock.ExitReadLock();
        }
    }

    private void RemoveFromFileIndex(string filePath, (string Namespace, string Name) key)
    {
        if (_fileIndex.TryGetValue(filePath, out var keys))
        {
            keys.TryRemove(key, out _);
            if (keys.IsEmpty)
                _fileIndex.TryRemove(filePath, out _);
        }
    }

    #region ISymbolLocationProvider Implementation

    /// <inheritdoc/>
    (string FilePath, Range Range)? ISymbolLocationProvider.FindFunctionLocation(string? ns, string name)
    {
        var symbol = ns is not null ? FindSymbol(ns, name) : FindSymbolByNameOnly(name);
        if (symbol is not null && symbol.Type == ExportedSymbolType.Function)
            return (symbol.FilePath, symbol.Range);
        return null;
    }

    /// <inheritdoc/>
    (string FilePath, Range Range)? ISymbolLocationProvider.FindClassLocation(string? ns, string name)
    {
        var symbol = ns is not null ? FindSymbol(ns, name) : FindSymbolByNameOnly(name);
        if (symbol is not null && symbol.Type == ExportedSymbolType.Class)
            return (symbol.FilePath, symbol.Range);
        return null;
    }

    /// <inheritdoc/>
    (string FilePath, Range Range)? ISymbolLocationProvider.FindFunctionLocationAnyNamespace(string name)
    {
        var symbol = FindSymbolByNameOnly(name);
        if (symbol is not null && symbol.Type == ExportedSymbolType.Function)
            return (symbol.FilePath, symbol.Range);
        return null;
    }

    /// <inheritdoc/>
    (string FilePath, Range Range)? ISymbolLocationProvider.FindClassLocationAnyNamespace(string name)
    {
        var symbol = FindSymbolByNameOnly(name);
        if (symbol is not null && symbol.Type == ExportedSymbolType.Class)
            return (symbol.FilePath, symbol.Range);
        return null;
    }

    /// <inheritdoc/>
    string[]? ISymbolLocationProvider.GetFunctionParameters(string ns, string name)
    {
        var symbol = FindSymbol(ns, name);
        return symbol?.Type == ExportedSymbolType.Function ? symbol.Parameters : null;
    }

    /// <inheritdoc/>
    string[]? ISymbolLocationProvider.GetFunctionFlags(string ns, string name)
    {
        var symbol = FindSymbol(ns, name);
        return symbol?.Type == ExportedSymbolType.Function ? symbol.Flags : null;
    }

    /// <inheritdoc/>
    string? ISymbolLocationProvider.GetFunctionDoc(string ns, string name)
    {
        var symbol = FindSymbol(ns, name);
        return symbol?.Type == ExportedSymbolType.Function ? symbol.Documentation : null;
    }

    #endregion
}
