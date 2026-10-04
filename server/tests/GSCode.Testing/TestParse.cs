using GSCode.Core;
using GSCode.Core.Text;
using GSCode.Parser;
using GSCode.Parser.Preprocessing;

namespace GSCode.Testing;

/// <summary>
/// One source string parsed on its own: no workspace, so no <c>#insert</c> resolves. The shape
/// behind lint, formatter, typing and extraction tests that ask about one file's own text.
/// </summary>
public static class TestParse
{
    /// <summary>Where a single-file test's source lives when the path is not what it is about.</summary>
    public static string DefaultPath => TestPaths.Raw(@"scripts\t.gsc");

    /// <summary>
    /// Parses <paramref name="source"/> as the file at <paramref name="path"/> (absolute; default
    /// <see cref="DefaultPath"/>), in the language its extension names, under
    /// <paramref name="profile"/> or the active game.
    /// </summary>
    public static ParseResult Analyze(string source, string? path = null, GameProfile? profile = null)
    {
        string filePath = path ?? DefaultPath;
        return ScriptAnalysis.Analyze(
            filePath, ScriptAnalysis.LanguageFromPath(filePath), SourceText.From(source), NullInsertProvider.Instance,
            new NameTable(), profile);
    }
}
