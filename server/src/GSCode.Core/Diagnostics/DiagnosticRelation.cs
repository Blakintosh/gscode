using GSCode.Core.Text;

namespace GSCode.Core.Diagnostics;

/// <summary>
/// Another location that helps explain a diagnostic, such as the first of two competing
/// definitions. Paths stay plain strings here; the server maps them to URIs.
/// </summary>
public sealed record DiagnosticRelation(string FilePath, TextRange Range, string Message);
