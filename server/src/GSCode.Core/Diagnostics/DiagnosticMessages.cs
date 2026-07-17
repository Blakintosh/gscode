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
