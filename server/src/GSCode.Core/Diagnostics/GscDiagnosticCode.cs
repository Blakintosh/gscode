namespace GSCode.Core.Diagnostics;

/// <summary>
/// Every diagnostic the server can produce, one stable code per condition.
/// Message templates live in <see cref="DiagnosticMessages"/>. Grows phase by phase.
/// </summary>
public enum GscDiagnosticCode
{
    // Lexing (1xxx)
    UnterminatedString = 1000,
    UnterminatedBlockComment = 1001,
    UnterminatedDocComment = 1002,
    UnexpectedCharacter = 1003,
    UnknownDirective = 1004,
}
