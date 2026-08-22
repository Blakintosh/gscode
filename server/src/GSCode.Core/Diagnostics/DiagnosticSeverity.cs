namespace GSCode.Core.Diagnostics;

/// <summary>Diagnostic severity. Values match the LSP wire encoding so mapping is a cast.</summary>
public enum DiagnosticSeverity
{
    Error = 1,
    Warning = 2,
    Information = 3,
    Hint = 4,
}
