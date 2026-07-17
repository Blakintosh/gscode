using System.Collections.Immutable;
using GSCode.Core;
using GSCode.Core.Text;
using GSCode.Parser.Lexing;
using GSCode.Parser.Preprocessing;

namespace GSCode.Parser.Tests.Preprocessing;

/// <summary>An in-memory insert provider: register GSH sources by raw path.</summary>
public sealed class FakeInsertProvider : IInsertProvider
{
    private readonly Dictionary<string, InsertedFile> _files = new(StringComparer.OrdinalIgnoreCase);

    public FakeInsertProvider AddInsert(string rawPath, string content)
    {
        SourceText text = SourceText.From(content);
        LexResult lexed = Lexer.Lex(text);
        string normalizedPath = rawPath.ToLowerInvariant();
        _files[rawPath] = new InsertedFile(normalizedPath, text, lexed.Tokens);
        return this;
    }

    public bool TryGetInsert(string rawInsertPath, out InsertedFile inserted)
    {
        return _files.TryGetValue(rawInsertPath, out inserted!);
    }
}

internal static class PreprocessTestHelper
{
    public const string RootPath = @"c:\work\scripts\test.gsc";

    public static PreprocessResult Run(string source, IInsertProvider? insertProvider = null)
    {
        SourceText text = SourceText.From(source);
        LexResult lexed = Lexer.Lex(text);
        return Preprocessor.Process(RootPath, lexed.Tokens, text, insertProvider ?? NullInsertProvider.Instance, new NameTable());
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
