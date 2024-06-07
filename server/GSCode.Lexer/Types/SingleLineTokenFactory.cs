using GSCode.Data;
using GSCode.Data.Models;
using GSCode.Data.Models.Interfaces;
using GSCode.Lexer.Types.Interfaces;
using Serilog;
using System.Text;
using System.Text.RegularExpressions;

namespace GSCode.Lexer.Types
{
    internal sealed class SingleLineTokenFactory : ITokenFactory
    {
        private static List<ISingleLineFactory> SingleLineFactories { get; } = new()
        {
            new WhitespaceFactory(),
            new LineCommentFactory(),
            new PunctuationFactory(),
            new NumberFactory(),
            new KeywordFactory(),
            new OperatorFactory(),
            new SpecialTokenFactory(),
            new ScriptStringFactory(),
            new NameFactory()
        };

        public bool HasMatch(ReadOnlySpan<char> scriptSpan, ref int line, ref int lineBaseIndex, ref int currentIndex, out Token tokenIfMatched)
        {
            int upperBound = GetLineUpperBound(scriptSpan, currentIndex);

            ReadOnlySpan<char> slicedLine = scriptSpan[lineBaseIndex..upperBound];

            // Give the singleline matchers an offset from the beginning of the line
            int lineCharIndex = currentIndex - lineBaseIndex;

            foreach(ISingleLineFactory factory in SingleLineFactories)
            {
                if(factory.HasMatch(slicedLine, line, lineCharIndex, out Token token))
                {
                    tokenIfMatched = token;
                    currentIndex += token.TextRange.End.Character - token.TextRange.Start.Character;
                    return true;
                }
            }

            tokenIfMatched = default!;
            return false;
        }

        /// <summary>
        /// Gets the upper bound of the line in the file, for slicing the document span into a line.
        /// </summary>
        /// <param name="documentSpan">The full script text as a span</param>
        /// <param name="lowerBound">The beginning index of this line</param>
        /// <returns>A position if newline found or the end of the file reached.</returns>
        private static int GetLineUpperBound(ReadOnlySpan<char> documentSpan, int lowerBound = 0)
        {
            for (int i = lowerBound; i < documentSpan.Length; i++)
            {
                if (documentSpan[i] == '\n')
                {
                    return i;
                }
            }

            return documentSpan.Length;
        }
    }

    #region Keyword
    /// <summary>
    /// Enum for all keyword types supported by GSC
    /// </summary>
    public enum KeywordTypes
    {
        Classes,
        Function,
        Var,
        Return,
        Thread,
        Undefined,
        Class,
        Anim,
        If,
        Else,
        Do,
        While,
        Foreach,
        For,
        In,
        New,
        WaittillFrameEnd,
        WaitRealTime,
        Wait,
        Switch,
        Case,
        Default,
        Break,
        Continue,
        False,
        True,
        AssertMsg,
        Assert,
        Constructor,
        Destructor,
        Autoexec,
        Private,
        Const,
        VectorScale,
        GetTime,
        ProfileStart,
        ProfileStop,
        UsingAnimTree,
        AnimTree,
        Using,
        Insert,
        Define,
        Namespace,
        Precache,
        Vararg
    }

    /// <summary>
    /// Keyword tokens factory: e.g. function, in, etc.
    /// </summary>
    internal sealed partial class KeywordFactory : ISingleLineFactory
    {
        private readonly static Dictionary<string, Enum> keywordDictionary = new()
        {
            { "classes", KeywordTypes.Classes },
            { "function", KeywordTypes.Function },
            { "var", KeywordTypes.Var },
            { "return", KeywordTypes.Return },
            { "thread", KeywordTypes.Thread },
            { "undefined", KeywordTypes.Undefined },
            { "class", KeywordTypes.Class },
            { "anim", KeywordTypes.Anim },
            { "if", KeywordTypes.If },
            { "else", KeywordTypes.Else },
            { "do", KeywordTypes.Do },
            { "while", KeywordTypes.While },
            { "foreach", KeywordTypes.Foreach },
            { "for", KeywordTypes.For },
            { "in", KeywordTypes.In},
            { "new", KeywordTypes.New },
            { "waittillframeend", KeywordTypes.WaittillFrameEnd },
            { "waitrealtime", KeywordTypes.WaitRealTime },
            { "wait", KeywordTypes.Wait },
            { "switch", KeywordTypes.Switch },
            { "case", KeywordTypes.Case },
            { "default", KeywordTypes.Default },
            { "break", KeywordTypes.Break },
            { "continue", KeywordTypes.Continue },
            { "false", KeywordTypes.False },
            { "true", KeywordTypes.True },
            { "constructor", KeywordTypes.Constructor },
            { "destructor", KeywordTypes.Destructor },
            { "autoexec", KeywordTypes.Autoexec },
            { "private", KeywordTypes.Private },
            { "const", KeywordTypes.Const },
            { "#using_animtree", KeywordTypes.UsingAnimTree },
            { "#animtree", KeywordTypes.AnimTree },
            { "#using", KeywordTypes.Using },
            { "#insert", KeywordTypes.Insert },
            { "#define", KeywordTypes.Define },
            { "#namespace", KeywordTypes.Namespace },
            { "#precache", KeywordTypes.Precache },
            // Probably not where this should belong
            { "...", KeywordTypes.Vararg }
        };

        // TODO Monitor: need to make sure this isn't too slow
        private static readonly Regex keywordRegex = KeywordRegex();


        public bool HasMatch(ReadOnlySpan<char> lineSpan, int line, int currentIndex, out Token matchedToken)
        {
            var matches = keywordRegex.EnumerateMatches(lineSpan[currentIndex..]);
            foreach (ValueMatch match in matches)
            {
                if (match.Index != 0)
                {
                    break;
                }

                string text = lineSpan.Slice(currentIndex, match.Length).ToString();

                matchedToken = new(RangeHelper.From(line, currentIndex, line, currentIndex + match.Length), TokenType.Keyword, keywordDictionary[text], text);
                return true;
            }
            matchedToken = default!;
            return false;
        }

        [GeneratedRegex("\\b(waittillframeend|constructor|destructor|waitrealtime|undefined|function|continue|default|break|thread|return|classes|var|class|anim|if|else|do|while|foreach|for|in|new|wait|switch|case|false|true|autoexec|private|const)\\b|(#using_animtree|#animtree|#namespace|#precache|#define|#insert|#using|\\.\\.\\.)", RegexOptions.Compiled | RegexOptions.Singleline)]
        private static partial Regex KeywordRegex();
    }
    #endregion
    #region Line Comments
    // Line comments share the token type and CommentTypes enum with multiline comments
    internal sealed class LineCommentFactory : ISingleLineFactory
    {
        public bool HasMatch(ReadOnlySpan<char> lineSpan, int line, int currentIndex, out Token matchedToken)
        {
            if (currentIndex + 1 >= lineSpan.Length ||
                lineSpan[currentIndex] != '/' ||
                lineSpan[currentIndex + 1] != '/')
            {
                matchedToken = default!;
                return false;
            }

            string contents = lineSpan[currentIndex..].ToString();
            matchedToken = new(RangeHelper.From(line, currentIndex, line, currentIndex + contents.Length),
                TokenType.Comment, CommentTypes.Line,
                contents);
            return true;
        }
    }
    #endregion
    #region Names
    /// <summary>
    /// Name tokens factory: foo, _bar, test86
    /// </summary>
    internal sealed class NameFactory : ISingleLineFactory
    {
        public bool HasMatch(ReadOnlySpan<char> lineSpan, int line, int currentIndex, out Token matchedToken)
        {
            char startChar = lineSpan[currentIndex];
            if (!char.IsLetter(startChar) && startChar != '_')
            {
                matchedToken = default!;
                return false;
            }

            StringBuilder builder = new();
            builder.Append(startChar);

            for (int i = currentIndex + 1; i < lineSpan.Length; i++)
            {
                char c = lineSpan[i];

                if (char.IsLetter(c) || char.IsNumber(c) || c == '_')
                {
                    builder.Append(c);
                    continue;
                }
                break;
            }

            string contents = builder.ToString();
            matchedToken = new Token(RangeHelper.From(line, currentIndex, line, currentIndex + contents.Length),
                TokenType.Name, null,
                contents);
            return true;
        }
    }
    #endregion
    #region Numbers
    public enum NumberTypes
    {
        Int,
        Float,
        Hexadecimal
    }

    internal sealed class NumberFactory : ISingleLineFactory
    {
        public bool HasMatch(ReadOnlySpan<char> lineSpan, int line, int currentIndex, out Token matchedToken)
        {
            char startChar = lineSpan[currentIndex];

            // Hexadecimal numbers
            if (IsHexadecimal(lineSpan, currentIndex))
            {
                bool result = SeekHexadecimalNumber(lineSpan, out StringBuilder builder, currentIndex + 2);

                if (result)
                {
                    string builtNumber = builder.ToString();

                    matchedToken = new(RangeHelper.From(line, currentIndex, line, currentIndex + builtNumber.Length),
                        TokenType.Number, NumberTypes.Hexadecimal,
                        builtNumber);
                    return true;
                }
            }
            // Numbers
            else if(char.IsDigit(startChar) || startChar == '.')
            {
                StringBuilder builder = new();
                bool result = SeekNumber(lineSpan, builder, currentIndex, out bool isFloat);

                if (result)
                {
                    string builtNumber = builder.ToString();
                    Enum? subType = isFloat ? NumberTypes.Float : NumberTypes.Int;

                    matchedToken = new(RangeHelper.From(line, currentIndex, line, currentIndex + builtNumber.Length),
                        TokenType.Number, subType,
                        builtNumber);
                    return true;
                }
            }

            matchedToken = default!;
            return false;
        }

        private bool SeekNumber(ReadOnlySpan<char> lineSpan, StringBuilder builder, int baseIndex, out bool isFloat)
        {
            isFloat = false;
            if (!char.IsDigit(lineSpan[baseIndex]) && lineSpan[baseIndex] != '.')
            {
                return false;
            }

            for (int i = baseIndex; i < lineSpan.Length; i++)
            {
                char current = lineSpan[i];

                if(!char.IsDigit(current) && i == baseIndex + 1 && isFloat)
                {
                    return false;
                }

                if (char.IsDigit(current))
                {
                    builder.Append(current);
                    continue;
                }

                if(current == '.' && !isFloat)
                {
                    builder.Append(current);
                    isFloat = true;
                    continue;
                }

                if(!char.IsLetter(current))
                {
                    break;
                }
                return false;
            }
            return true;
        }

        private bool IsHexadecimal(ReadOnlySpan<char> lineSpan, int currentIndex)
        {
            if(currentIndex + 1 >= lineSpan.Length)
            {
                return false;
            }

            return lineSpan[currentIndex] == '0' && lineSpan[currentIndex + 1] == 'x';
        }

        private bool SeekHexadecimalNumber(ReadOnlySpan<char> lineSpan, out StringBuilder builder, int baseIndex)
        {
            builder = new("0x");

            for (int i = baseIndex; i < lineSpan.Length; i++)
            {
                char current = lineSpan[i];
                if (char.IsLetterOrDigit(current))
                {
                    builder.Append(current);
                    continue;
                }
                break;
            }
            return true;
        }
    }
    #endregion
    #region Operators
    public enum OperatorTypes
    {
        AssignmentBitwiseLeftShift,
        AssignmentBitwiseRightShift,
        NotTypeEquals,
        TypeEquals,
        ScopeResolution,
        MemberAccess,
        MethodAccess,
        And,
        AssignmentBitwiseAnd,
        AssignmentBitwiseOr,
        AssignmentBitwiseXor,
        AssignmentDivide,
        AssignmentMinus,
        AssignmentRemainder,
        AssignmentMultiply,
        AssignmentPlus,
        BitLeftShift,
        BitRightShift,
        Decrement,
        Equals,
        GreaterThanEquals,
        Increment,
        LessThanEquals,
        NotEquals,
        Or,
        Assignment,
        Ampersand,
        BitwiseNot,
        BitwiseOr,
        BitwiseComplement,
        Divide,
        GreaterThan,
        LessThan,
        Minus,
        Remainder,
        Multiply,
        Not,
        Plus,
        Xor,
        TernaryStart,
        Colon,
        Comma
    }

    internal sealed partial class OperatorFactory : ISingleLineFactory
    {
        private readonly static Dictionary<string, Enum> operatorDictionary = new()
        {
            { "<<=", OperatorTypes.AssignmentBitwiseLeftShift },
            { ">>=", OperatorTypes.AssignmentBitwiseRightShift },
            { "!==", OperatorTypes.NotTypeEquals },
            { "===", OperatorTypes.TypeEquals },
            { "::", OperatorTypes.ScopeResolution },
            { "&&", OperatorTypes.And },
            { "&=", OperatorTypes.AssignmentBitwiseAnd },
            { "|=", OperatorTypes.AssignmentBitwiseOr },
            { "^=", OperatorTypes.AssignmentBitwiseXor },
            { "/=", OperatorTypes.AssignmentDivide },
            { "-=", OperatorTypes.AssignmentMinus },
            { "%=", OperatorTypes.AssignmentRemainder },
            { "*=", OperatorTypes.AssignmentMultiply },
            { "+=", OperatorTypes.AssignmentPlus },
            { "<<", OperatorTypes.BitLeftShift },
            { ">>", OperatorTypes.BitRightShift },
            { "--", OperatorTypes.Decrement },
            { "==", OperatorTypes.Equals },
            { ">=", OperatorTypes.GreaterThanEquals },
            { "++", OperatorTypes.Increment },
            { "<=", OperatorTypes.LessThanEquals },
            { "!=", OperatorTypes.NotEquals },
            { "||", OperatorTypes.Or },
            { "->", OperatorTypes.MethodAccess },
            { "=", OperatorTypes.Assignment },
            { "&", OperatorTypes.Ampersand },
            { "~", OperatorTypes.BitwiseNot },
            { "|", OperatorTypes.BitwiseOr },
            { "/", OperatorTypes.Divide },
            { ">", OperatorTypes.GreaterThan },
            { "<", OperatorTypes.LessThan },
            { "-", OperatorTypes.Minus },
            { "%", OperatorTypes.Remainder },
            { "*", OperatorTypes.Multiply },
            { "!", OperatorTypes.Not },
            { "+", OperatorTypes.Plus },
            { "^", OperatorTypes.Xor },
            { "?", OperatorTypes.TernaryStart },
            { ":", OperatorTypes.Colon },
            { ",", OperatorTypes.Comma },
            { ".", OperatorTypes.MemberAccess }
        };

        // TODO Monitor: need to make sure this isn't too slow
        private static readonly Regex operatorRegex = OperatorRegex();

        public bool HasMatch(ReadOnlySpan<char> lineSpan, int line, int currentIndex, out Token matchedToken)
        {
            // Constrained matches to length 3 to minimise computation time
            var matches = operatorRegex.EnumerateMatches(lineSpan.Slice(currentIndex, Math.Min(lineSpan.Length - currentIndex, 3)));
            foreach(ValueMatch match in matches)
            {
                if(match.Index != 0)
                {
                    break;
                }

                string text = lineSpan.Slice(currentIndex, match.Length).ToString();

                matchedToken = new(RangeHelper.From(line, currentIndex, line, currentIndex + match.Length), TokenType.Operator, operatorDictionary[text], text);
                return true;
            }
            matchedToken = default!;
            return false;
        }

        [GeneratedRegex("===|!==|>>=|<<=|::|&&|&=|\\|=|\\^=|/=|-=|%=|\\*=|\\+=|<<|>>|--|==|>=|\\+\\+|<=|!=|\\|\\||->|=|&|~|\\||/|>|<|-|%|\\*|!|\\+|\\^|\\?|:|,|\\.", RegexOptions.Compiled | RegexOptions.Singleline)]
        private static partial Regex OperatorRegex();
    }
    #endregion
    #region Punctuation
    /// <summary>
    /// Enum for all punctuation types in GSC
    /// </summary>
    public enum PunctuationTypes
    {
        OpenBrace,
        CloseBrace,
        OpenBracket,
        CloseBracket,
        OpenParen,
        CloseParen,
        OpenDevBlock,
        CloseDevBlock
    }

    /// <summary>
    /// Punctuation tokens factory: () [] {} /# #/
    /// </summary>
    internal sealed class PunctuationFactory : ISingleLineFactory
    {
        public bool HasMatch(ReadOnlySpan<char> lineSpan, int line, int currentIndex, out Token matchedToken)
        {
            int matchLength = 1;
            Enum? type = lineSpan[currentIndex] switch
            {
                '(' => PunctuationTypes.OpenParen,
                ')' => PunctuationTypes.CloseParen,
                '[' => PunctuationTypes.OpenBracket,
                ']' => PunctuationTypes.CloseBracket,
                '{' => PunctuationTypes.OpenBrace,
                '}' => PunctuationTypes.CloseBrace,
                _ => null,
            };

            if(type is null && currentIndex + 1 < lineSpan.Length)
            {
                if (lineSpan[currentIndex] == '/' && lineSpan[currentIndex + 1] == '#')
                {
                    matchLength = 2;
                    type = PunctuationTypes.OpenDevBlock;
                }
                else if (lineSpan[currentIndex] == '#' && lineSpan[currentIndex + 1] == '/')
                {
                    matchLength = 2;
                    type = PunctuationTypes.CloseDevBlock;
                }
            }

            if (type is not null)
            {
                matchedToken = new Token(RangeHelper.From(line, currentIndex, line, currentIndex + matchLength),
                    TokenType.Punctuation, type, lineSpan[currentIndex].ToString());
                return true;
            }

            matchedToken = default!;
            return false;
        }
    }
    #endregion
    #region Script Strings
    // This isn't a great solution but we'll come back to it later if it's a problem
    public enum StringTypes
    {
        SingleQuote,
        DoubleQuote,
        SingleCompileHash,
        DoubleCompileHash,
        SinglePrecached,
        DoublePrecached,
        Unterminated // For errors
    }

    internal sealed class ScriptStringFactory : ISingleLineFactory
    {
        // refactor at a different date
        public bool HasMatch(ReadOnlySpan<char> lineSpan, int line, int currentIndex, out Token matchedToken)
        {
            // Seek precached or compile time hashed
            // Not well coded
            bool precached = false;
            bool hashed = false;
            bool hasModifier = false;

            char firstChar = lineSpan[currentIndex];
            if(firstChar == '#')
            {
                hashed = true;
                hasModifier = true;
            }
            else if(firstChar == '&')
            {
                precached = true;
                hasModifier = true;
            }

            int baseIndex = currentIndex;
            StringBuilder builder;
            if(hasModifier)
            {
                baseIndex++;
                builder = new StringBuilder(firstChar);
            }
            else
            {
                builder = new();
            }

            if(!SeekScriptString(lineSpan, builder, baseIndex, out bool isTerminated, out bool doubleQuoted))
            {
                matchedToken = default!;
                return false;
            }

            // Awful
            Enum? subType;
            if(!isTerminated)
            {
                subType = StringTypes.Unterminated;
            }
            else if(precached)
            {
                subType = doubleQuoted ? StringTypes.DoublePrecached : StringTypes.SinglePrecached;
            }
            else if (hashed)
            {
                subType = doubleQuoted ? StringTypes.DoubleCompileHash : StringTypes.SingleCompileHash;
            }
            else
            {
                subType = doubleQuoted ? StringTypes.DoubleQuote : StringTypes.SingleQuote;
            }

            string result = builder.ToString();
            matchedToken = new(RangeHelper.From(line, currentIndex, line, currentIndex + result.Length),
                TokenType.ScriptString, subType,
                result);
            return true;
        }

        private bool SeekScriptString(ReadOnlySpan<char> lineSpan, StringBuilder builder, int baseIndex, out bool isTerminated, out bool doubleQuoted)
        {
            if(!GetStartQuote(lineSpan, baseIndex, out char quoteChar))
            {
                isTerminated = false;
                doubleQuoted = false;
                return false;
            }
            doubleQuoted = quoteChar == '"';

            builder.Append(quoteChar);

            for (int i = baseIndex + 1; i < lineSpan.Length; i++)
            {
                char c = lineSpan[i];
                // Add characters on premise that we will return true to avoid O(n^2) operation
                builder.Append(c);

                if(AtEndQuote(lineSpan, i, quoteChar))
                {
                    isTerminated = true;
                    return true;
                }
            }

            isTerminated = false;
            return true;
        }

        private bool GetStartQuote(ReadOnlySpan<char> lineSpan, int index, out char quoteChar)
        {
            if(index < lineSpan.Length && (lineSpan[index] == '\'' || lineSpan[index] == '"'))
            {
                quoteChar = lineSpan[index];
                return true;
            }
            quoteChar = default!;
            return false;
        }

        private bool AtEndQuote(ReadOnlySpan<char> lineSpan, int index, char quoteChar)
        {
            return lineSpan[index] == quoteChar && lineSpan[index - 1] != '\\';
        }
    }
    #endregion
    #region Special Tokens
    /// <summary>
    /// Enum for all special token types in GSC
    /// </summary>
    public enum SpecialTokenTypes
    {
        SemiColon,
        Backslash
    }

    /// <summary>
    /// Special tokens factory: ; : and \
    /// </summary>
    internal sealed class SpecialTokenFactory : ISingleLineFactory
    {
        public bool HasMatch(ReadOnlySpan<char> lineSpan, int line, int currentIndex, out Token matchedToken)
        {
            Enum? type = lineSpan[currentIndex] switch
            {
                ';' => SpecialTokenTypes.SemiColon,
                '\\' => SpecialTokenTypes.Backslash,
                _ => null,
            };

            if (type is not null)
            {
                matchedToken = new Token(RangeHelper.From(line, currentIndex, line, currentIndex + 1),
                    TokenType.SpecialToken, type, lineSpan[currentIndex].ToString());
                return true;
            }

            matchedToken = default!;
            return false;
        }
    }
    #endregion
    #region Whitespace
    /// <summary>
    /// Whitespace token factory: spaces, \t etc.
    /// </summary>
    internal sealed class WhitespaceFactory : ISingleLineFactory
    {
        public bool HasMatch(ReadOnlySpan<char> lineSpan, int line, int currentIndex, out Token matchedToken)
        {
            char startChar = lineSpan[currentIndex];
            if (!char.IsWhiteSpace(startChar) || IsCarriageReturn(startChar))
            {
                matchedToken = default!;
                return false;
            }

            int i;
            for (i = currentIndex + 1; i < lineSpan.Length; i++)
            {
                char c = lineSpan[i];
                if (!char.IsWhiteSpace(c) || IsCarriageReturn(c))
                {
                    break;
                }
            }

            // With whitespace we have no interest in preserving it in its raw form, just set it to be one space.
            matchedToken = new(RangeHelper.From(line, currentIndex, line, i),
                TokenType.Whitespace, null,
                " ");
            return true;
        }

        private static bool IsCarriageReturn(char target)
        {
            return target == '\r';
        }
    }
    #endregion
}
