using GSCode.Parser.DSA.Types;
using GSCode.Parser.SPA.Sense;
using Newtonsoft.Json.Linq;
using Serilog;
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
    private Dictionary<string, ScrVariable> GlobalTable { get; } = new();
    private Dictionary<string, ScrVariable> LocalTable { get; } = new();
    public int Depth { get; } = 0;


    public SymbolTable(IEnumerable<IExportedSymbol> symbols)
    {
        // TODO: Should be a dictionary, and checked for duplicates.
        // Does it need to be checked for duplicates? I don't think so.
        foreach(IExportedSymbol symbol in symbols)
        {
            if(ExportedSymbolTable.ContainsKey(symbol.Name))
            {
                //throw new Exception($"Duplicate symbol {symbol.Name} found in symbol table.");
                //Log.Warning($"Duplicate symbol {symbol.Name}");
                continue;
            }
            ExportedSymbolTable.Add(symbol.Name, symbol);
        }
    }

    public SymbolTable(Dictionary<string, IExportedSymbol> symbols, int depth)
    {
        ExportedSymbolTable = symbols;
        Depth = depth;
    }
    public SymbolTable(SymbolTable symbolTable, int depth) : this(symbolTable.ExportedSymbolTable, depth) { }

    public SymbolTable Clone(int? depth = default)
    {
        SymbolTable symbolTable = new(ExportedSymbolTable, depth ?? 0);

        foreach(KeyValuePair<string, ScrVariable> symbol in GlobalTable)
        {
            symbolTable.GlobalTable.Add(symbol.Key, symbol.Value);
        }

        foreach(KeyValuePair<string, ScrVariable> symbol in LocalTable)
        {
            symbolTable.LocalTable.Add(symbol.Key, symbol.Value);
        }

        return symbolTable;
    }

    public void MarkSymbolsAsSplit()
    {
        foreach(KeyValuePair<string, ScrVariable> symbol in GlobalTable)
        {
            symbol.Value.MarkSplit();
        }

        foreach(KeyValuePair<string, ScrVariable> symbol in LocalTable)
        {
            symbol.Value.MarkSplit();
        }
    }

    public void UnmarkSymbolsAsSplit()
    {
        foreach(KeyValuePair<string, ScrVariable> symbol in GlobalTable)
        {
            symbol.Value.UnmarkSplit();
        }

        foreach(KeyValuePair<string, ScrVariable> symbol in LocalTable)
        {
            symbol.Value.UnmarkSplit();
        }
    }

    public void AddIncomingSymbols(SymbolTable symbolTable, bool isSplit = false)
    {
        foreach (KeyValuePair<string, ScrVariable> symbol in symbolTable.GlobalTable)
        {
            if(GlobalTable.ContainsKey(symbol.Key))
            {
                // Union, merge the two symbols
                ScrVariable globalSymbol = GlobalTable[symbol.Key];

                ScrVariable newSymbol = ScrVariable.Merge(globalSymbol, symbol.Value);

                GlobalTable[symbol.Key] = newSymbol;
                continue;
            }
            GlobalTable.Add(symbol.Key, symbol.Value);
        }

        foreach(KeyValuePair<string, ScrVariable> symbol in symbolTable.LocalTable)
        {
            ScrVariable scrSymbol = symbol.Value;
            // Only add symbols that are at the same scope or higher
            if(scrSymbol.Depth <= Depth)
            {
                if(LocalTable.ContainsKey(symbol.Key))
                {
                    // Union, merge the two symbols
                    ScrVariable localSymbol = LocalTable[symbol.Key];

                    ScrVariable newSymbol = ScrVariable.Merge(localSymbol, symbol.Value);

                    LocalTable[symbol.Key] = newSymbol;
                    continue;
                }
                LocalTable.Add(symbol.Key, scrSymbol);
            }
        }
    }

    /// <summary>
    /// Adds or sets the symbol on the symbol table, returns true if was newly added.
    /// </summary>
    /// <param name="symbol">The symbol name</param>
    /// <param name="data">The value</param>
    /// <returns>true if new, false if not, null if assignment to a constant</returns>
    public bool AddOrSetSymbol(string symbol, ScrData data, bool isConstant = false)
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
        return TryAddSymbol(symbol, data, isConstant);
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
        // Check if the symbol exists in the global table
        if (GlobalTable.ContainsKey(symbol))
        {
            return true;
        }

        // Check if the symbol exists in the local table
        if (LocalTable.ContainsKey(symbol))
        {
            return true;
        }

        return false;
    }

    /// <summary>
    /// Tries to add a symbol to the top-level scope. Returns true if the symbol was added successfully.
    /// </summary>
    /// <param name="symbol">The symbol to add</param>
    /// <param name="data">The associated ScrData for the symbol</param>
    /// <returns>true if the symbol was added, false if it already exists</returns>
    public bool TryAddSymbol(string symbol, ScrData data, bool isConstant = false)
    {
        // Check if the symbol exists in the global table
        if (GlobalTable.ContainsKey(symbol))
        {
            return false;
        }

        // Check if the symbol exists in the local table
        if (LocalTable.ContainsKey(symbol))
        {
            return false;
        }

        // If the symbol doesn't exist, add it to the top-level scope
        LocalTable.Add(symbol, new ScrVariable(symbol, data, Depth, isConstant));
        return true;
    }

    /// <summary>
    /// Tries to get the associated ScrData for a symbol if it exists.
    /// </summary>
    /// <param name="symbol">The symbol to look for</param>
    /// <returns>The associated ScrData if the symbol exists, null otherwise</returns>
    public ScrData? TryGetSymbol(string symbol)
    {
        // Check if the symbol exists in the global table
        if (GlobalTable.TryGetValue(symbol, out ScrVariable? globalData))
        {
            return globalData!;
        }

        // Check if the symbol exists in the local table
        if (LocalTable.TryGetValue(symbol, out ScrVariable? localData))
        {
            return localData!;
        }

        // If the symbol doesn't exist, return undefined.
        return ScrVariable.Undefined(symbol);
    }

    /// <summary>
    /// Sets the value for the symbol.
    /// </summary>
    /// <param name="symbol">The symbol to look for</param>
    /// <returns>The associated ScrData if the symbol exists, null otherwise</returns>
    public void SetSymbol(string symbol, ScrData value)
    {
        // Check if the symbol exists in the global table, set it there if so
        if (GlobalTable.TryGetValue(symbol, out ScrVariable? existing))
        {
            GlobalTable[symbol] = new ScrVariable(symbol, value, existing!.Depth);
            return;
        }

        // Check if the symbol exists in the local table, set it there if so
        if (LocalTable.TryGetValue(symbol, out ScrVariable? existing2))
        {
            LocalTable[symbol] = new ScrVariable(symbol, value, existing2.Depth);
            return;
        }
    }

    public bool SymbolIsConstant(string symbol)
    {
        // Check if the symbol exists in the global table
        if (GlobalTable.ContainsKey(symbol))
        {
            return GlobalTable[symbol].ReadOnly;
        }

        // Check if the symbol exists in the local table
        if (LocalTable.ContainsKey(symbol))
        {
            return LocalTable[symbol].ReadOnly;
        }
        return false;
    }
}
