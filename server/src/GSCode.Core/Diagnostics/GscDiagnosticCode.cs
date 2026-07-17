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

    // Preprocessing (2xxx)
    ExpectedMacroName = 2000,
    UnterminatedMacroParameters = 2001,
    InvalidLineContinuation = 2002,
    MissingInsertPath = 2003,
    InsertMissingSemicolon = 2004,
    InvalidInsertPath = 2005,
    InsertNotFound = 2006,
    InsertTooDeep = 2007,
    InsertCycle = 2008,
    UnterminatedConditionalDirective = 2009,
    UnexpectedConditionalDirective = 2010,
    MissingMacroArguments = 2011,
    UnterminatedMacroArguments = 2012,
}
