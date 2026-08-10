using System.Collections.Immutable;
using GSCode.Core.Text;

namespace GSCode.Core.Diagnostics;

/// <summary>A single reported problem: where, how severe, which condition, and the formatted message.</summary>
public sealed record Diagnostic(TextRange Range, DiagnosticSeverity Severity, GscDiagnosticCode Code, string Message)
{
    /// <summary>
    /// Editor presentation hints. Empty for an ordinary diagnostic; set via a `with` expression
    /// when a range should grey out or strike through.
    /// </summary>
    public ImmutableArray<DiagnosticTag> Tags { get; init; } = ImmutableArray<DiagnosticTag>.Empty;

    /// <summary>Other locations that explain this diagnostic. Empty unless a rule supplies them.</summary>
    public ImmutableArray<DiagnosticRelation> RelatedInformation { get; init; } = ImmutableArray<DiagnosticRelation>.Empty;

    /// <summary>Creates a diagnostic, formatting the code's message template with the given arguments.</summary>
    public static Diagnostic Create(TextRange range, DiagnosticSeverity severity, GscDiagnosticCode code, params object[] arguments)
    {
        return new Diagnostic(range, severity, code, DiagnosticMessages.Format(code, arguments));
    }
}
