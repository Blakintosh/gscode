namespace GSCode.Parser.SA;

public enum SymbolKind
{
    Function,
    Method,
    Class,
    Variable // local variable (function-scoped)
}

public readonly record struct SymbolKey(
    SymbolKind Kind,
    string Namespace,
    string Name,
    string? ClassName = null,
    string? ScopeId = null // for variables: typically "ns::functionName"
)
{
    public override string ToString() => Kind switch
    {
        SymbolKind.Method => $"method {Namespace}::{ClassName}::{Name}",
        SymbolKind.Function => $"function {Namespace}::{Name}",
        SymbolKind.Class => $"class {Namespace}::{Name}",
        SymbolKind.Variable => $"var {ScopeId ?? Namespace}::{Name}",
        _ => $"{Namespace}::{Name}"
    };
}