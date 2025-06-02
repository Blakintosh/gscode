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
    private Dictionary<string, IExportedSymbol> ExportedSymbolTable { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, ScrVariable> VariableSymbols { get; } = new(StringComparer.OrdinalIgnoreCase);

    private static HashSet<string> ReservedSymbols { get; } = new(StringComparer.OrdinalIgnoreCase)
    {
        "waittill",
        "notify",
        "isdefined",
        "endon"
    };

    public int LexicalScope { get; } = 0;

    public SymbolTable(Dictionary<string, IExportedSymbol> exportedSymbolTable, Dictionary<string, ScrVariable> inSet, int lexicalScope)
    {
        ExportedSymbolTable = exportedSymbolTable;
        VariableSymbols = inSet;
        LexicalScope = lexicalScope;
    }

    /// <summary>
    /// Adds or sets the symbol on the symbol table, returns true if was newly added.
    /// </summary>
    /// <param name="symbol">The symbol name</param>
    /// <param name="data">The value</param>
    /// <returns>true if new, false if not, null if assignment to a constant</returns>
    public AssignmentResult AddOrSetSymbol(string symbol, ScrData data)
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
        return TryAddSymbol(symbol, data);
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
    public AssignmentResult TryAddSymbol(string symbol, ScrData data)
    {
        // Check if the symbol exists in the table
        if (VariableSymbols.ContainsKey(symbol))
        {
            return AssignmentResult.Failed;
        }
        // If the symbol is reserved, block the assignment.
        if (ReservedSymbols.Contains(symbol))
        {
            return AssignmentResult.FailedReserved;
        }

        // If the symbol doesn't exist, add it to the top-level scope
        VariableSymbols.Add(symbol, new ScrVariable(symbol, data.Copy(), LexicalScope));
        return AssignmentResult.SuccessNew;
    }

    /// <summary>
    /// Tries to get the associated ScrData for a symbol if it exists.
    /// </summary>
    /// <param name="symbol">The symbol to look for</param>
    /// <returns>The associated ScrData if the symbol exists, null otherwise</returns>
    public ScrData? TryGetSymbol(string symbol, out SymbolFlags flags)
    {
        flags = SymbolFlags.None;

        // Reserved takes precedence over local variables, and locals take precedence over globals.


        // TODO: not sure I'm happy with this, we might want to just syntactically enforce the special functions.
        // If the symbol is reserved, return this.
        if (ReservedSymbols.Contains(symbol))
        {
            flags = SymbolFlags.Global | SymbolFlags.Reserved | SymbolFlags.BuiltIn;
            return new ScrData(ScrDataTypes.Function);
        }

        // Check if the symbol exists in the global table
        if (VariableSymbols.TryGetValue(symbol, out ScrVariable? globalData))
        {
            if (globalData.Global)
            {
                flags = SymbolFlags.Global;
            }
            return globalData.Data!;
        }

        // Check if the symbol is an exported symbol.
        if (ExportedSymbolTable.TryGetValue(symbol, out IExportedSymbol? exportedSymbol))
        {
            flags = SymbolFlags.Global;
            return new ScrData(exportedSymbol.Type switch
            {
                ExportedSymbolType.Function => ScrDataTypes.Function,
                // ExportedSymbolType.Class => ScrDataTypes.Class,
                _ => ScrDataTypes.Undefined
            }, exportedSymbol.Type switch
            {
                ExportedSymbolType.Function => (ScrFunction)exportedSymbol,
                // ExportedSymbolType.Class => exportedSymbol.Get<ScrClass>(),
                _ => null
            });
        }

        // If the symbol doesn't exist, return undefined.
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