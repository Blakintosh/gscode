using GSCode.Parser.Data;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace GSCode.NET.LSP.Utility;

internal class SemanticTokensBuilder
{
    public List<ISemanticToken> SemanticTokens { get; set; } = new();

    public static readonly string[] SemanticTokenTypes = new[]
    {
        "macro",
        "variable",
        "parameter",
        "property"
    };

    public static readonly string[] SemanticTokenModifiers = new[]
    {
        "declaration",
        "definition",
        "readonly",
        "local",
        "defaultLibrary"
    };

    /// <summary>
    /// Encodes all semantic tokens provided to the builder into LSP-compliant format.
    /// </summary>
    /// <returns></returns>
    public int[] Encode()
    {
        SortedList<ulong, ISemanticToken> tokens = new();
        int[] result = new int[SemanticTokens.Count * 5];

        ulong tieBreak = 0;
        foreach(ISemanticToken token in SemanticTokens)
        {
            ulong index = (ulong)token.Range.Start.Line * 1000000000000L + (ulong)token.Range.Start.Character * 1000L + tieBreak++;

            tokens.Add(index, token);

        }

        int lastLine = 0;
        int lastCharacter = 0;
        int resultIndex = 0;

        for(int i = 0; i < tokens.Count; i++)
        {
            int deltaLine = 0;
            int tokenType = 0;
            int tokenModifiers = 0;

            ISemanticToken current = tokens.GetValueAtIndex(i);

            int deltaCharacter;
            if (current.Range.Start.Line != lastLine)
            {
                deltaLine = current.Range.Start.Line - lastLine;
                deltaCharacter = current.Range.Start.Character;
            }
            else
            {
                deltaCharacter = current.Range.Start.Character - lastCharacter;
            }
            int length = current.Range.End.Character - current.Range.Start.Character;

            for (int j = 0; j < SemanticTokenTypes.Length; j++)
            {
                if (SemanticTokenTypes[j] == current.SemanticTokenType)
                {
                    tokenType = j;
                    break;
                }
            }

            for (int j = 0; j < SemanticTokenModifiers.Length; j++)
            {
                foreach(string modifier in current.SemanticTokenModifiers)
                {
                    if (SemanticTokenModifiers[j] == modifier)
                    {
                        tokenModifiers |= 1 << j;
                        break;
                    }
                }
            }

            result[resultIndex] = deltaLine;
            result[resultIndex + 1] = deltaCharacter;
            result[resultIndex + 2] = length;
            result[resultIndex + 3] = tokenType;
            result[resultIndex + 4] = tokenModifiers;

            resultIndex += 5;

            lastLine = current.Range.Start.Line;
            lastCharacter = current.Range.Start.Character;
        }

        return result;
    }
}
