using GSCode.Data;
using GSCode.Lexer.Types;
using GSCode.Parser.AST.Expressions;
using GSCode.Parser.Util;
using System.Data;

namespace GSCode.Parser.AST.Nodes
{
    internal interface INodeComponent
    {
        public bool Optional { get; init; }
        public bool Parse(ref Token currentToken, ASTHelper data);

        public INodeComponent Clone();
    }

    internal class SingletonComponent : INodeComponent
    {
        public virtual TokenType Type { get; init; }
        public Enum? SubType { get; init; }
        public bool Optional { get; init; } = false;
        public Token? Token { get; private set; }

        public bool Parse(ref Token currentToken, ASTHelper data)
        {
            if(currentToken.Is(Type, SubType))
            {
                Token = currentToken;
                currentToken = currentToken.NextConcrete();
                return true;
            }
            return false;
        }

        public virtual INodeComponent Clone()
        {
            return new SingletonComponent
            {
                Type = Type,
                SubType = SubType,
                Optional = Optional,
                Token = Token
            };
        }
    }

    internal sealed class KeywordComponent : SingletonComponent
    {
        public override TokenType Type { get; init; } = TokenType.Keyword;

        public KeywordComponent(Enum subType, bool optional = false)
        {
            SubType = subType;
            Optional = optional;
        }

        public override INodeComponent Clone()
        {
            return new KeywordComponent(SubType!, Optional)
            {
                Type = Type
            };
        }
    }

    internal sealed class NameComponent : SingletonComponent
    {
        public override TokenType Type { get; init; } = TokenType.Name;

        public string GetSymbolName()
        {
            return Token!.Contents;
        }

        public override INodeComponent Clone()
        {
            return new NameComponent
            {
                Type = Type,
                SubType = SubType,
                Optional = Optional
            };
        }
    }

    internal sealed class SemiColonComponent : SingletonComponent
    {
        public override TokenType Type { get; init; } = TokenType.SpecialToken;

        public SemiColonComponent()
        {
            SubType = SpecialTokenTypes.SemiColon;
        }

        public override INodeComponent Clone()
        {
            return new SemiColonComponent
            {
                Type = Type,
                SubType = SubType,
                Optional = Optional
            };
        }
    }

    internal sealed class ExpressionComponent : INodeComponent
    {
        public Expression Expression { get; } = new();
        public bool Optional { get; init; } = false;

        public bool Parse(ref Token currentToken, ASTHelper data)
        {
            return Expression.Parse(ref currentToken, data);
        }

        public INodeComponent Clone()
        {
            return new ExpressionComponent
            {
                Optional = Optional
            };
        }
    }
    
    internal sealed class ArgumentListComponent : INodeComponent
    {
        public List<Expression> ArgumentExpressions { get; } = [];
        public bool Optional { get; init; } = false;

        public bool Parse(ref Token currentToken, ASTHelper data)
        {
            // return Expression.Parse(ref currentToken, data);
            bool success = true;

            do
            {
                Expression argument = new();
                success = argument.Parse(ref currentToken, data);

                ArgumentExpressions.Add(argument);
            } while (success && currentToken.Is(TokenType.SpecialToken, SpecialTokenTypes.Comma));
            
            return success;
        }

        public INodeComponent Clone()
        {
            return new ExpressionComponent
            {
                Optional = Optional
            };
        }
    }

    internal sealed class FilePathComponent : INodeComponent
    {
        public bool Optional { get; init; } = false;
        public string? ScriptPath { get; set; }

        public bool Parse(ref Token currentToken, ASTHelper data)
        {
            Token firstToken = currentToken;
            string? relativeScriptPath = ParserUtil.ConvertImportSequenceToString(firstToken, false, out Token lastToken);

            if(relativeScriptPath is not null)
            {
                ScriptPath = ParserUtil.GetScriptFilePath(data.ScriptFile, relativeScriptPath, "gsc");
                currentToken = lastToken;
                if(string.IsNullOrEmpty(ScriptPath))
                {
                    data.AddDiagnostic(Token.RangeBetweenTokens(firstToken, lastToken), GSCErrorCodes.MissingScript, relativeScriptPath);
                    return false;
                }
                return true;
            }
            return false;
        }

        public INodeComponent Clone()
        {
            return new FilePathComponent
            {
                Optional = Optional
            };
        }
    }
}
