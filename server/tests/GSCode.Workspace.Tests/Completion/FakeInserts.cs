using GSCode.Core.Text;
using GSCode.Parser.Lexing;
using GSCode.Parser.Preprocessing;

namespace GSCode.Workspace.Tests.Completion;

/// <summary>
/// Serves a header's text to <c>#insert</c>, so a macro can come from outside the file under test.
///
/// Shared rather than nested in one fixture: completion and signature help both have to be asked
/// about a macro a header supplies, which is where the macros that matter actually live — a shared
/// .gsh, not the root file.
/// </summary>
internal sealed class FakeInserts : IInsertProvider
{
    private readonly Dictionary<string, InsertedFile> _files = new(StringComparer.OrdinalIgnoreCase);

    public FakeInserts Add(string rawPath, string content)
    {
        SourceText text = SourceText.From(content);
        _files[rawPath] = new InsertedFile(rawPath.ToLowerInvariant(), text, Lexer.Lex(text).Tokens);
        return this;
    }

    public bool TryGetInsert(string rawInsertPath, out InsertedFile inserted)
    {
        return _files.TryGetValue(rawInsertPath, out inserted!);
    }

    public bool TryResolveInsertPath(string rawInsertPath, out string resolvedPath)
    {
        if ( _files.TryGetValue(rawInsertPath, out InsertedFile? file) )
        {
            resolvedPath = file.Path;
            return true;
        }

        resolvedPath = "";
        return false;
    }
}
