using GSCode.Data;
using GSCode.Data.Models;
using GSCode.Data.Models.Interfaces;
using GSCode.Lexer.Types.Interfaces;
using System.Text;

namespace GSCode.Lexer.Types
{
    public enum CommentTypes
    {
        Block,
        Documentation,
        Line
    }

    internal class MultilineCommentTokenFactory : ITokenFactory
    {
        public bool HasMatch(ReadOnlySpan<char> scriptSpan, ref int line, ref int lineBaseIndex, ref int currentIndex, out Token tokenIfMatched)
        {
            if(!CommentStartMatches(scriptSpan, currentIndex, out CommentTypes commentType))
            {
                tokenIfMatched = default!;
                return false;
            }

            int firstLine = line;
            int basePosition = currentIndex - lineBaseIndex;

            StringBuilder contentsBuilder = new();
            // Given that we're only supporting two types here a dictionary's overhead is undesirable
            contentsBuilder.Append(commentType == CommentTypes.Block ? "/*" : "/@");

            int lastPosition = currentIndex;

            for(int i = currentIndex + 2; i < scriptSpan.Length; i++)
            {
                lastPosition = i;
                contentsBuilder.Append(scriptSpan[i]);
                if(scriptSpan[i] == '/')
                {
                    if (scriptSpan[i - 1] == '*' || scriptSpan[i - 1] == '@')
                    {
                        break;
                    }
                }
                CheckAndUpdateLine(scriptSpan, i, ref line, ref lineBaseIndex);
            }

            int endLine = line;

            currentIndex = lastPosition + 1;
            tokenIfMatched = new(RangeHelper.From(firstLine, basePosition, endLine, lastPosition + 1 - lineBaseIndex), 
                TokenType.Comment, commentType, 
                contentsBuilder.ToString());
            return true;
        }

        private void CheckAndUpdateLine(ReadOnlySpan<char> scriptSpan, int currentIndex, ref int line, ref int lineBaseIndex)
        {
            if (scriptSpan[currentIndex] == '\n')
            {
                lineBaseIndex = currentIndex + 1;
                line++;
            }
        }

        private bool CommentStartMatches(ReadOnlySpan<char> scriptSpan, int currentIndex, out CommentTypes commentType)
        {
            if(currentIndex + 1 < scriptSpan.Length &&
                scriptSpan[currentIndex] == '/')
            {
                if(scriptSpan[currentIndex + 1] == '*')
                {
                    commentType = CommentTypes.Block;
                    return true;
                }
                if (scriptSpan[currentIndex + 1] == '@')
                {
                    commentType = CommentTypes.Documentation;
                    return true;
                }
            }

            commentType = default!;
            return false;
        }
    }
}
