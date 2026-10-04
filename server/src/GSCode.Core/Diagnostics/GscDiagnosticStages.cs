namespace GSCode.Core.Diagnostics;

/// <summary>
/// Which pipeline stage a code belongs to, read from its thousands digit — the ranges
/// <see cref="GscDiagnosticCode"/> is laid out in. Named here so a caller asking "is this a parse
/// error" does not spell the range out itself.
/// </summary>
public static class GscDiagnosticStages
{
    /// <summary>A lexer diagnostic (1xxx).</summary>
    public static bool IsLexing(GscDiagnosticCode code)
    {
        return (int)code is >= 1000 and < 2000;
    }

    /// <summary>A parser diagnostic (3xxx).</summary>
    public static bool IsParsing(GscDiagnosticCode code)
    {
        return (int)code is >= 3000 and < 4000;
    }
}
