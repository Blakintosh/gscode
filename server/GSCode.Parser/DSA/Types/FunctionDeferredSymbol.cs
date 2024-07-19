using GSCode.Parser.DFA;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace GSCode.Parser.DSA.Types;

internal class FunctionDeferredSymbol : IDeferredSymbol
{
    public string Name { get; }
    public string? Namespace { get; }
    public ScrArguments? Arguments { get; }

    public DeferredSymbolTypes Type { get; } = DeferredSymbolTypes.Function;

    public FunctionDeferredSymbol(string name, ScrArguments? arguments = null) : this(name, null, arguments) { }

    public FunctionDeferredSymbol(string name, string? nameSpace, ScrArguments? arguments = null)
    {
        Name = name;
        Namespace = nameSpace;
        Arguments = arguments;
    }
}
