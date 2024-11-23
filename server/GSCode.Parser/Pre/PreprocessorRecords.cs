using GSCode.Data;
using GSCode.Parser.Data;
using GSCode.Parser.Lexical;
using GSCode.Parser.Util;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;

namespace GSCode.Parser.Pre;

/// <summary>
/// Records script defines for semantics & usage
/// </summary>
/// <param name="Source">The source name token</param>
/// <param name="DefineTokens">The tokens #define to the last token of the expansion, excluding comments</param>
/// <param name="ExpansionTokens">When used, the key expands to these tokens</param>
/// <param name="Parameters">List of parameters</param>
/// <param name="Documentation">Documentation for the define if it ends in a comment</param>
internal record MacroDefinition(Token Source, TokenList DefineTokens, TokenList ExpansionTokens,
   LinkedList<Token>? Parameters, string? Documentation = null) : ISenseDefinition
{
    public bool IsFromPreprocessor { get; } = Source.IsFromPreprocessor;
    public Range Range { get; } = Source.Range;

    public string SemanticTokenType { get; } = "macro";
    public string[] SemanticTokenModifiers { get; } = [];

    public Hover GetHover()
    {
        return new()
        {
            Range = Range,
            Contents = new MarkedStringsOrMarkupContent(new MarkupContent()
            {
                Kind = MarkupKind.Markdown,
                Value = string.Format("```gsc\n{0}\n```\n{1}",
                    DefineTokens.ToSnippetString(), GetFormattedDocumentation())
            })
        };
    }

    private string GetFormattedDocumentation()
    {
        if (!string.IsNullOrEmpty(Documentation))
        {
            return string.Format("---\n{0}", Documentation);
        }
        return string.Empty;
    }

    public static MacroDefinition BuiltInMacroDefinition(string source, params Token[] expansion)
    {
        Token sourceToken = new(TokenType.Identifier, RangeHelper.Empty, source)
        {
            IsFromPreprocessor = true
        };

        // Create a combined array of all tokens for the define source
        Token[] defineTokens = [
            new Token(TokenType.Define, RangeHelper.Empty, "#define"),
            new Token(TokenType.Whitespace, RangeHelper.Empty, " "),
            sourceToken,
            new Token(TokenType.Whitespace, RangeHelper.Empty, " "),
            .. expansion,
        ];

        TokenList defineSource = TokenList.From(defineTokens);
        TokenList expansionSource = TokenList.From(expansion);

        return new(sourceToken, defineSource, expansionSource, null);
    }
}

/// <summary>
/// Records usages of a macro for semantics & hovers
/// </summary>
/// <param name="Source">The macro token source</param>
/// <param name="DefineSource">The define this macro is from</param>
/// <param name="ExpansionTokens">The expansion of this macro</param>
internal record ScriptMacro(Token Source, MacroDefinition DefineSource, TokenList ExpansionTokens) : ISenseDefinition
{
    public bool IsFromPreprocessor { get; } = Source.IsFromPreprocessor;
    public Range Range { get; } = Source.Range;

    public string SemanticTokenType { get; } = OmniSharp.Extensions.LanguageServer.Protocol.Models.SemanticTokenType.Macro;

    public string[] SemanticTokenModifiers { get; } = Array.Empty<string>();

    public Hover GetHover()
    {
        string defineSnippet = DefineSource.DefineTokens.ToSnippetString();
        string expansionSnippet = ExpansionTokens.ToSnippetString();

        Hover hover = new()
        {
            Contents = new MarkedStringsOrMarkupContent(new MarkupContent()
            {
                Kind = MarkupKind.Markdown,
                Value = string.Format("```gsc\n{0}\n```\n{1}\n\n---\n{2}\n```gsc\n{3}\n```",
                    defineSnippet, GetFormattedDocumentation(), "Expands to:", expansionSnippet)
            }),
            Range = Source.Range,
        };

        return hover;
    }

    private string GetFormattedDocumentation()
    {
        if (!string.IsNullOrEmpty(DefineSource.Documentation))
        {
            return string.Format("---\n{0}", DefineSource.Documentation);
        }
        return string.Empty;
    }
}