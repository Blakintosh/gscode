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
        [GscDiagnosticCode.UnterminatedString] = "String literal is not terminated before the end of the line.",
        [GscDiagnosticCode.UnterminatedBlockComment] = "Block comment is missing its closing '*/'.",
        [GscDiagnosticCode.UnterminatedDocComment] = "Documentation block is missing its closing '@/'.",
        [GscDiagnosticCode.UnexpectedCharacter] = "Unexpected character '{0}'.",
        [GscDiagnosticCode.UnknownDirective] = "Unknown preprocessor directive '{0}'.",
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
