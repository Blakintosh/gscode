using GSCode.Data;
using GSCode.Lexer.Types;
using GSCode.Parser.AST.Expressions;
using GSCode.Parser.Data;
using GSCode.Parser.SPA.Logic.Analysers;
using GSCode.Parser.SPA.Logic.Components;
using GSCode.Parser.SPA.Sense;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace GSCode.Parser.AST.Nodes
{
    internal static class NodeData
    {
        /// <summary>
        /// Body node factories such as if, else, while, return, etc.
        /// </summary>
        public static List<IASTNodeFactory> BodyFactories { get; } = new()
        {
            // if(a)
            new FlexiBranchingNodeFactory(NodeTypes.IfStatement,
                new List<INodeComponent>()
                {
                    new KeywordComponent(KeywordTypes.If),
                    new SingletonComponent()
                    {
                        Type = TokenType.Punctuation,
                        SubType = PunctuationTypes.OpenParen
                    },
                    new ExpressionComponent(),
                    new SingletonComponent()
                    {
                        Type = TokenType.Punctuation,
                        SubType = PunctuationTypes.CloseParen
                    }
                },
                InheritFactories, new IfStatementAnalyser()),
            // else if(a)
            new FlexiBranchingNodeFactory(NodeTypes.ElseIfStatement,
                new List<INodeComponent>()
                {
                    new KeywordComponent(KeywordTypes.Else),
                    new KeywordComponent(KeywordTypes.If),
                    new SingletonComponent()
                    {
                        Type = TokenType.Punctuation,
                        SubType = PunctuationTypes.OpenParen
                    },
                    new ExpressionComponent(),
                    new SingletonComponent()
                    {
                        Type = TokenType.Punctuation,
                        SubType = PunctuationTypes.CloseParen
                    }
                },
                InheritFactories, new ElseIfStatementAnalyser()),
            // else
            new FlexiBranchingNodeFactory(NodeTypes.ElseStatement,
                new List<INodeComponent>()
                {
                    new KeywordComponent(KeywordTypes.Else)
                },
                InheritFactories, new ElseStatementAnalyser()),
            // do
            new FlexiBranchingNodeFactory(NodeTypes.DoLoop,
                new List<INodeComponent>()
                {
                    new KeywordComponent(KeywordTypes.Do)
                },
                InheritFactories),
            // while(a)
            new OptionalFlexiBranchingNodeFactory(NodeTypes.WhileLoop,
                new List<INodeComponent>()
                {
                    new KeywordComponent(KeywordTypes.While),
                    new SingletonComponent()
                    {
                        Type = TokenType.Punctuation,
                        SubType = PunctuationTypes.OpenParen
                    },
                    new ExpressionComponent(),
                    new SingletonComponent()
                    {
                        Type = TokenType.Punctuation,
                        SubType = PunctuationTypes.CloseParen
                    }
                },
                InheritFactories, 
                new WhileLoopAnalyser()),
            // for(a; b; c)
            new FlexiBranchingNodeFactory(NodeTypes.ForLoop,
                new List<INodeComponent>()
                {
                    new KeywordComponent(KeywordTypes.For),
                    new SingletonComponent()
                    {
                        Type = TokenType.Punctuation,
                        SubType = PunctuationTypes.OpenParen
                    },
                    new ExpressionComponent(),
                    new SemiColonComponent(),
                    new ExpressionComponent(),
                    new SemiColonComponent(),
                    new ExpressionComponent(),
                    new SingletonComponent()
                    {
                        Type = TokenType.Punctuation,
                        SubType = PunctuationTypes.CloseParen
                    },
                },
                InheritFactories),
            // foreach(a in b)
            new FlexiBranchingNodeFactory(NodeTypes.ForeachLoop,
                new List<INodeComponent>()
                {
                    new KeywordComponent(KeywordTypes.Foreach),
                    new SingletonComponent()
                    {
                        Type = TokenType.Punctuation,
                        SubType = PunctuationTypes.OpenParen
                    },
                    new ExpressionComponent(),
                    new KeywordComponent(KeywordTypes.In),
                    new ExpressionComponent(),
                    new SingletonComponent()
                    {
                        Type = TokenType.Punctuation,
                        SubType = PunctuationTypes.CloseParen
                    },
                },
                InheritFactories),
            // switch a (Not currently supported properly)
            new FlexiBranchingNodeFactory(NodeTypes.SwitchStatement,
                new List<INodeComponent>()
                {
                    new KeywordComponent(KeywordTypes.Switch),
                    new ExpressionComponent()
                },
                InheritFactories),
            // waittillframeend
            new StatementNodeFactory(NodeTypes.WaittillFrameEndStatement,
                new List<INodeComponent>()
                {
                    new KeywordComponent(KeywordTypes.WaittillFrameEnd)
                }),
            // return a
            new StatementNodeFactory(NodeTypes.ReturnStatement,
                new List<INodeComponent>()
                {
                    new KeywordComponent(KeywordTypes.Return),
                    new ExpressionComponent()
                }, new ReturnStatementAnalyser()),
            // wait a
            new StatementNodeFactory(NodeTypes.WaitStatement,
                new List<INodeComponent>()
                {
                    new KeywordComponent(KeywordTypes.Wait),
                    new ExpressionComponent()
                }, new WaitStatementAnalyser()),
            // waitrealtime a
            new StatementNodeFactory(NodeTypes.WaitRealTimeStatement,
                new List<INodeComponent>()
                {
                    new KeywordComponent(KeywordTypes.WaitRealTime),
                    new ExpressionComponent()
                }),
            // const a
            new StatementNodeFactory(NodeTypes.ConstDeclaration,
                new List<INodeComponent>()
                {
                    new KeywordComponent(KeywordTypes.Const),
                    new ExpressionComponent()
                }, new ConstDeclarationAnalyser()),
            // break
            new StatementNodeFactory(NodeTypes.BreakStatement,
                new List<INodeComponent>()
                {
                    new KeywordComponent(KeywordTypes.Break)
                }),
            // continue
            new StatementNodeFactory(NodeTypes.ContinueStatement,
                new List<INodeComponent>()
                {
                    new KeywordComponent(KeywordTypes.Continue)
                }),
            // case a:
            new CaseLabelNodeFactory(NodeTypes.CaseStatement,
                new List<INodeComponent>()
                {
                    new KeywordComponent(KeywordTypes.Case),
                    new ExpressionComponent()
                }),
            // default:
            new CaseLabelNodeFactory(NodeTypes.DefaultStatement,
                new List<INodeComponent>()
                {
                    new KeywordComponent(KeywordTypes.Default)
                }),
            // Just a ;
            new EmptyNodeFactory(),
            // A {
            new BracedNodeFactory(),
            // A /#
            new DevBlockNodeFactory(),
            // Any expression
            new ExpressionNodeFactory()
        };

        /// <summary>
        /// Class body node factories such as var, constructor, destructor, etc.
        /// </summary>
        public static List<IASTNodeFactory> ClassBodyFactories { get; } = new()
        {
            // constructor(a)
            new BracedBranchingNodeFactory(NodeTypes.ClassConstructor,
                new List<INodeComponent>()
                {
                    new KeywordComponent(KeywordTypes.Constructor),
                    new ExpressionComponent()
                },
                BodyFactories),
            // destructor(a)
            new BracedBranchingNodeFactory(NodeTypes.ClassDestructor,
                new List<INodeComponent>()
                {
                    new KeywordComponent(KeywordTypes.Destructor),
                    new ExpressionComponent()
                },
                BodyFactories),
            // var a
            // TODO: Check if assignment works
            new StatementNodeFactory(NodeTypes.ClassField,
                new List<INodeComponent>()
                {
                    new KeywordComponent(KeywordTypes.Var),
                    new NameComponent()
                }),
            // function a(b)
            new BracedBranchingNodeFactory(NodeTypes.FunctionDeclaration,
                new List<INodeComponent>()
                {
                    new KeywordComponent(KeywordTypes.Function),
                    new SingletonComponent()
                    {
                        Type = TokenType.Keyword,
                        Optional = true
                    },
                    new SingletonComponent()
                    {
                        Type = TokenType.Keyword,
                        Optional = true
                    },
                    new NameComponent(),
                    new ExpressionComponent()
                },
                BodyFactories,
                null,
                new FunctionDeclarationSignatureAnalyser()),
            // probably valid
            // A /#
            new DevBlockNodeFactory(),
        };

        /// <summary>
        /// Root node factories such as function, class, using, etc.
        /// </summary>
        public static List<IASTNodeFactory> RootFactories { get; } = new()
        {
            // function a(b)
            new BracedBranchingNodeFactory(NodeTypes.FunctionDeclaration,
                new List<INodeComponent>()
                {
                    new KeywordComponent(KeywordTypes.Function),
                    new SingletonComponent()
                    {
                        Type = TokenType.Keyword,
                        Optional = true
                    },
                    new SingletonComponent()
                    {
                        Type = TokenType.Keyword,
                        Optional = true
                    },
                    new NameComponent(),
                    new SingletonComponent()
                    {
                        Type = TokenType.Punctuation,
                        SubType = PunctuationTypes.OpenParen
                    },
                    new ExpressionComponent(),
                    new SingletonComponent()
                    {
                        Type = TokenType.Punctuation,
                        SubType = PunctuationTypes.CloseParen
                    }
                },
                BodyFactories, null, new FunctionDeclarationSignatureAnalyser()),
            // class a : b
            new BracedBranchingNodeFactory(NodeTypes.ClassDeclaration,
                new List<INodeComponent>()
                {
                    new KeywordComponent(KeywordTypes.Class),
                    new NameComponent(),
                    new SingletonComponent()
                    {
                        Type = TokenType.Operator,
                        SubType = OperatorTypes.Colon,
                        Optional = true
                    },
                    new NameComponent()
                    {
                        Optional = true,
                    }
                },
                ClassBodyFactories),
            // #using a
            new StatementNodeFactory(NodeTypes.UsingDirective,
                new List<INodeComponent>()
                {
                    new KeywordComponent(KeywordTypes.Using),
                    new FilePathComponent()
                }, null, new UsingDirectiveAnalyser()),
            // #namespace a
            new StatementNodeFactory(NodeTypes.NamespaceDirective,
                new List<INodeComponent>()
                {
                    new KeywordComponent(KeywordTypes.Namespace),
                    new NameComponent()
                }, null, new NamespaceDirectiveAnalyser()),
            // #precache(a)
            new StatementNodeFactory(NodeTypes.PrecacheDirective,
                new List<INodeComponent>()
                {
                    new KeywordComponent(KeywordTypes.Precache),
                    new ExpressionComponent()
                }),
            // #using_animtree(a)
            new StatementNodeFactory(NodeTypes.UsingAnimTreeDirective,
                new List<INodeComponent>()
                {
                    new KeywordComponent(KeywordTypes.UsingAnimTree),
                    new ExpressionComponent()
                }),
            // A /#
            new DevBlockNodeFactory(),
        };

        /// <summary>
        /// Inherit factories from the parent branch, e.g. for an if inside a loop
        /// </summary>
        public static List<IASTNodeFactory>? InheritFactories { get; } = null;
    }
}
