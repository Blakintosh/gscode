using System.Collections.Immutable;
using GSCode.Core;
using GSCode.Core.Text;
using GSCode.Parser.Lexing;
using GSCode.Parser.Preprocessing;

namespace GSCode.Workspace.Resolution;

/// <summary>
/// The real #insert provider: resolves the path as written through the asking file's resolution
/// context, then reads and lexes the target - or takes it from <see cref="InsertCache"/>, which is
/// shared across every file, since a provider is built per file and a header is inserted by many.
/// </summary>
public sealed class ResolverInsertProvider : IInsertProvider
{
    private readonly PathResolver _resolver;
    private readonly ResolutionContext _context;
    private readonly IFileSystem _fileSystem;
    private readonly InsertCache? _cache;

    public ResolverInsertProvider(
        PathResolver resolver, ResolutionContext context, IFileSystem fileSystem, InsertCache? cache = null)
    {
        _resolver = resolver;
        _context = context;
        _fileSystem = fileSystem;
        _cache = cache;
    }

    public bool TryGetInsert(string rawInsertPath, out InsertedFile inserted)
    {
        inserted = null!;

        string? resolved = _resolver.Resolve(_context, rawInsertPath);
        if ( resolved is null )
        {
            return false;
        }

        // The cache is keyed by the RESOLVED path, so a header in a mod and the raw header it
        // shadows are different entries even though both were asked for by the same written path.
        InsertedFile? file = _cache is not null
            ? _cache.GetOrAdd(resolved, _fileSystem, () => Read(resolved))
            : Read(resolved);

        if ( file is null )
        {
            return false;
        }

        inserted = file;
        return true;
    }

    public bool TryResolveInsertPath(string rawInsertPath, out string resolvedPath)
    {
        string? resolved = _resolver.Resolve(_context, rawInsertPath);
        resolvedPath = resolved ?? "";
        return resolved is not null;
    }

    /// <summary>Reads and lexes one header, or null when it cannot be read.</summary>
    private InsertedFile? Read(string resolved)
    {
        string content;
        try
        {
            content = _fileSystem.ReadAllText(resolved);
        }
        catch ( IOException )
        {
            return null;
        }
        catch ( UnauthorizedAccessException )
        {
            return null;
        }

        SourceText text = SourceText.From(content);
        ImmutableArray<Token> tokens = Lexer.Lex(text).Tokens;
        return new InsertedFile(resolved, text, tokens);
    }
}
