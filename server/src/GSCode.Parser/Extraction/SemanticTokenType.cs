namespace GSCode.Parser.Extraction;

/// <summary>
/// The semantic token classifications GSCode emits. The integer values are the indices
/// into the LSP legend the server registers, so their ORDER is a contract with that legend.
/// </summary>
public enum SemanticTokenType
{
    Namespace = 0,
    Type = 1,
    Function = 2,
    Macro = 3,
    Parameter = 4,
    Variable = 5,
    Property = 6,
    Keyword = 7,
    String = 8,
    Number = 9,
    Comment = 10,
}

/// <summary>One classified span for semantic highlighting (line/char are zero-based).</summary>
public readonly record struct SemanticToken(int Line, int StartChar, int Length, SemanticTokenType Type);
