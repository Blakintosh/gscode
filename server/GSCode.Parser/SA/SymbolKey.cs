namespace GSCode.Parser.SA;

public enum SymbolKind
{
    Function,
    Method,
    Class
}

public readonly record struct SymbolKey(SymbolKind Kind, string Namespace, string Name, string? ClassName = null)
{
    public override string ToString() => Kind switch
    {
        SymbolKind.Method => $"method {Namespace}::{ClassName}::{Name}",
        SymbolKind.Function => $"function {Namespace}::{Name}",
        SymbolKind.Class => $"class {Namespace}::{Name}",
        _ => $"{Namespace}::{Name}"
    };
}