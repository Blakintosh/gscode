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
    public const string Ide = "gscode-ide"; // IDE enforced conventions
}

public enum GSCErrorCodes
{
    // 1xxx errors are issued by the preprocessor
    MissingToken = 1000,
    UnexpectedCharacter = 1001,
    TokenNotValidInContext = 1002,
    MissingData = 1003,
    MissingIdentifier = 1004,
    MissingScript = 1005,
    TooManyMacroArguments = 1006,
    TooFewMacroArguments = 1007,
    UnknownStatement = 1008,
    MultilineStringLiteral = 1009,
    InvalidFilePath = 1010,
    UnexpectedEof = 1011,
    CommentLineContinuation = 1012,
    InvalidLineContinuation = 1013,
    InvalidExpressionTerm = 1014,

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
}

public static class DiagnosticCodes
{
    private static readonly Dictionary<GSCErrorCodes, DiagnosticCode> diagnosticsDictionary = new()
    {
        // 1xxx
        { GSCErrorCodes.MissingToken, new("'{0}' expected.", DiagnosticSeverity.Error) },
        { GSCErrorCodes.UnexpectedCharacter, new("Unexpected character '{0}'.", DiagnosticSeverity.Error) },
        { GSCErrorCodes.TokenNotValidInContext, new("The token '{0}' is not valid in this context.", DiagnosticSeverity.Error) },
        { GSCErrorCodes.MissingData, new("Expected data or reference.", DiagnosticSeverity.Error) },
        { GSCErrorCodes.MissingIdentifier, new("Expected an identifier.", DiagnosticSeverity.Error) },
        { GSCErrorCodes.MissingScript, new("Unable to locate script '{0}'.", DiagnosticSeverity.Error) },
        { GSCErrorCodes.TooManyMacroArguments, new("Too many arguments in invocation of macro '{0}', expected {1} arguments.", DiagnosticSeverity.Error) },
        { GSCErrorCodes.TooFewMacroArguments, new("Too few arguments in invocation of macro '{0}', expected {1} arguments.", DiagnosticSeverity.Error) },
        { GSCErrorCodes.UnknownStatement, new("Unknown token sequence.", DiagnosticSeverity.Error) }, // an ideal parser will never need to use this code
        { GSCErrorCodes.MultilineStringLiteral, new("Carriage return embedded in string literal.", DiagnosticSeverity.Error) },
        { GSCErrorCodes.InvalidFilePath, new("Not a valid script file path.", DiagnosticSeverity.Error) },
        { GSCErrorCodes.UnexpectedEof, new("Unexpected end of file reached.", DiagnosticSeverity.Error) },
        { GSCErrorCodes.CommentLineContinuation, new("Line continuation use following a comment is not supported.", DiagnosticSeverity.Error) },
        { GSCErrorCodes.InvalidLineContinuation, new("A line continuation character must immediately precede a line break.", DiagnosticSeverity.Error) },
        { GSCErrorCodes.InvalidExpressionTerm, new("Expression is not valid.", DiagnosticSeverity.Error) }, // TODO: want this error to be unused as soon as possible

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
