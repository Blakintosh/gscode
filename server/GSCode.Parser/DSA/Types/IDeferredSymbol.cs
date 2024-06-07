using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace GSCode.Parser.DSA.Types;

public enum DeferredSymbolTypes
{
    Function,
    Class
}

/// <summary>
/// Deferred symbol interface
/// Used in the case where the value or validity of them cannot be assessed when the traditional
/// static analyser runs. For example, requests for dependencies (which issue exported functions)
/// only occurs after the analyser, and function declarations are resolved during SA. As GSC enforces
/// no "declaration before invocation" rule, the analysis of function calls must be deferred, and so
/// do classes.
/// </summary>
public interface IDeferredSymbol
{
    public string Name { get; }
    public DeferredSymbolTypes Type { get; }
}
