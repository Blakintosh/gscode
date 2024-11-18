using Microsoft.VisualStudio.LanguageServer.Protocol;
using System;
using System.Collections.Generic;

namespace GSCode.Data;

internal record class DiagnosticCode(string Message, DiagnosticSeverity Category, DiagnosticTag[]? Tags = null);

public static class DiagnosticSources
{
    public const string Lexer = "gscode-lex"; // Lexical token creation
    public const string Preprocessor = "gscode-mac"; // Preprocessor transformations
    public const string Ast = "gscode-ast"; // Syntax tree generation
    public const string Spa = "gscode-spa"; // Static program analysis
    public const string Ide = "gscode-ide"; // IDE/Language Server enforced conventions
}

public enum GSCErrorCodes
{
    // 1xxx errors are issued by the preprocessor
    ExpectedPreprocessorToken = 1000,
    UnexpectedCharacter = 1001,
    ExpectedInsertPath = 1002,
    ExpectedMacroParameter = 1003,
    DuplicateMacroParameter = 1004,
    MissingInsertFile = 1005,
    TooManyMacroArguments = 1006,
    TooFewMacroArguments = 1007,
    MisplacedPreprocessorDirective = 1008,
    MultilineStringLiteral = 1009,
    ExpectedMacroIdentifier = 1010,
    UnterminatedPreprocessorDirective = 1011,
    InvalidInsertPath = 1012,
    InvalidLineContinuation = 1013,
    DuplicateMacroDefinition = 1014,
    UserDefinedMacroIgnored = 1015,
    MissingMacroParameterList = 1016,
    InactivePreprocessorBranch = 1017,

    // 2xxx errors are issued by the parser
    ExpectedPathSegment = 2000,
    ExpectedSemiColon = 2001,
    UnexpectedUsing = 2002,
    ExpectedScriptDefn = 2003,
    ExpectedToken = 2004,
    ExpectedPrecacheType = 2005,
    ExpectedPrecachePath = 2006,
    ExpectedAnimTreeName = 2007,
    ExpectedNamespaceIdentifier = 2008,
    ExpectedFunctionIdentifier = 2009,
    UnexpectedFunctionModifier = 2010,
    ExpectedParameterIdentifier = 2011,
    ExpectedConstantIdentifier = 2012,
    ExpectedForeachIdentifier = 2013,
    ExpectedAssignmentOperator = 2014,
    ExpectedClassIdentifier = 2015,
    ExpectedMethodIdentifier = 2016,
    ExpectedFunctionQualification = 2017,
    ExpectedExpressionTerm = 2018,
    ExpectedConstructorParenthesis = 2019,
    UnexpectedConstructorParameter = 2020,
    ExpectedClassBodyDefinition = 2021,
    ExpectedMemberIdentifier = 2022,

    // 3xxx errors are issued by static analysis
    ObjectTokenNotValid = 3000,
    InvalidDereference = 3001,
    DuplicateModifier = 3002,
    IdentifierExpected = 3003,
    IntegerTooLarge = 3004,
    OperatorNotSupportedOnTypes = 3005,
    CannotAssignToConstant = 3006,
    StoreFunctionAsPointer = 3007,
    IntegerTooSmall = 3008,
    MissingAccompanyingConditional = 3009,
    RedefinitionOfSymbol = 3010,
    InvalidAssignmentTarget = 3011,
    InvalidExpressionFollowingConstDeclaration = 3012,
    VariableDeclarationExpected = 3013,
    OperatorNotSupportedOn = 3014,
    InvalidExpressionStatement = 3015,
    NoImplicitConversionExists = 3016,
    UnreachableCodeDetected = 3017,
    DivisionByZero = 3018,
    MissingDoLoop = 3019,
    BelowVmRefreshRate = 3020,
    CannotWaitNegativeDuration = 3021,
    SquareBracketInitialisationNotSupported = 3022,
    ExpressionExpected = 3023,
    DoesNotContainMember = 3024,
    VarargNotLastParameter = 3025,
    ParameterNameReserved = 3026,
    DuplicateFunction = 3027,
    CannotUseAsIndexer = 3028,
    IndexerExpected = 3029,
    NotDefined = 3030,
    NoEnclosingLoop = 3031,
    CannotAssignToReadOnlyProperty = 3032,

    // 8xxx errors are issued by the IDE for conventions

    // 9xxx errors are issued by the IDE for GSCode.NET faults
    UnhandledLexError = 9000,
    UnhandledMacError = 9001,
    UnhandledAstError = 9002,
    UnhandledSpaError = 9003,
    UnhandledIdeError = 9004,
    FailedToReadInsertFile = 9005,

    PreprocessorIfAnalysisUnsupported = 9900,
}

public static class DiagnosticCodes
{
    private static readonly Dictionary<GSCErrorCodes, DiagnosticCode> diagnosticsDictionary = new()
    {
        // 1xxx
        { GSCErrorCodes.ExpectedPreprocessorToken, new("'{0}' expected, but instead got '{1}'.", DiagnosticSeverity.Error) },
        { GSCErrorCodes.UnexpectedCharacter, new("Unexpected character '{0}'.", DiagnosticSeverity.Error) },
        { GSCErrorCodes.ExpectedInsertPath, new("Expected a file path for insert directive.", DiagnosticSeverity.Error) },
        { GSCErrorCodes.ExpectedMacroParameter, new("Expected an identifier corresponding to a macro parameter name, but instead got '{0}'.", DiagnosticSeverity.Error) },
        { GSCErrorCodes.DuplicateMacroParameter, new("A macro parameter named '{0}' already exists on this definition.", DiagnosticSeverity.Error) },
        { GSCErrorCodes.MissingInsertFile, new("Unable to locate file '{0}' for insert directive.", DiagnosticSeverity.Error) },
        { GSCErrorCodes.TooManyMacroArguments, new("Too many arguments in invocation of macro '{0}', expected {1} arguments.", DiagnosticSeverity.Error) },
        { GSCErrorCodes.TooFewMacroArguments, new("Too few arguments in invocation of macro '{0}', expected {1} arguments.", DiagnosticSeverity.Error) },
        { GSCErrorCodes.MisplacedPreprocessorDirective, new("The preprocessor directive '{0}' is not valid in this context.", DiagnosticSeverity.Error) },
        { GSCErrorCodes.MultilineStringLiteral, new("Carriage return embedded in string literal.", DiagnosticSeverity.Error) },
        { GSCErrorCodes.ExpectedMacroIdentifier, new("Expected an identifier corresponding to a macro name, but instead got '{0}'.", DiagnosticSeverity.Error) },
        { GSCErrorCodes.UnterminatedPreprocessorDirective, new("Expected an '#endif' to terminate '{0}' directive.", DiagnosticSeverity.Error) },
        { GSCErrorCodes.InvalidInsertPath, new("The insert path '{0}' is not valid. The path must be relative and point to a file inside the project directory.", DiagnosticSeverity.Error) },
        { GSCErrorCodes.InvalidLineContinuation, new("A line continuation character must immediately precede a line break.", DiagnosticSeverity.Error) },
        { GSCErrorCodes.DuplicateMacroDefinition, new("A macro named '{0}' already exists in this context.", DiagnosticSeverity.Error) },
        { GSCErrorCodes.UserDefinedMacroIgnored, new("Due to script engine limitations, the reference to user-defined macro '{0}' will not be recognised in this preprocessor-if statement.", DiagnosticSeverity.Warning) },
        { GSCErrorCodes.MissingMacroParameterList, new("'{0}' is a recognised macro but will be ignored here because it requires arguments.", DiagnosticSeverity.Warning) },
        { GSCErrorCodes.InactivePreprocessorBranch, new(string.Empty, DiagnosticSeverity.Hint, [DiagnosticTag.Unnecessary]) },

        // 2xxx
        { GSCErrorCodes.ExpectedPathSegment, new("Expected a file or directory path segment, but instead got '{0}'.", DiagnosticSeverity.Error) },
        { GSCErrorCodes.ExpectedSemiColon, new("';' expected to end {0}.", DiagnosticSeverity.Error) },
        { GSCErrorCodes.UnexpectedUsing, new("Misplaced '#using' directive. Using directives must precede all other definitions and directives in the script.", DiagnosticSeverity.Error) },
        { GSCErrorCodes.ExpectedScriptDefn, new("Expected a directive, class or function definition, but instead got '{0}'.", DiagnosticSeverity.Error) },
        { GSCErrorCodes.ExpectedToken, new("'{0}' expected, but instead got '{1}'.", DiagnosticSeverity.Error) },
        { GSCErrorCodes.ExpectedPrecacheType, new("Expected a string corresponding to a precache asset type, but instead got '{0}'.", DiagnosticSeverity.Error) },
        { GSCErrorCodes.ExpectedPrecachePath, new("Expected a string corresponding to a precache asset path, but instead got '{0}'.", DiagnosticSeverity.Error) },
        { GSCErrorCodes.ExpectedAnimTreeName, new("Expected a string corresponding to an animation tree name, but instead got '{0}'.", DiagnosticSeverity.Error) },
        { GSCErrorCodes.ExpectedNamespaceIdentifier, new("Expected a namespace identifier, but instead got '{0}'.", DiagnosticSeverity.Error) },
        { GSCErrorCodes.ExpectedFunctionIdentifier, new("Expected an identifier corresponding to a function name, but instead got '{0}'.", DiagnosticSeverity.Error) },
        { GSCErrorCodes.UnexpectedFunctionModifier, new("Unexpected function modifier '{0}'. When used, modifiers must appear after the function keyword.", DiagnosticSeverity.Error) },
        { GSCErrorCodes.ExpectedParameterIdentifier, new("Expected an identifier corresponding to a parameter name, but instead got '{0}'.", DiagnosticSeverity.Error) },
        { GSCErrorCodes.ExpectedConstantIdentifier, new("Expected an identifier corresponding to a constant name, but instead got '{0}'.", DiagnosticSeverity.Error) },
        { GSCErrorCodes.ExpectedForeachIdentifier, new("Expected an identifier corresponding to a foreach variable name, but instead got '{0}'.", DiagnosticSeverity.Error) },
        { GSCErrorCodes.ExpectedAssignmentOperator, new("Expected an assignment operator, but instead got '{0}'.", DiagnosticSeverity.Error) },
        { GSCErrorCodes.ExpectedClassIdentifier, new("Expected an identifier corresponding to a class name, but instead got '{0}'.", DiagnosticSeverity.Error) },
        { GSCErrorCodes.ExpectedMethodIdentifier, new("Expected an identifier corresponding to a method name, but instead got '{0}'.", DiagnosticSeverity.Error) },
        { GSCErrorCodes.ExpectedFunctionQualification, new("Expected '::' or a function arguments list, but instead got '{0}'.", DiagnosticSeverity.Error) },
        { GSCErrorCodes.ExpectedExpressionTerm, new("Expected an expression term, but instead got '{0}'.", DiagnosticSeverity.Error) },
        { GSCErrorCodes.ExpectedConstructorParenthesis, new("Expected ')' to complete constructor definition, but instead got '{0}'.", DiagnosticSeverity.Error) },
        { GSCErrorCodes.UnexpectedConstructorParameter, new("Expected ')' to complete constructor definition, but instead got '{0}'. If this was intentional, constructor parameters are not supported by GSC.", DiagnosticSeverity.Error) },
        { GSCErrorCodes.ExpectedClassBodyDefinition, new("Expected a member, method or constructor definition, but instead got '{0}'.", DiagnosticSeverity.Error) },
        { GSCErrorCodes.ExpectedMemberIdentifier, new("Expected an identifier corresponding to a member name, but instead got '{0}'.", DiagnosticSeverity.Error) },
        
        // 3xxx
        { GSCErrorCodes.ObjectTokenNotValid, new("The operator '{0}' is not valid on non-object type '{1}'.", DiagnosticSeverity.Error) },
        { GSCErrorCodes.InvalidDereference, new("The dereference of '{0}' is not valid as it is not a variable of type '{1}'.", DiagnosticSeverity.Error) },
        { GSCErrorCodes.IdentifierExpected, new("Expected an identifier.", DiagnosticSeverity.Error) },
        { GSCErrorCodes.DuplicateModifier, new("Duplicate '{0} modifier.", DiagnosticSeverity.Error) },
        { GSCErrorCodes.IntegerTooLarge, new("The integer '{0}' exceeds the maximum integer value supported.", DiagnosticSeverity.Error) },
        { GSCErrorCodes.OperatorNotSupportedOnTypes, new("The operator '{0}' is not supported on types '{1}' and '{2}'.", DiagnosticSeverity.Error) },
        { GSCErrorCodes.CannotAssignToConstant, new("The variable '{0}' cannot be assigned to, it is a constant.", DiagnosticSeverity.Error) },
        { GSCErrorCodes.StoreFunctionAsPointer, new("Function '{0}' cannot be assigned directly to a variable, it must be pointed to.", DiagnosticSeverity.Error) },
        { GSCErrorCodes.IntegerTooSmall, new("The integer '{0}' is less than the minimum integer value supported.", DiagnosticSeverity.Error) },
        { GSCErrorCodes.MissingAccompanyingConditional, new("'else' conditional used without an accompanying 'if' statement.", DiagnosticSeverity.Error) },
        { GSCErrorCodes.RedefinitionOfSymbol, new("The name '{0}' already exists in this context.", DiagnosticSeverity.Error) },
        { GSCErrorCodes.InvalidAssignmentTarget, new("Only variables, fields and array or map indices are valid destinations for an assignment.", DiagnosticSeverity.Error) },
        { GSCErrorCodes.InvalidExpressionFollowingConstDeclaration, new("The expression following a constant declaration must be an assignment.", DiagnosticSeverity.Error) },
        { GSCErrorCodes.VariableDeclarationExpected, new("Expected a variable declaration.", DiagnosticSeverity.Error) },
        { GSCErrorCodes.OperatorNotSupportedOn, new("The operator '{0}' is not supported on type '{1}'.", DiagnosticSeverity.Error) },
        { GSCErrorCodes.InvalidExpressionStatement, new("Only assignment, call, increment, decrement, and new object expressions can be used as a statement.", DiagnosticSeverity.Error) },
        { GSCErrorCodes.NoImplicitConversionExists, new("No implicit conversion exists from type '{0}' to type '{1}'.", DiagnosticSeverity.Error) },
        { GSCErrorCodes.UnreachableCodeDetected, new("Unreachable code detected.", DiagnosticSeverity.Warning, new[] { DiagnosticTag.Unnecessary}) },
        { GSCErrorCodes.DivisionByZero, new("Division by zero detected.", DiagnosticSeverity.Error) },
        { GSCErrorCodes.MissingDoLoop, new("A statementless 'while' loop can only be used with a preceding 'do' branch.", DiagnosticSeverity.Error) },
        { GSCErrorCodes.BelowVmRefreshRate, new("Because the {0} VM runs at {1} Hz, the time '{2}' will be rounded up to '{3}'.", DiagnosticSeverity.Warning) },
        { GSCErrorCodes.CannotWaitNegativeDuration, new("Cannot wait for a zero or negative duration.", DiagnosticSeverity.Error) },
        { GSCErrorCodes.SquareBracketInitialisationNotSupported, new("Square bracket collection initialisation with members is not supported. Use array() instead.", DiagnosticSeverity.Error) },
        { GSCErrorCodes.ExpressionExpected, new("Expected an expression.", DiagnosticSeverity.Error) },
        { GSCErrorCodes.DoesNotContainMember, new("Property '{0}' does not exist on type '{1}'.", DiagnosticSeverity.Error) },
        { GSCErrorCodes.VarargNotLastParameter, new("A vararg '...' declaration must be the final parameter of a parameter list.", DiagnosticSeverity.Error) },
        { GSCErrorCodes.ParameterNameReserved, new("The parameter name '{0}' is reserved.", DiagnosticSeverity.Error) },
        { GSCErrorCodes.DuplicateFunction, new("Duplicate function implementation '{0}'.", DiagnosticSeverity.Error) },
        { GSCErrorCodes.CannotUseAsIndexer, new("Cannot use type '{0}' as an indexer.", DiagnosticSeverity.Error) },
        { GSCErrorCodes.IndexerExpected, new("Expected an indexer.", DiagnosticSeverity.Error) },
        { GSCErrorCodes.NotDefined, new("The name '{0}' does not exist in the current context.", DiagnosticSeverity.Error) },
        { GSCErrorCodes.NoEnclosingLoop, new("No enclosing loop out of which to break or continue.", DiagnosticSeverity.Error) },
        { GSCErrorCodes.CannotAssignToReadOnlyProperty, new("The property '{0}' cannot be assigned to, it is read-only.", DiagnosticSeverity.Error) },

        // 9xxx
        { GSCErrorCodes.UnhandledLexError, new("An unhandled exception '{0}' caused tokenisation (gscode-lex) of the script to fail. File a GSCode issue report and provide this script file for error reproduction.", DiagnosticSeverity.Error) },
        { GSCErrorCodes.UnhandledMacError, new("An unhandled exception '{0}' caused preprocessing (gscode-mac) to fail. File a GSCode issue report and provide this script file for error reproduction.", DiagnosticSeverity.Error) },
        { GSCErrorCodes.UnhandledAstError, new("An unhandled exception '{0}' caused syntax tree generation (gscode-ast) to fail. File a GSCode issue report and provide this script file for error reproduction.", DiagnosticSeverity.Error) },
        { GSCErrorCodes.UnhandledSpaError, new("An unhandled exception '{0}' caused static program analysis (gscode-spa) to fail. File a GSCode issue report and provide this script file for error reproduction.", DiagnosticSeverity.Error) },
        { GSCErrorCodes.UnhandledIdeError, new("An unhandled exception '{0}' caused GSCode IDE analysis (gscode-ide) to fail. File a GSCode issue report and provide this script file for error reproduction.", DiagnosticSeverity.Error) },
        { GSCErrorCodes.FailedToReadInsertFile, new("Failed to read contents of insert-directive file '{0}' due to exception '{1}'. Check the file is accessible, then try again.", DiagnosticSeverity.Error) },

        { GSCErrorCodes.PreprocessorIfAnalysisUnsupported, new("Preprocessor-if analysis is not currently supported. This might lead to incorrect labelling of syntax errors.", DiagnosticSeverity.Information) },
    };

    public static Diagnostic GetDiagnostic(Range range, string source, GSCErrorCodes key, params object?[] arguments)
    {
        if (diagnosticsDictionary.ContainsKey(key))
        {
            DiagnosticCode result = diagnosticsDictionary[key];
            return new()
            {
                Message = string.Format(result.Message, arguments),
                Range = range,
                Severity = result.Category,
                Code = (int)key,
                Source = source,
                Tags = result.Tags
            };
        }

        return new()
        {
            Message = "GSCode.NET Error: could not find an error matching this code.",
            Range = range,
            Severity = DiagnosticSeverity.Error,
            Code = (int)key,
            Source = source
        };
    }
}
