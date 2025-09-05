using GSCode.Data;
using GSCode.Parser.Data;
using GSCode.Parser.Lexical;
using GSCode.Parser.Steps.Interfaces;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace GSCode.Parser.Misc;

internal ref partial struct FoldingRangeAnalyser(Token startToken, ParserIntelliSense sense)
{
    private Token CurrentToken { get; set; } = startToken;
    public readonly TokenType CurrentTokenType => CurrentToken.Type;

    private ParserIntelliSense Sense { get; } = sense;

    public List<FoldingRange> Analyse()
    {
        List<FoldingRange> foldingRanges = new();
        
        while(CurrentTokenType != TokenType.Eof)
        {
            if(!IsRegionStart(CurrentToken, out string? regionName))
            {
                CurrentToken = CurrentToken.Next;
                continue;
            }

            FoldingRange? foldingRange = AnalyseFoldingRange(CurrentToken, regionName ?? string.Empty, foldingRanges);
            if(foldingRange is not null)
            {
                foldingRanges.Add(foldingRange);
            }
        }
        
        return foldingRanges;
    }

    private FoldingRange? AnalyseFoldingRange(Token startToken, string name, List<FoldingRange> foldingRanges)
    {
        CurrentToken = CurrentToken.Next;

        while(CurrentTokenType != TokenType.Eof)
        {
            if(IsRegionEnd(CurrentToken))
            {
                Token endToken = CurrentToken;
                CurrentToken = CurrentToken.Next;

                return new FoldingRange
                {
                    StartLine = startToken.Range.Start.Line,
                    StartCharacter = startToken.Range.End.Character,

                    EndLine = endToken.Range.End.Line,
                    EndCharacter = endToken.Range.Start.Character,

                    Kind = FoldingRangeKind.Region
                };
            }

            if(!IsRegionStart(CurrentToken, out string? nestedRegionName))
            {
                CurrentToken = CurrentToken.Next;
                continue;
            }

            FoldingRange? foldingRange = AnalyseFoldingRange(CurrentToken, nestedRegionName ?? string.Empty, foldingRanges);
            if(foldingRange is not null)
            {
                foldingRanges.Add(foldingRange);
            }
        }

        Sense.AddIdeDiagnostic(RangeHelper.From(startToken.Range.Start, startToken.Range.End), GSCErrorCodes.UnterminatedRegion, name);
        return null;
    }

    [GeneratedRegex(@"^\s*/\*\s*region\s+(\w+)\s*\*/\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex RegionStartRegex();

    [GeneratedRegex(@"^\s*/\*\s*endregion\s*\*/\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex RegionEndRegex();

    private static bool IsRegionStart(Token token, out string? name)
    {
        name = null;
        
        if(token.Type != TokenType.MultilineComment)
        {
            return false;
        }

        Match match = RegionStartRegex().Match(token.Lexeme);
        if(match.Success)
        {
            name = match.Groups[1].Value;
            return true;
        }

        return false;
    }

    private static bool IsRegionEnd(Token token)
    {
        if(token.Type != TokenType.MultilineComment)
        {
            return false;
        }

        return RegionEndRegex().IsMatch(token.Lexeme);
    }
}
