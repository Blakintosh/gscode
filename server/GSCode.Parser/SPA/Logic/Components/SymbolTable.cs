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

internal class SymbolTable
{
    private Dictionary<string, IExportedSymbol> ExportedSymbolTable { get; } = new();
    public Dictionary<string, ScrVariable> VariableSymbols { get; } = new();

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
    public bool AddOrSetSymbol(string symbol, ScrData data)
    {
        if(ContainsSymbol(symbol))
        {
            // Check they're not assigning to a constant
            if (SymbolIsConstant(symbol))
            {
                return false;
            }

            // Re-assign
            SetSymbol(symbol, data);
            return false;
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
    public bool TryAddSymbol(string symbol, ScrData data)
    {
        // Check if the symbol exists in the table
        if (VariableSymbols.ContainsKey(symbol))
        {
            return false;
        }

        // If the symbol doesn't exist, add it to the top-level scope
        VariableSymbols.Add(symbol, new ScrVariable(symbol, data.Copy(), LexicalScope));
        return true;
    }

    /// <summary>
    /// Tries to get the associated ScrData for a symbol if it exists.
    /// </summary>
    /// <param name="symbol">The symbol to look for</param>
    /// <returns>The associated ScrData if the symbol exists, null otherwise</returns>
    public ScrData? TryGetSymbol(string symbol, out bool isGlobal)
    {
        isGlobal = false;

        // Check if the symbol exists in the global table
        if (VariableSymbols.TryGetValue(symbol, out ScrVariable? globalData))
        {
            isGlobal = globalData.Global;
            return globalData.Data!;
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