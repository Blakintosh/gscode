using System.Collections.Immutable;
using GSCode.Core;
using GSCode.Core.Text;
using GSCode.Parser.Lexing;
using GSCode.Parser.Preprocessing;

namespace GSCode.Workspace.Resolution;

/// <summary>
/// The real #insert provider: resolves the raw path through the asking file's
/// resolution context, reads and lexes the target. (A shared lexed-GSH cache lands
/// with the indexer in P5; open-document usage is light enough without it.)
/// </summary>
public sealed class ResolverInsertProvider : IInsertProvider
{
    private readonly PathResolver _resolver;
    private readonly ResolutionContext _context;
    private readonly IFileSystem _fileSystem;

    public ResolverInsertProvider(PathResolver resolver, ResolutionContext context, IFileSystem fileSystem)
    {
        _resolver = resolver;
        _context = context;
        _fileSystem = fileSystem;
    }

    public bool TryGetInsert(string rawInsertPath, out InsertedFile inserted)
    {
        inserted = null!;

        string? resolved = _resolver.Resolve(_context, rawInsertPath);
        if ( resolved is null )
        {
            return false;
        }

        string content;
        try
        {
            content = _fileSystem.ReadAllText(resolved);
        }
        catch ( IOException )
        {
            return false;
        }
        catch ( UnauthorizedAccessException )
        {
            return false;
        }

        SourceText text = SourceText.From(content);
        ImmutableArray<Token> tokens = Lexer.Lex(text).Tokens;
        inserted = new InsertedFile(resolved, text, tokens);
        return true;
    }
}
