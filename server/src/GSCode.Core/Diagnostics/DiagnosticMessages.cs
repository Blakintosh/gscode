using System.Collections.Frozen;

namespace GSCode.Core.Diagnostics;

/// <summary>
/// The message template for every diagnostic code, kept in one table so wording
/// stays consistent and codes can never ship without a message.
/// </summary>
public static class DiagnosticMessages
{
    private static readonly FrozenDictionary<GscDiagnosticCode, string> s_templates = new Dictionary<GscDiagnosticCode, string>
    {
        // Lexing
        [GscDiagnosticCode.UnterminatedString] = "String literal is not terminated before the end of the line.",
        [GscDiagnosticCode.UnterminatedBlockComment] = "Block comment is missing its closing '*/'.",
        [GscDiagnosticCode.UnterminatedDocComment] = "Documentation block is missing its closing '@/'.",
        [GscDiagnosticCode.UnexpectedCharacter] = "Unexpected character '{0}'.",
        [GscDiagnosticCode.UnknownDirective] = "Unknown preprocessor directive '{0}'.",

        // Preprocessing
        [GscDiagnosticCode.ExpectedMacroName] = "Expected a macro name after '#define'.",
        [GscDiagnosticCode.UnterminatedMacroParameters] = "The parameter list of macro '{0}' is missing its closing ')'.",
        [GscDiagnosticCode.InvalidLineContinuation] = "A line continuation '\\' must be the last token on its line.",
        [GscDiagnosticCode.MissingInsertPath] = "Expected a file path after '#insert'.",
        [GscDiagnosticCode.InsertMissingSemicolon] = "'#insert' directive must end with ';'.",
        [GscDiagnosticCode.InvalidInsertPath] = "'{0}' is not a valid insert path: paths must be relative and cannot contain '..'.",
        [GscDiagnosticCode.InsertNotFound] = "Cannot find insert file '{0}'.",
        [GscDiagnosticCode.InsertTooDeep] = "'#insert' nesting is too deep at '{0}'.",
        [GscDiagnosticCode.InsertCycle] = "'#insert' cycle detected: '{0}' is already being inserted.",
        [GscDiagnosticCode.UnterminatedConditionalDirective] = "'{0}' directive is missing its closing '#endif'.",
        [GscDiagnosticCode.UnexpectedConditionalDirective] = "'{0}' without a matching '#if'.",
        [GscDiagnosticCode.MissingMacroArguments] = "Macro '{0}' expects an argument list.",
        [GscDiagnosticCode.UnterminatedMacroArguments] = "The argument list for macro '{0}' is missing its closing ')'.",
        [GscDiagnosticCode.InactiveConditionalBranch] = "Inactive preprocessor branch; this code is excluded from the build.",

        // Parsing
        [GscDiagnosticCode.ExpectedToken] = "Expected '{0}' but found '{1}'.",
        [GscDiagnosticCode.ExpectedDeclaration] = "Expected a function, class, or directive but found '{0}'.",
        [GscDiagnosticCode.ExpectedExpression] = "Expected an expression but found '{0}'.",
        [GscDiagnosticCode.ExpectedStatement] = "Expected a statement but found '{0}'.",
        [GscDiagnosticCode.ExpectedParameterName] = "Expected a parameter name but found '{0}'.",
        [GscDiagnosticCode.ExpectedClassMember] = "Expected 'var', 'constructor', 'destructor', or 'function' but found '{0}'.",
        [GscDiagnosticCode.ExpectedCaseLabel] = "Expected 'case' or 'default' but found '{0}'.",
        [GscDiagnosticCode.UnterminatedBlock] = "Block is missing its closing '}}'.",
        [GscDiagnosticCode.UnterminatedDevBlock] = "Dev block is missing its closing '#/'.",
        [GscDiagnosticCode.UsingAfterDeclaration] = "'#using' directives must appear before the first function or class declaration.",
        [GscDiagnosticCode.ExpectedScriptPath] = "Expected a script path after '{0}'.",
        [GscDiagnosticCode.ExpectedNamespaceName] = "Expected a namespace name after '#namespace'.",
        [GscDiagnosticCode.InvalidAssignmentTarget] = "Cannot assign to {0} — assignment needs a variable, field or array element on the left.",

        // Extraction / per-file semantics
        [GscDiagnosticCode.UnknownPrecacheType] = "'{0}' is not a known #precache asset type.",
        [GscDiagnosticCode.ClientOnlyPrecacheType] = "'{0}' is a client-side asset type and can only be precached from a client script.",
        [GscDiagnosticCode.WrongPrecacheArgumentCount] = "#precache type '{0}' expects {1} value(s) after the type but got {2}.",
        [GscDiagnosticCode.ConstructorHasParameters] = "Constructors cannot declare parameters.",
        [GscDiagnosticCode.DestructorHasParameters] = "Destructors cannot declare parameters.",
        [GscDiagnosticCode.NonValueDefaultParameter] = "Default value for '{0}' must be a plain value (literals and vectors only).",
        [GscDiagnosticCode.DuplicateFunction] = "Function '{0}' is already defined in namespace '{1}'.",
        [GscDiagnosticCode.AmbiguousFunction] =
            "'{0}' is declared in {2} of the files this script imports, all in namespace '{1}' — which one this call reaches is undefined.",
        [GscDiagnosticCode.UnusedLocal] = "'{0}' is assigned but never used.",
        [GscDiagnosticCode.UsingNotFound] = "Cannot find script '{0}'.",
        [GscDiagnosticCode.CaseUndefined] =
            "'case undefined' never matches — a switch compares values, so this branch is unreachable.",
        [GscDiagnosticCode.NonConstantCaseLabel] = "A case label must be a constant value.",

        // Cross-file / workspace semantics
        [GscDiagnosticCode.NamespaceNotImported] = "Namespace '{0}' is called but no '#using' imports a file that declares it.",
        [GscDiagnosticCode.UnusedUsing] = "'{0}' is imported but nothing from it is used.",
        [GscDiagnosticCode.UnusedInclude] = "'{0}' is included but nothing from it is used.",
        // Keeps v1's wording for the script case (its FunctionDoesNotExist = 3035), since it says
        // exactly the right thing; the builtin case names the two places that were searched, because
        // "could not be resolved" reads as a tooling failure when the name may simply be an engine
        // function we do not have data for.
        [GscDiagnosticCode.ScriptFunctionNotFound] = "The function '{0}' could not be resolved.",
        [GscDiagnosticCode.BuiltinFunctionNotFound] = "'{0}' matches no script function or known engine function.",
        // {0} is the noun -- "Parameter" or "Field" -- since the same rule covers a builtin's
        // declared-bool argument and an engine field the data types bool.
        [GscDiagnosticCode.PreferBooleanLiteral] = "{0} '{1}' is a bool; prefer '{2}' over the integer literal.",
        [GscDiagnosticCode.PrivateFunctionNotVisible] = "'{0}' is private to namespace '{1}'; only files declaring that namespace can call it.",
        [GscDiagnosticCode.ReadOnlyFieldWrite] = "Engine field '{0}' is read-only; assigning to it has no effect.",
        [GscDiagnosticCode.SizeIsReadOnly] = "'.size' is read-only and cannot be assigned.",
        [GscDiagnosticCode.DevOnlyFunctionCalledFromRelease] = "'{0}' is declared inside a '/# #/' dev block and will not exist in a release build.",
        [GscDiagnosticCode.UnreachableCode] = "Unreachable: the preceding '{0}' always leaves this block.",
    }.ToFrozenDictionary();

    /// <summary>Formats the template for a code with its arguments.</summary>
    public static string Format(GscDiagnosticCode code, params object[] arguments)
    {
        string template = s_templates[code];

        if ( arguments.Length == 0 )
        {
            return template;
        }

        return string.Format(System.Globalization.CultureInfo.InvariantCulture, template, arguments);
    }
}
