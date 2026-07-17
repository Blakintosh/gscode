using GSCode.Core.Text;

namespace GSCode.Core.Diagnostics;

/// <summary>A single reported problem: where, how severe, which condition, and the formatted message.</summary>
public sealed record Diagnostic(TextRange Range, DiagnosticSeverity Severity, GscDiagnosticCode Code, string Message)
{
    /// <summary>Creates a diagnostic, formatting the code's message template with the given arguments.</summary>
    public static Diagnostic Create(TextRange range, DiagnosticSeverity severity, GscDiagnosticCode code, params object[] arguments)
    {
        return new Diagnostic(range, severity, code, DiagnosticMessages.Format(code, arguments));
    }
}
