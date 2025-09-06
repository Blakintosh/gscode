using System;
using System.Collections.Generic;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;

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
    MissingUsingFile = 3033,

    // Newly added SPA diagnostics
    UnusedVariable = 3034,
    UnusedParameter = 3035,
    TooManyArguments = 3036,
    TooFewArguments = 3037,
    ArgumentTypeMismatch = 3038,
    PossibleUndefinedAccess = 3039,
    UnknownNamespace = 3040,
    DuplicateCaseLabel = 3041,
    MultipleDefaultLabels = 3042,
    FallthroughCase = 3043,
    UnreachableCase = 3044,
    ShadowedSymbol = 3045,
    UnusedUsing = 3046,
    CircularDependency = 3047,
    NoMatchingOverload = 3048,
    CalledOnInvalidTarget = 3049,
    InvalidThreadCall = 3050,
    AssignOnThreadedFunction = 3051,

    // 8xxx errors are issued by the IDE for conventions
    UnterminatedRegion = 8000,

    // 9xxx errors are issued by the IDE for GSCode.NET faults
    UnhandledLexError = 9000,
    UnhandledMacError = 9001,
    UnhandledAstError = 9002,
    UnhandledSaError = 9003,
    UnhandledFraError = 9004,
    UnhandledSpaError = 9005,
    UnhandledIdeError = 9006,
    FailedToReadInsertFile = 9007,

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
        { GSCErrorCodes.InactivePreprocessorBranch, new("This code is not included in compilation as its preprocessor condition is not met.", DiagnosticSeverity.Hint, [DiagnosticTag.Unnecessary]) },

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
        { GSCErrorCodes.MissingUsingFile, new("Unable to locate file '{0}' in the workspace or in the shared scripts directory.", DiagnosticSeverity.Error) },
        // Newly added SPA diagnostics
        { GSCErrorCodes.UnusedVariable, new("The variable '{0}' is declared but never used.", DiagnosticSeverity.Warning, new[] { DiagnosticTag.Unnecessary }) },
        { GSCErrorCodes.UnusedParameter, new("The parameter '{0}' is never used.", DiagnosticSeverity.Hint, new[] { DiagnosticTag.Unnecessary }) },
        { GSCErrorCodes.TooManyArguments, new("Function '{0}' called with {1} arguments, but expects {2}.", DiagnosticSeverity.Error) },
        { GSCErrorCodes.TooFewArguments, new("Function '{0}' called with {1} arguments, but expects at least {2}.", DiagnosticSeverity.Warning) },
        { GSCErrorCodes.ArgumentTypeMismatch, new("Argument {0} to '{1}' expects '{2}', got '{3}'.", DiagnosticSeverity.Error) },
        { GSCErrorCodes.PossibleUndefinedAccess, new("Possible dereference of 'undefined' value.", DiagnosticSeverity.Warning) },
        { GSCErrorCodes.UnknownNamespace, new("The namespace '{0}' does not exist.", DiagnosticSeverity.Error) },
        { GSCErrorCodes.DuplicateCaseLabel, new("Duplicate 'case' label.", DiagnosticSeverity.Error) },
        { GSCErrorCodes.MultipleDefaultLabels, new("Multiple 'default' labels in this switch.", DiagnosticSeverity.Error) },
        { GSCErrorCodes.FallthroughCase, new("Control falls through from 'case' to the next 'case'.", DiagnosticSeverity.Information) },
        { GSCErrorCodes.UnreachableCase, new("'case' is unreachable.", DiagnosticSeverity.Warning, new[] { DiagnosticTag.Unnecessary }) },
        { GSCErrorCodes.ShadowedSymbol, new("Local '{0}' shadows a symbol from an outer scope.", DiagnosticSeverity.Information) },
        { GSCErrorCodes.UnusedUsing, new("The using file '{0}' is not referenced.", DiagnosticSeverity.Hint, new[] { DiagnosticTag.Unnecessary }) },
        { GSCErrorCodes.CircularDependency, new("Circular dependency detected involving '{0}'.", DiagnosticSeverity.Error) },
        { GSCErrorCodes.NoMatchingOverload, new("No overload of '{0}' matches argument types ({1}).", DiagnosticSeverity.Error) },
        { GSCErrorCodes.CalledOnInvalidTarget, new("Called-on target must be an entity/struct; got '{0}'.", DiagnosticSeverity.Error) },
        { GSCErrorCodes.InvalidThreadCall, new("Only function calls can be threaded.", DiagnosticSeverity.Error) },
        { GSCErrorCodes.AssignOnThreadedFunction, new("Assigning a value on a threaded function can be undefined behavior if the function has a wait inside of it", DiagnosticSeverity.Warning) },
  
        // 8xxx
        { GSCErrorCodes.UnterminatedRegion, new("No corresponding '/* endregion */' found to terminate '{0}' region.", DiagnosticSeverity.Warning) },
      
        // 9xxx
        { GSCErrorCodes.UnhandledLexError, new("An unhandled exception '{0}' caused tokenisation (gscode-lex) of the script to fail. File a GSCode issue report and provide this script file for error reproduction.", DiagnosticSeverity.Error) },
        { GSCErrorCodes.UnhandledMacError, new("An unhandled exception '{0}' caused preprocessing (gscode-mac) to fail. File a GSCode issue report and provide this script file for error reproduction.", DiagnosticSeverity.Error) },
        { GSCErrorCodes.UnhandledAstError, new("An unhandled exception '{0}' caused syntax tree generation (gscode-ast) to fail. File a GSCode issue report and provide this script file for error reproduction.", DiagnosticSeverity.Error) },
        { GSCErrorCodes.UnhandledSaError, new("An unhandled exception '{0}' caused signature analysis (gscode-sa) to fail. File a GSCode issue report and provide this script file for error reproduction.", DiagnosticSeverity.Error) },
        { GSCErrorCodes.UnhandledFraError, new("An unhandled exception '{0}' caused folding range analysis (gscode-fra) to fail. File a GSCode issue report and provide this script file for error reproduction.", DiagnosticSeverity.Error) },
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
