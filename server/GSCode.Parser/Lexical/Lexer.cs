﻿using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;
using static System.Text.RegularExpressions.Regex;

namespace GSCode.Parser.Lexical;

internal ref partial struct Lexer(ReadOnlySpan<char> input)
{
    private ReadOnlySpan<char> _input = input;
    private int _line = 0;
    private int _linePosition = 0;

    public TokenList Transform()
    {
        Token first = new Token(TokenType.Sof, Helper.RangeFrom(0, 0, 0, 0), string.Empty);

        Token current = first;
        while (!_input.IsEmpty)
        {
            if(AdvanceAtLineBreak(out Token? lineBreakToken))
            {
                current.Next = lineBreakToken;
                lineBreakToken.Previous = current;

                current = lineBreakToken;
                continue;
            }

            Token next = NextToken();

            // Shorten the input text by the length of the token
            _input = _input[next.Length..];
            // Goto the new locations given by the token
            _line = next.Range.End.Line;
            _linePosition = next.Range.End.Character;

            // Link the tokens
            current.Next = next;
            next.Previous = current;

            // Move to the next token
            current = next;
        }

        // Done - add EOF
        Token eof = new Token(TokenType.Eof, Helper.RangeFrom(_line, _linePosition, _line, _linePosition), string.Empty);

        current.Next = eof;
        eof.Previous = current;
        eof.Next = eof;

        return new TokenList(first, eof);
    }

    [GeneratedRegex(@"^/\*.*?\*/", RegexOptions.Multiline)]
    private static partial Regex MultilineCommentRegex();

    [GeneratedRegex(@"^/\@.*?\@/", RegexOptions.Multiline)]
    private static partial Regex DocCommentRegex();

    [GeneratedRegex(@"^//.*$", RegexOptions.Multiline)]
    private static partial Regex SinglelineCommentRegex();
    //[GeneratedRegex(@"\s+", RegexOptions.Singleline)]
    //private static partial Regex DoubleQuoteStringRegex();

    private Token NextToken()
    {
        char current = _input[0];
        Token? result = current switch
        {
            // Division & comments & devblock
            '/' => MatchBySlash(),
            // Accessor and vararg dots
            '.' => MatchByDot(),
            // Hash token, devblock and preprocessor directives
            '#' => MatchByHash(),
            // Punctuation
            '(' => CharMatch(TokenType.OpenParen, "("),
            ')' => CharMatch(TokenType.CloseParen, ")"),
            '[' => CharMatch(TokenType.OpenBracket, "["),
            ']' => CharMatch(TokenType.CloseBracket, "]"),
            '{' => CharMatch(TokenType.OpenBrace, "{"),
            '}' => CharMatch(TokenType.CloseBrace, "}"),
            // Special tokens
            ';' => CharMatch(TokenType.Semicolon, ";"),
            ',' => CharMatch(TokenType.Comma, ","),
            '\\' => CharMatch(TokenType.Backslash, "\\"),
            // Operators
            '!' => MatchByExclamation(),
            '=' => MatchByEquals(),
            ':' => MatchByColon(),
            '-' => MatchByMinus(),
            '&' => MatchByAmp(),
            '<' => MatchByLeft(),
            '>' => MatchByRight(),
            '|' => MatchByBar(),
            '^' => MatchByCaret(),
            '%' => MatchByPercent(),
            '*' => MatchByMultiply(),
            '+' => MatchByAdd(),
            '~' => CharMatch(TokenType.BitNot, "~"),
            '?' => CharMatch(TokenType.QuestionMark, "?"),
            // Keywords
            // c or C
            'c' => MatchByC(),
            'C' => MatchByC(),
            // f or F
            'f' => MatchByF(),
            'F' => MatchByF(),
            // r or R
            'r' when StartsWithKeyword("return") => DoCharMatchIfWordBoundary(TokenType.Return, "return"),
            'R' when StartsWithKeyword("return") => DoCharMatchIfWordBoundary(TokenType.Return, "return"),
            // v or V
            'v' when StartsWithKeyword("var") => DoCharMatchIfWordBoundary(TokenType.Var, "var"),
            'V' when StartsWithKeyword("var") => DoCharMatchIfWordBoundary(TokenType.Var, "var"),
            // a or A
            'a' => MatchByA(),
            'A' => MatchByA(),
            // i or I
            'i' => MatchByI(),
            'I' => MatchByI(),
            // e or E
            'e' when StartsWithKeyword("else") => DoCharMatchIfWordBoundary(TokenType.Else, "else"),
            'E' when StartsWithKeyword("else") => DoCharMatchIfWordBoundary(TokenType.Else, "else"),
            // d or D
            'd' => MatchByD(),
            'D' => MatchByD(),
            // w or W
            'w' when StartsWithKeyword("while") => DoCharMatchIfWordBoundary(TokenType.While, "while"),
            'W' when StartsWithKeyword("while") => DoCharMatchIfWordBoundary(TokenType.While, "while"),
            // s or S
            's' when StartsWithKeyword("switch") => DoCharMatchIfWordBoundary(TokenType.Switch, "switch"),
            'S' when StartsWithKeyword("switch") => DoCharMatchIfWordBoundary(TokenType.Switch, "switch"),
            // b or B
            'b' when StartsWithKeyword("break") => DoCharMatchIfWordBoundary(TokenType.Break, "break"),
            'B' when StartsWithKeyword("break") => DoCharMatchIfWordBoundary(TokenType.Break, "break"),
            // p or P
            'p' when StartsWithKeyword("private") => DoCharMatchIfWordBoundary(TokenType.Private, "private"),
            'P' when StartsWithKeyword("private") => DoCharMatchIfWordBoundary(TokenType.Private, "private"),
            // Strings
            '"' => MatchString(TokenType.String),
            // No match
            _ => default
        };

        if(result is null)
        {
            // Numbers
            if (char.IsDigit(current))
            {
                result = MatchNumber();
            }
            // Identifiers
            else if(char.IsLetter(current) || current == '_')
            {
                result = MatchIdentifier();
            }
            else if(char.IsWhiteSpace(current) && current != '\n' && current != '\r')
            {
                result = MatchWhitespace();
            }
        }

        // Match unknown if nothing else matched
        return result ?? CharMatch(TokenType.Unknown, current.ToString());
    }

    /// <summary>
    /// Increments the line index and resets position if the input at the base index matches a line break.
    /// </summary>
    /// <returns>True if the line was advanced</returns>
    private bool AdvanceAtLineBreak([NotNullWhen(true)] out Token? lineBreakToken)
    {
        if(AtNewLine(0))
        {
            // Advance two positions if it's carriage return + newline, otherwise 1
            int offset = _input[0] == '\r' ? 2 : 1;

            // Like whitespace, we probably don't need to preserve the exact line break, so show it as <EOL>
            lineBreakToken = new Token(TokenType.LineBreak, Helper.RangeFrom(_line, _linePosition, _line + 1, 0), "<EOL>");

            _line++;
            _linePosition = 0;

            _input = _input[offset..];

            return true;
        }
        lineBreakToken = null;
        return false;
    }

    private Token MatchBySlash()
    {
        if (_input.Length > 1)
        {
            // /=
            if (_input[1] == '=')
            {
                return CharMatch(TokenType.DivideAssign, "/=");
            }
            // /#
            else if (_input[1] == '#')
            {
                return CharMatch(TokenType.OpenDevBlock, "/#");
            }
            // /* */
            else if (_input[1] == '*' && 
                SeekMatch(MultilineCommentRegex(), out int mlLength))
            {
                return MultilineRegexMatch(TokenType.MultilineComment, mlLength);
            }
            // /@ @/
            else if (_input[1] == '@' &&
                SeekMatch(DocCommentRegex(), out int dcLength))
            {
                return MultilineRegexMatch(TokenType.DocComment, dcLength);
            }
            // //
            else if (_input[1] == '/' &&
                SeekMatch(SinglelineCommentRegex(), out int slLength))
            {
                return SinglelineRegexMatch(TokenType.LineComment, slLength);
            }
        }

        return CharMatch(TokenType.Divide, "/");
    }

    private Token MatchByDot()
    {
        char second = InputAt(1);
        if(second == '.' && InputAt(2) == '.')
        {
            return CharMatch(TokenType.VarargDots, "...");
        }
        // .2, .23423 etc.
        else if(char.IsDigit(second))
        {
            int totalLength = 1 + GetLengthOfNumberSequence(1);
            return new Token(TokenType.Float, Helper.RangeFrom(_line, _linePosition, _line, _linePosition + totalLength), _input[..totalLength].ToString());
        }
        return CharMatch(TokenType.Dot, ".");
    }

    private Token MatchByHash()
    {
        char second = InputAt(1);

        // #/
        if (second == '/')
        {
            return CharMatch(TokenType.CloseDevBlock, "#/");
        }
        else if(second == '"')
        {
            return MatchString(TokenType.CompilerHash, 2);
        }

        // Seek a preprocessor directive - not pretty, but fast
        if(_input.StartsWith("#using_animtree"))
        {
            return CharMatch(TokenType.UsingAnimTree, "#using_animtree");
        }
        else if(_input.StartsWith("#animtree"))
        {
            return CharMatch(TokenType.AnimTree, "#animtree");
        }
        else if(_input.StartsWith("#using"))
        {
            return CharMatch(TokenType.Using, "#using");
        }
        else if(_input.StartsWith("#insert"))
        {
            return CharMatch(TokenType.Insert, "#insert");
        }
        else if(_input.StartsWith("#define"))
        {
            return CharMatch(TokenType.Define, "#define");
        }
        else if(_input.StartsWith("#namespace"))
        {
            return CharMatch(TokenType.Namespace, "#namespace");
        }
        else if(_input.StartsWith("#precache"))
        {
            return CharMatch(TokenType.Precache, "#precache");
        }
        else if(_input.StartsWith("#if"))
        {
            return CharMatch(TokenType.PreIf, "#if");
        }
        else if(_input.StartsWith("#elif"))
        {
            return CharMatch(TokenType.PreElIf, "#elif");
        }
        else if(_input.StartsWith("#else"))
        {
            return CharMatch(TokenType.PreElse, "#else");
        }
        else if(_input.StartsWith("#endif"))
        {
            return CharMatch(TokenType.PreEndIf, "#endif");
        }

        // TODO: Why do we have this, does GSC have a use case for #?
        return CharMatch(TokenType.Hash, "#");
    }

    private Token MatchByExclamation()
    {
        return InputAt(1) switch
        {
            '=' => InputAt(2) switch
            {
                '=' => CharMatch(TokenType.IdentityNotEquals, "!=="),
                _ => CharMatch(TokenType.NotEquals, "!=")
            },
            _ => CharMatch(TokenType.Not, "!")
        };
    }

    private Token MatchByEquals()
    {
        return InputAt(1) switch
        {
            '=' => InputAt(2) switch
            {
                '=' => CharMatch(TokenType.IdentityEquals, "==="),
                _ => CharMatch(TokenType.Equals, "==")
            },
            _ => CharMatch(TokenType.Assign, "=")
        };
    }

    private Token MatchByColon()
    {
        return InputAt(1) switch
        {
            ':' => CharMatch(TokenType.ScopeResolution, "::"),
            _ => CharMatch(TokenType.Colon, ":")
        };
    }

    private Token MatchByMinus()
    {
        return InputAt(1) switch
        {
            '-' => CharMatch(TokenType.Decrement, "--"),
            '>' => CharMatch(TokenType.Arrow, "->"),
            '=' => CharMatch(TokenType.MinusAssign, "-="),
            _ => CharMatch(TokenType.Minus, "-")
        };
    }

    private Token MatchByAmp()
    {
        return InputAt(1) switch
        {
            '&' => CharMatch(TokenType.And, "&&"),
            '=' => CharMatch(TokenType.BitAndAssign, "&="),
            '"' => MatchString(TokenType.IString, 2),
            _ => CharMatch(TokenType.BitAnd, "&")
        };
    }

    private Token MatchByLeft()
    {
        return InputAt(1) switch
        {
            '<' => InputAt(2) switch
            {
                '=' => CharMatch(TokenType.BitLeftShiftAssign, "<<="),
                _ => CharMatch(TokenType.BitLeftShift, "<<")
            },
            '=' => CharMatch(TokenType.LessThanEquals, "<="),
            _ => CharMatch(TokenType.LessThan, "<")
        };
    }

    private Token MatchByRight()
    {
        return InputAt(1) switch
        {
            '>' => InputAt(2) switch
            {
                '=' => CharMatch(TokenType.BitRightShiftAssign, ">>="),
                _ => CharMatch(TokenType.BitRightShift, ">>")
            },
            '=' => CharMatch(TokenType.GreaterThanEquals, ">="),
            _ => CharMatch(TokenType.GreaterThan, ">")
        };
    }

    private Token MatchByBar()
    {
        return InputAt(1) switch
        {
            '|' => CharMatch(TokenType.Or, "||"),
            '=' => CharMatch(TokenType.BitOrAssign, "|="),
            _ => CharMatch(TokenType.BitOr, "|"),
        };
    }

    private Token MatchByCaret()
    {
        return InputAt(1) switch
        {
            '=' => CharMatch(TokenType.BitXorAssign, "^="),
            _ => CharMatch(TokenType.BitXor, "^")
        };
    }

    private Token MatchByPercent()
    {
        char second = InputAt(1);

        // Anim identifier
        if(IsWordChar(second))
        {
            int length = 2;

            while (IsWordChar(InputAt(length)))
            {
                length++;
            }

            return new Token(TokenType.AnimIdentifier, Helper.RangeFrom(_line, _linePosition, _line, _linePosition + length), _input[..length].ToString());
        }

        // Otherwise an operator
        return second switch
        {
            '=' => CharMatch(TokenType.ModuloAssign, "%="),
            _ => CharMatch(TokenType.Modulo, "%")
        };
    }

    private Token MatchByMultiply()
    {
        return InputAt(1) switch
        {
            '=' => CharMatch(TokenType.MultiplyAssign, "*="),
            _ => CharMatch(TokenType.Multiply, "*")
        };
    }

    private Token MatchByAdd()
    {
        return InputAt(1) switch
        {
            '+' => CharMatch(TokenType.Increment, "++"),
            '=' => CharMatch(TokenType.PlusAssign, "+="),
            _ => CharMatch(TokenType.Plus, "+")
        };
    }

    private Token? MatchByC()
    {
        if(StartsWithKeyword("classes"))
        {
            return DoCharMatchIfWordBoundary(TokenType.Classes, "classes");
        }
        else if(StartsWithKeyword("class"))
        {
            return DoCharMatchIfWordBoundary(TokenType.Class, "class");
        }
        else if(StartsWithKeyword("case"))
        {
            return DoCharMatchIfWordBoundary(TokenType.Case, "case");
        }
        else if(StartsWithKeyword("continue"))
        {
            return DoCharMatchIfWordBoundary(TokenType.Continue, "continue");
        }
        else if(StartsWithKeyword("constructor"))
        {
            return DoCharMatchIfWordBoundary(TokenType.Constructor, "constructor");
        }
        else if(StartsWithKeyword("const"))
        {
            return DoCharMatchIfWordBoundary(TokenType.Const, "const");
        }
        return default;
    }

    private Token? MatchByF()
    {
        if (StartsWithKeyword("function"))
        {
            return DoCharMatchIfWordBoundary(TokenType.Function, "function");
        }
        else if (StartsWithKeyword("foreach"))
        {
            return DoCharMatchIfWordBoundary(TokenType.Foreach, "foreach");
        }
        else if (StartsWithKeyword("for"))
        {
            return DoCharMatchIfWordBoundary(TokenType.For, "for");
        }
        else if (StartsWithKeyword("false"))
        {
            return DoCharMatchIfWordBoundary(TokenType.False, "false");
        }
        return default;
    }

    private Token? MatchByA()
    {
        if (StartsWithKeyword("anim"))
        {
            return DoCharMatchIfWordBoundary(TokenType.Anim, "anim");
        }
        else if (StartsWithKeyword("autoexec"))
        {
            return DoCharMatchIfWordBoundary(TokenType.Autoexec, "autoexec");
        }
        return default;
    }

    private Token? MatchByI()
    {
        if (StartsWithKeyword("if"))
        {
            return DoCharMatchIfWordBoundary(TokenType.If, "if");
        }
        else if (StartsWithKeyword("in"))
        {
            return DoCharMatchIfWordBoundary(TokenType.In, "in");
        }
        return default;
    }

    private Token? MatchByD()
    {
        if (StartsWithKeyword("do"))
        {
            return DoCharMatchIfWordBoundary(TokenType.Do, "do");
        }
        else if (StartsWithKeyword("default"))
        {
            return DoCharMatchIfWordBoundary(TokenType.Default, "default");
        }
        else if (StartsWithKeyword("destructor"))
        {
            return DoCharMatchIfWordBoundary(TokenType.Destructor, "destructor");
        }
        return default;
    }

    private Token? MatchByW()
    {
        if (StartsWithKeyword("while"))
        {
            return DoCharMatchIfWordBoundary(TokenType.While, "while");
        }
        else if(StartsWithKeyword("wait"))
        {
            return DoCharMatchIfWordBoundary(TokenType.Wait, "wait");
        }
        else if(StartsWithKeyword("waittillframeend"))
        {
            return DoCharMatchIfWordBoundary(TokenType.WaittillFrameEnd, "waittillframeend");
        }
        else if(StartsWithKeyword("waitrealtime"))
        {
            return DoCharMatchIfWordBoundary(TokenType.WaitRealTime, "waitrealtime");
        }
        return default;
    }

    private Token MatchString(TokenType stringType, int offset = 1)
    {
        bool inEscape = false;
        

        while(offset < _input.Length && !AtNewLine(offset))
        {
            if(!inEscape && InputAt(offset) == '"')
            {
                return new Token(stringType, Helper.RangeFrom(_line, _linePosition, _line, _linePosition + offset + 1), _input[..(offset + 1)].ToString());
            }
            inEscape = !inEscape && InputAt(offset) == '\\';
            offset++;
        }

        return new Token(TokenType.ErrorString, Helper.RangeFrom(_line, _linePosition, _line, _linePosition + offset), _input[..offset].ToString());
    }

    private Token MatchNumber()
    {
        // 0x12AB
        if(InputAt(0) == '0' && InputAt(1) == 'x')
        {
            int hexaLength = 2 + GetLengthOfHexNumberSequence(2);
            return new Token(TokenType.Hex, Helper.RangeFrom(_line, _linePosition, _line, _linePosition + hexaLength), _input[..hexaLength].ToString());
        }

        int deciLength = GetLengthOfNumberSequence(0);

        // 20.5
        if(InputAt(deciLength) == '.')
        {
            int fracLength = GetLengthOfNumberSequence(deciLength + 1);

            int totalLength = deciLength + 1 + fracLength;
            return new Token(TokenType.Float, Helper.RangeFrom(_line, _linePosition, _line, _linePosition + totalLength), _input[..(totalLength)].ToString());
        }

        // 20
        return new Token(TokenType.Integer, Helper.RangeFrom(_line, _linePosition, _line, _linePosition + deciLength), _input[..deciLength].ToString());
    }

    private Token MatchIdentifier()
    {
        // _foo, bar, etc.
        int length = 1;
        while (char.IsLetterOrDigit(InputAt(length)) || InputAt(length) == '_')
        {
            length++;
        }
        return new Token(TokenType.Identifier, Helper.RangeFrom(_line, _linePosition, _line, _linePosition + length), _input[..length].ToString());
    }

    private Token MatchWhitespace()
    {
        // TODO; whitespace is probably skippable, but newlines are relevant to the preprocessor
        int length = 1;

        char current = InputAt(length);
        while (char.IsWhiteSpace(current) && current != '\n' && current != '\r')
        {
            current = InputAt(++length);
        }

        // Don't worry about preserving the exact whitespace, it's easier for us to represent it in IntelliSense by just one space
        return new Token(TokenType.Whitespace, Helper.RangeFrom(_line, _linePosition, _line, _linePosition + length), " ");
    }

    private int GetLengthOfNumberSequence(int startOffset)
    {
        int i = startOffset;
        while(char.IsDigit(InputAt(i)))
        {
            i++;
        }
        return i - startOffset;
    }
    private int GetLengthOfHexNumberSequence(int startOffset)
    {
        int i = startOffset;
        while (char.IsAsciiHexDigit(InputAt(i)))
        {
            i++;
        }
        return i - startOffset;
    }

    private Token MultilineRegexMatch(TokenType tokenType, int length)
    {
        ReadOnlySpan<char> contents = _input[..length];
        int lines = contents.Count('\n');
        int endOffset = contents.Length - contents.LastIndexOf('\n');

        return new Token(tokenType, Helper.RangeFrom(_line, _linePosition, _line + lines, _linePosition + endOffset), contents.ToString());
    }

    private Token SinglelineRegexMatch(TokenType tokenType, int length)
    {
        ReadOnlySpan<char> contents = _input[..length];

        return new Token(tokenType, Helper.RangeFrom(_line, _linePosition, _line, _linePosition + length), contents.ToString());
    }

    private Token? DoCharMatchIfWordBoundary(TokenType tokenType, string lexeme)
    {
        char boundary = InputAt(lexeme.Length);
        if (!IsWordChar(boundary))
        {
            return CharMatch(tokenType, lexeme);
        }
        return default;
    }

    private Token CharMatch(TokenType tokenType, string lexeme)
    {
        return new Token(tokenType, Helper.RangeFrom(_line, _linePosition, _line, _linePosition + lexeme.Length), lexeme);
    }

    private bool IsWordChar(char c)
    {
        return char.IsLetterOrDigit(c) || c == '_';
    }

    private bool AtNewLine(int offset)
    {
        char current = InputAt(offset);
        return current == '\n' || (current == '\r' && InputAt(offset + 1) == '\n');
    }

    private char InputAt(int index)
    {
        if(index >= _input.Length)
        {
            return '\0';
        }
        return _input[index];
    }

    private bool StartsWithKeyword(string keyword)
    {
        return _input.StartsWith(keyword, StringComparison.OrdinalIgnoreCase) && !IsWordChar(_input[keyword.Length]);
    }

    private bool SeekMatch(Regex regex, out int length)
    {
        ValueMatchEnumerator enumerator = regex.EnumerateMatches(_input);

        // Got a match
        if (enumerator.MoveNext())
        {
            ValueMatch result = enumerator.Current;

            if(result.Index != 0)
            {
                throw new Exception("Regex is not obeying start of string rule");
            }
            length = result.Length;
            return true;
        }

        // No match found
        length = default;
        return false;
    }
}
