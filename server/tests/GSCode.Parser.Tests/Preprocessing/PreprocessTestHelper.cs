using System.Collections.Immutable;
using GSCode.Core;
using GSCode.Core.Text;
using GSCode.Parser.Lexing;
using GSCode.Parser.Preprocessing;

namespace GSCode.Parser.Tests.Preprocessing;

/// <summary>
/// An in-memory insert provider: register GSH sources by raw path. One instance stands for one
/// file's resolution context, so two of them registering the same raw path against different
/// resolved paths model a mod overlaying a raw header.
/// </summary>
public sealed class FakeInsertProvider : IInsertProvider
{
    private readonly Dictionary<string, InsertedFile> _files = new(StringComparer.OrdinalIgnoreCase);

    public FakeInsertProvider AddInsert(string rawPath, string content, string? resolvedPath = null)
    {
        SourceText text = SourceText.From(content);
        LexResult lexed = Lexer.Lex(text);
        string normalizedPath = (resolvedPath ?? rawPath).ToLowerInvariant();
        _files[rawPath] = new InsertedFile(normalizedPath, text, lexed.Tokens);
        return this;
    }

    /// <summary>Raw paths asked for, in order — a walk fetches its target, a replay does not.</summary>
    public List<string> Fetched { get; } = [];

    public bool TryGetInsert(string rawInsertPath, out InsertedFile inserted)
    {
        Fetched.Add(rawInsertPath);
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

/// <summary>
/// A header contribution cache with the workspace's InsertCache storage semantics and none of its
/// file-system validation: keyed by resolved path, case-insensitive, last store wins.
/// </summary>
public sealed class FakeHeaderMacroCache : IHeaderMacroCache
{
    private readonly Dictionary<string, HeaderContribution> _contributions =
        new(StringComparer.OrdinalIgnoreCase);

    public bool TryGet(string resolvedPath, out HeaderContribution contribution)
    {
        return _contributions.TryGetValue(resolvedPath, out contribution!);
    }

    public void Store(string resolvedPath, HeaderContribution contribution)
    {
        _contributions[resolvedPath] = contribution;
    }

    public bool Contains(string resolvedPath)
    {
        return _contributions.ContainsKey(resolvedPath);
    }
}

internal static class PreprocessTestHelper
{
    public const string RootPath = @"c:\work\scripts\test.gsc";

    /// <summary>The root file's stem — the namespace a file that declares none falls back to.</summary>
    public const string RootStem = "test";

    public static PreprocessResult Run(
        string source, IInsertProvider? insertProvider = null, IHeaderMacroCache? headerCache = null)
    {
        SourceText text = SourceText.From(source);
        LexResult lexed = Lexer.Lex(text);
        return Preprocessor.Process(
            RootPath, lexed.Tokens, text, insertProvider ?? NullInsertProvider.Instance, new NameTable(),
            headerCache: headerCache);
    }

    /// <summary>Kinds of the parse stream, excluding the trailing EndOfFile.</summary>
    public static List<TokenKind> Kinds(PreprocessResult result)
    {
        List<TokenKind> kinds = [];
        foreach ( PToken token in result.Tokens )
        {
            if ( token.Kind != TokenKind.EndOfFile )
            {
                kinds.Add(token.Kind);
            }
        }

        return kinds;
    }

    /// <summary>Texts of the parse stream, excluding the trailing EndOfFile.</summary>
    public static List<string> Texts(PreprocessResult result)
    {
        List<string> texts = [];
        foreach ( PToken token in result.Tokens )
        {
            if ( token.Kind != TokenKind.EndOfFile )
            {
                texts.Add(token.Text);
            }
        }

        return texts;
    }

    public static ImmutableArray<PToken> SignificantTokens(PreprocessResult result)
    {
        return result.Tokens;
    }
}
