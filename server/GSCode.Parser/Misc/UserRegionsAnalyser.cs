using GSCode.Data;
using GSCode.Parser.Data;
using GSCode.Parser.Lexical;
using GSCode.Parser.Steps.Interfaces;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using Serilog;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;

namespace GSCode.Parser.Misc;

internal ref partial struct UserRegionsAnalyser(Token startToken, ParserIntelliSense sense)
{
    private Token CurrentToken { get; set; } = startToken;
    public readonly TokenType CurrentTokenType => CurrentToken.Type;

    private ParserIntelliSense Sense { get; } = sense;

    public void Analyse()
    {
        while (CurrentTokenType != TokenType.Eof)
        {
            if (!IsRegionStart(CurrentToken, out string? regionName, out Match? regionMatch))
            {
                CurrentToken = CurrentToken.Next;
                continue;
            }

            EmitRegionStartSemanticTokens(CurrentToken, regionName, regionMatch);

            FoldingRange? foldingRange = AnalyseFoldingRange(CurrentToken, regionName ?? string.Empty);
            if (foldingRange is not null)
            {
                Sense.FoldingRanges.Add(foldingRange);
            }
        }
    }

    private FoldingRange? AnalyseFoldingRange(Token startToken, string name)
    {
        CurrentToken = CurrentToken.Next;

        while (CurrentTokenType != TokenType.Eof)
        {
            if (IsRegionEnd(CurrentToken, out Match? regionEndMatch))
            {
                EmitRegionEndSemanticTokens(CurrentToken, regionEndMatch);

                Token endToken = CurrentToken;
                CurrentToken = CurrentToken.Next;

                return new FoldingRange
                {
                    StartLine = startToken.Range.Start.Line,
                    StartCharacter = startToken.Range.End.Character,

                    EndLine = endToken.Range.End.Line,
                    EndCharacter = endToken.Range.Start.Character,

                    CollapsedText = name,
                    Kind = FoldingRangeKind.Region
                };
            }

            if (!IsRegionStart(CurrentToken, out string? nestedRegionName, out Match? nestedRegionMatch))
            {
                CurrentToken = CurrentToken.Next;
                continue;
            }

            EmitRegionStartSemanticTokens(CurrentToken, nestedRegionName, nestedRegionMatch);

            FoldingRange? foldingRange = AnalyseFoldingRange(CurrentToken, nestedRegionName ?? string.Empty);
            if (foldingRange is not null)
            {
                Sense.FoldingRanges.Add(foldingRange);
            }
        }

        Sense.AddIdeDiagnostic(RangeHelper.From(startToken.Range.Start, startToken.Range.End), GSCErrorCodes.UnterminatedRegion, name);
        return null;
    }

    private readonly void EmitRegionStartSemanticTokens(Token token, string lexeme, Match match)
    {
        int baseChar = token.Range.Start.Character;
        int line = token.Range.Start.Line;

        // Emit the 'region' keyword
        int regionStartCharOffset = match.Groups[1].Index;
        int regionEndCharOffset = regionStartCharOffset + match.Groups[1].Length;

        Sense.SemanticTokens.Add(new SemanticTokenDefinition(new Range(line, regionStartCharOffset + baseChar, line, regionEndCharOffset + baseChar), "keyword", []));

        // and the region's name
        int nameStartCharOffset = match.Groups[2].Index;
        int nameEndCharOffset = nameStartCharOffset + match.Groups[2].Length;

        Sense.SemanticTokens.Add(new SemanticTokenDefinition(new Range(line, nameStartCharOffset + baseChar, line, nameEndCharOffset + baseChar), "variable", []));
    }

    private readonly void EmitRegionEndSemanticTokens(Token token, Match match)
    {
        int baseChar = token.Range.Start.Character;
        int line = token.Range.Start.Line;

        int endregionStartCharOffset = match.Groups[1].Index;
        int endregionEndCharOffset = endregionStartCharOffset + match.Groups[1].Length;

        Sense.SemanticTokens.Add(new SemanticTokenDefinition(new Range(line, endregionStartCharOffset + baseChar, line, endregionEndCharOffset + baseChar), "keyword", []));
    }

    [GeneratedRegex(@"^\s*/\*\s*(region)\s+([^*]+?)\s*\*/\s*$", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex RegionStartRegex();

    [GeneratedRegex(@"^\s*/\*\s*(endregion)\s*\*/\s*$", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex RegionEndRegex();

    private static bool IsRegionStart(Token token, [NotNullWhen(true)] out string? name, [NotNullWhen(true)] out Match? match)
    {
        name = null;
        match = null;

        if (token.Type != TokenType.MultilineComment)
        {
            return false;
        }

        match = RegionStartRegex().Match(token.Lexeme);
        if (match.Success)
        {
            name = match.Groups[1].Value.Trim();
            return true;
        }

        return false;
    }

    private static bool IsRegionEnd(Token token, [NotNullWhen(true)] out Match? match)
    {
        match = null;
        if (token.Type != TokenType.MultilineComment)
        {
            return false;
        }

        match = RegionEndRegex().Match(token.Lexeme);
        if (match.Success)
        {
            return true;
        }

        return false;
    }
}
