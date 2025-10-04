using GSCode.Parser.DFA;
using GSCode.Parser.DSA.Types;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using static System.Runtime.InteropServices.JavaScript.JSType;

namespace GSCode.Parser.SPA.Logic.Components;

internal enum AssignmentResult
{
    // Successful - was a new symbol
    SuccessNew,
    // Successful - mutated an existing symbol
    SuccessMutated,
    // Failed - the symbol exists and is a constant
    FailedConstant,
    // Failed - the symbol is reserved (isdefined, etc.)
    FailedReserved,
    // Failed for unknown reason (shouldn't be hit)
    Failed
};

[Flags]
internal enum SymbolFlags
{
    None = 0,
    Global = 1 << 0,
    BuiltIn = 1 << 1,
    Reserved = 1 << 2
}

internal class SymbolTable
{
    private Dictionary<string, IExportedSymbol> GlobalSymbolTable { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, ScrVariable> VariableSymbols { get; } = new(StringComparer.OrdinalIgnoreCase);
    private ScriptAnalyserData? ApiData { get; }

    private static HashSet<string> ReservedSymbols { get; } = new(StringComparer.OrdinalIgnoreCase)
    {
        "waittill",
        "notify",
        "isdefined",
        "endon"
    };

    public int LexicalScope { get; } = 0;

    public SymbolTable(Dictionary<string, IExportedSymbol> exportedSymbolTable, Dictionary<string, ScrVariable> inSet, int lexicalScope, ScriptAnalyserData? apiData = null)
    {
        GlobalSymbolTable = exportedSymbolTable;
        VariableSymbols = inSet;
        LexicalScope = lexicalScope;
        ApiData = apiData;
    }

    /// <summary>
    /// Adds or sets the variable symbol on the symbol table, returns true if was newly added.
    /// </summary>
    /// <param name="symbol">The symbol name</param>
    /// <param name="data">The value</param>
    /// <returns>true if new, false if not, null if assignment to a constant</returns>
    public AssignmentResult AddOrSetVariableSymbol(string symbol, ScrData data)
    {
        if (ContainsSymbol(symbol))
        {
            // Check they're not assigning to a constant
            if (SymbolIsConstant(symbol))
            {
                return AssignmentResult.FailedConstant;
            }

            // Re-assign
            SetSymbol(symbol, data);
            return AssignmentResult.SuccessMutated;
        }
        return TryAddVariableSymbol(symbol, data);
    }

    public bool ContainsConstant(string symbol)
    {
        if (!ContainsSymbol(symbol))
        {
            return false;
        }

        return SymbolIsConstant(symbol);
    }

    public bool ContainsSymbol(string symbol)
    {
        // Check if the symbol exists in the table
        if (VariableSymbols.ContainsKey(symbol))
        {
            return true;
        }

        return false;
    }

    /// <summary>
    /// Tries to add a symbol, by copying it, to the top-level scope. Returns true if the symbol was added successfully.
    /// </summary>
    /// <param name="symbol">The symbol to add</param>
    /// <param name="data">The associated ScrData for the symbol</param>
    /// <returns>true if the symbol was added, false if it already exists</returns>
    public AssignmentResult TryAddVariableSymbol(string symbol, ScrData data)
    {
        // Check if the symbol exists in the table
        if (VariableSymbols.ContainsKey(symbol))
        {
            return AssignmentResult.Failed;
        }

        // If the symbol is reserved, block the assignment.
        // Technically GSC treats this as a syntax error, but IMO it's more intuitive to use a semantic error.
        if (ReservedSymbols.Contains(symbol))
        {
            return AssignmentResult.FailedReserved;
        }

        // If the symbol doesn't exist, add it to the top-level scope
        VariableSymbols.Add(symbol, new ScrVariable(symbol, data.Copy(), LexicalScope));
        return AssignmentResult.SuccessNew;
    }

    /// <summary>
    /// Tries to get the associated ScrData for a local variable if it exists.
    /// This is used for normal identifier references (e.g., a = b, where b is looked up in locals).
    /// </summary>
    /// <param name="symbol">The symbol to look for</param>
    /// <returns>The associated ScrData if the symbol exists, null otherwise</returns>
    public ScrData TryGetLocalVariable(string symbol, out SymbolFlags flags)
    {
        flags = SymbolFlags.None;

        // Check if the symbol exists in the local variable table
        if (VariableSymbols.TryGetValue(symbol, out ScrVariable? localData))
        {
            if (localData.Global)
            {
                flags = SymbolFlags.Global;
            }
            return localData.Data!;
        }

        // Handle built-in implicit globals that are always available
        if (symbol.Equals("self", StringComparison.OrdinalIgnoreCase))
        {
            flags = SymbolFlags.Global | SymbolFlags.BuiltIn;
            return new ScrData(ScrDataTypes.Entity, ScrStruct.NonDeterministic());
        }
        if (symbol.Equals("level", StringComparison.OrdinalIgnoreCase))
        {
            flags = SymbolFlags.Global | SymbolFlags.BuiltIn;
            return new ScrData(ScrDataTypes.Entity, ScrStruct.NonDeterministic());
        }
        if (symbol.Equals("game", StringComparison.OrdinalIgnoreCase))
        {
            flags = SymbolFlags.Global | SymbolFlags.BuiltIn;
            return new ScrData(ScrDataTypes.Array);
        }
        if (symbol.Equals("anim", StringComparison.OrdinalIgnoreCase))
        {
            flags = SymbolFlags.Global | SymbolFlags.BuiltIn;
            return new ScrData(ScrDataTypes.Entity, ScrStruct.NonDeterministic());
        }

        // If the symbol doesn't exist, return undefined.
        return ScrData.Undefined();
    }

    /// <summary>
    /// Tries to get the associated ScrData for a function if it exists.
    /// This is used for function calls (e.g., b()), function pointers (e.g., &b), and namespaced functions.
    /// All functions are global - looks up in the global symbol table, then API functions as fallback.
    /// Reserved functions (waittill, notify, isdefined, endon) take precedence.
    /// </summary>
    /// <param name="symbol">The function symbol to look for</param>
    /// <param name="flags">The flags for the symbol</param>
    /// <returns>The associated ScrData if the function exists, undefined otherwise</returns>
    public ScrData TryGetFunction(string symbol, out SymbolFlags flags)
    {
        flags = SymbolFlags.None;

        // 1. Reserved functions take precedence
        if (ReservedSymbols.Contains(symbol))
        {
            flags = SymbolFlags.Global | SymbolFlags.Reserved | SymbolFlags.BuiltIn;
            return new ScrData(ScrDataTypes.Function);
        }

        // 2. Check global symbol table (script-defined functions)
        if (GlobalSymbolTable.TryGetValue(symbol, out IExportedSymbol? exportedSymbol))
        {
            if (exportedSymbol.Type == ExportedSymbolType.Function)
            {
                flags = SymbolFlags.Global;
                return new ScrData(ScrDataTypes.Function, (ScrFunction)exportedSymbol);
            }
            else if (exportedSymbol.Type == ExportedSymbolType.Class)
            {
                flags = SymbolFlags.Global;
                // TODO: needs data
                return new ScrData(ScrDataTypes.Object, null);
            }
        }

        // 3. Check API functions (built-in library functions)
        if (ApiData is not null)
        {
            ScrFunction? apiFunction = ApiData.GetApiFunction(symbol);
            if (apiFunction is not null)
            {
                flags = SymbolFlags.Global | SymbolFlags.BuiltIn;
                return new ScrData(ScrDataTypes.Function, apiFunction);
            }
        }

        // If the function doesn't exist, return undefined
        return ScrData.Undefined();
    }

    /// <summary>
    /// Tries to get the associated ScrData for a function symbol if it exists.
    /// </summary>
    /// <param name="symbol">The symbol to look for</param>
    /// <param name="flags">The flags for the symbol</param>
    /// <returns>The associated ScrData if the symbol exists, null otherwise</returns>
    public ScrData TryGetFunctionSymbol(string symbol, out SymbolFlags flags)
    {
        flags = SymbolFlags.None;

        if (GlobalSymbolTable.TryGetValue(symbol, out IExportedSymbol? exportedSymbol))
        {
            if (exportedSymbol.Type == ExportedSymbolType.Function)
            {
                flags = SymbolFlags.Global;
                return new ScrData(ScrDataTypes.Function, (ScrFunction)exportedSymbol);
            }
        }

        return ScrData.Undefined();
    }

    /// <summary>
    /// Tries to get the associated ScrData for a namespaced function symbol if it exists.
    /// </summary>
    /// <param name="namespaceName">The namespace to look in</param>
    /// <param name="symbol">The symbol to look for</param>
    /// <param name="flags">The flags for the symbol</param>
    /// <returns>The associated ScrData if the symbol exists, null otherwise</returns>
    public ScrData TryGetNamespacedFunctionSymbol(string namespaceName, string symbol, out SymbolFlags flags)
    {
        flags = SymbolFlags.None;

        // Check global symbol table first
        if (GlobalSymbolTable.TryGetValue($"{namespaceName}::{symbol}", out IExportedSymbol? exportedSymbol))
        {
            if (exportedSymbol.Type == ExportedSymbolType.Function && ((ScrFunction)exportedSymbol).Namespace == namespaceName)
            {
                flags = SymbolFlags.Global;
                return new ScrData(ScrDataTypes.Function, (ScrFunction)exportedSymbol);
            }
        }

        // Check if namespace is "sys" and lookup in API
        if (namespaceName.Equals("sys", StringComparison.OrdinalIgnoreCase) && ApiData is not null)
        {
            ScrFunction? apiFunction = ApiData.GetApiFunction(symbol);
            if (apiFunction is not null)
            {
                flags = SymbolFlags.Global | SymbolFlags.BuiltIn;
                return new ScrData(ScrDataTypes.Function, apiFunction);
            }
        }

        return ScrData.Undefined();
    }

    /// <summary>
    /// Sets the value for the symbol to a copy of the provided ScrData.
    /// </summary>
    /// <param name="symbol">The symbol to look for</param>
    /// <returns>The associated ScrData if the symbol exists, null otherwise</returns>
    public void SetSymbol(string symbol, ScrData value)
    {
        ScrData scrData = value.Copy();

        // Check if the symbol exists in the table, set it there if so
        if (VariableSymbols.TryGetValue(symbol, out ScrVariable? existing))
        {
            VariableSymbols[symbol] = new ScrVariable(symbol, scrData, existing!.LexicalScope);
            return;
        }
    }

    public bool SymbolIsConstant(string symbol)
    {
        // Check if the symbol exists in the table
        if (VariableSymbols.ContainsKey(symbol))
        {
            return VariableSymbols[symbol].Data.ReadOnly;
        }
        return false;
    }
}