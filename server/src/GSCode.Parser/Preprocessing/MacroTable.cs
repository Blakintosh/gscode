using System.Collections.Immutable;
using GSCode.Core.Text;

namespace GSCode.Parser.Preprocessing;

/// <summary>
/// One #define: its name, where it lives, its optional parameter list, its body as
/// provenance-stamped tokens, and any trailing same-line comment as documentation.
/// </summary>
/// <param name="Name">The macro name, EXACT case — macro names are the case-sensitive exception.</param>
/// <param name="SourceFile">File containing the definition; null = the root file being processed.</param>
/// <param name="NameRange">Range of the name token (the go-to-definition target).</param>
/// <param name="Parameters">Parameter names in order; null for object-like macros.</param>
/// <param name="Body">Replacement tokens (line continuations already stripped).</param>
/// <param name="Documentation">Trailing comment on the define line, if any.</param>
public sealed record MacroDefinition(
    string Name,
    string? SourceFile,
    TextRange NameRange,
    ImmutableArray<string>? Parameters,
    ImmutableArray<PToken> Body,
    string? Documentation)
{
    /// <summary>True for #define NAME(args) macros.</summary>
    public bool IsFunctionLike
    {
        get { return Parameters is not null; }
    }
}

/// <summary>
/// All macros visible during preprocessing. Keyed CASE-SENSITIVELY (ordinal): the
/// language reference states macro names are case sensitive, unlike everything else.
/// Redefinition silently replaces, matching engine behavior.
/// </summary>
public sealed class MacroTable
{
    private readonly Dictionary<string, MacroDefinition> _macros = new(StringComparer.Ordinal);

    public int Count
    {
        get { return _macros.Count; }
    }

    public IEnumerable<MacroDefinition> All
    {
        get { return _macros.Values; }
    }

    public bool TryGet(string name, out MacroDefinition definition)
    {
        return _macros.TryGetValue(name, out definition!);
    }

    public void Define(MacroDefinition definition)
    {
        _macros[definition.Name] = definition;
    }
}
