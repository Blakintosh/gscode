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
    InactiveConditionalBranch = 2013,

    // Parsing (3xxx)
    ExpectedToken = 3000,
    ExpectedDeclaration = 3001,
    ExpectedExpression = 3002,
    ExpectedStatement = 3003,
    ExpectedParameterName = 3004,
    ExpectedClassMember = 3005,
    ExpectedCaseLabel = 3006,
    UnterminatedBlock = 3007,
    UnterminatedDevBlock = 3008,
    UsingAfterDeclaration = 3009,
    ExpectedScriptPath = 3010,
    ExpectedNamespaceName = 3011,

    // Extraction / per-file semantics (4xxx)
    UnknownPrecacheType = 4000,
    WrongPrecacheArgumentCount = 4001,
    ConstructorHasParameters = 4002,
    DestructorHasParameters = 4003,
    NonValueDefaultParameter = 4004,
    DuplicateFunction = 4005,

    // Cross-file / workspace semantics (5xxx)
    NamespaceNotImported = 5000,
    UnusedUsing = 5001,
    PreferBooleanLiteral = 5002,
    PrivateFunctionNotVisible = 5003,
}
