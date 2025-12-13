using System;
using System.Collections.Generic;
using GSCode.Data.Models;
using GSCode.Data.Models.Interfaces;
using GSCode.Parser.DFA;
using GSCode.Parser.SPA.Logic.Components;
using Xunit;

namespace GSCode.Tests;

public class SymbolTableTests
{
    [Fact]
    public void TryGetNamespacedFunctionSymbol_IsCaseInsensitive()
    {
        Dictionary<string, IExportedSymbol> exported = new(StringComparer.OrdinalIgnoreCase);
        ScrFunction function = new()
        {
            Name = "DoThing",
            Namespace = "CustomNamespace",
            Overloads = [new ScrFunctionOverload()]
        };

        exported.Add("CustomNamespace::DoThing", function);
        exported.Add("DoThing", function);

        HashSet<string> knownNamespaces = new(StringComparer.OrdinalIgnoreCase) { "CustomNamespace" };
        SymbolTable table = new(exported, new Dictionary<string, ScrVariable>(StringComparer.OrdinalIgnoreCase), 0, knownNamespaces: knownNamespaces);

        ScrData result = table.TryGetNamespacedFunctionSymbol("customnamespace", "dothing", out SymbolFlags flags, out bool namespaceExists);

        Assert.Equal(ScrDataTypes.Function, result.Type);
        Assert.Same(function, result.Get<ScrFunction>());
        Assert.True(flags.HasFlag(SymbolFlags.Global));
        Assert.True(namespaceExists);
    }
}

