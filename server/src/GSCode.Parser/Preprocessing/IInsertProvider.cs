using System.Collections.Immutable;
using GSCode.Core.Text;
using GSCode.Parser.Lexing;

namespace GSCode.Parser.Preprocessing;

/// <summary>A resolved, already-lexed insert target ready for splicing.</summary>
/// <param name="Path">Normalized absolute path of the inserted file.</param>
/// <param name="Text">The file's source snapshot (token offsets refer into this).</param>
/// <param name="Tokens">The file's raw lexed tokens (trivia included).</param>
public sealed record InsertedFile(string Path, SourceText Text, ImmutableArray<Token> Tokens);

/// <summary>
/// Supplies #insert targets to the preprocessor. The Workspace layer implements this
/// over PathResolver + a lexed-GSH cache; tests use in-memory fakes. Keeping it an
/// interface keeps GSCode.Parser free of I/O.
/// </summary>
public interface IInsertProvider
{
    /// <summary>
    /// Resolves and lexes the raw path written after #insert. Returns false when the
    /// file cannot be found (the preprocessor reports the diagnostic).
    /// </summary>
    bool TryGetInsert(string rawInsertPath, out InsertedFile inserted);
}

/// <summary>An insert provider for contexts with no resolver (isolated parses, some tests).</summary>
public sealed class NullInsertProvider : IInsertProvider
{
    public static NullInsertProvider Instance { get; } = new();

    public bool TryGetInsert(string rawInsertPath, out InsertedFile inserted)
    {
        inserted = null!;
        return false;
    }
}
