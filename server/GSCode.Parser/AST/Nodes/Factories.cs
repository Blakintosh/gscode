using GSCode.Data;
using GSCode.Lexer.Types;
using GSCode.Parser.AST.Expressions;
using GSCode.Parser.SPA.Logic.Analysers;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection.PortableExecutable;
using System.Text;
using System.Threading.Tasks;

namespace GSCode.Parser.AST.Nodes
{
    internal interface IASTNodeFactory
    {
        /// <summary>
        /// Gets whether the current tokens match this node type.
        /// </summary>
        /// <param name="currentToken">The current token in the token linked list.</param>
        /// <returns>true if a match</returns>
        public bool Matches(Token currentToken);

        /// <summary>
        /// Parses using this type and generates an AST node from it.
        /// </summary>
        /// <param name="currentToken">Reference to the current token in the token linked list.</param>
        /// <param name="data">Reference to the token data class.</param>
        /// <param name="parentFactories">Reference to the parent factory list, for branching purposes.</param>
        /// <returns>An instance of an ASTNode of this type with its relevant components if end of node was reached, e.g. a semicolon.</returns>
        public ASTNode? Parse(ref Token currentToken, ASTHelper data, List<IASTNodeFactory> parentFactories);
    }

    /// <summary>
    /// Standard non-branching statement node, e.g. return
    /// </summary>
    internal class StatementNodeFactory : IASTNodeFactory
    {
        public NodeTypes Type { get; init; }
        public List<INodeComponent> Components { get; init; }
        public DataFlowNodeAnalyser? Analyzer { get; init; }
        public SignatureNodeAnalyser? SignatureAnalyzer { get; init; }

        public StatementNodeFactory(NodeTypes type, List<INodeComponent> components, DataFlowNodeAnalyser? analyzer = null,
            SignatureNodeAnalyser? signatureAnalyzer = null)
        {
            Type = type;
            Components = components;
            Analyzer = analyzer;
            SignatureAnalyzer = signatureAnalyzer;
        }

        public virtual bool Matches(Token currentToken)
        {
            foreach(INodeComponent component in Components)
            {
                if(component is not SingletonComponent)
                {
                    return true;
                }

                SingletonComponent singletonComponent = (SingletonComponent)component;
                if (currentToken.Is(singletonComponent.Type, singletonComponent.SubType))
                {
                    currentToken = currentToken.NextConcrete();
                    continue;
                }

                if(!singletonComponent.Optional)
                {
                    return false;
                }
            }
            return true;
        }

        public virtual ASTNode? Parse(ref Token currentToken, ASTHelper data, List<IASTNodeFactory> parentFactories)
        {
            Token firstToken = currentToken;

            List<INodeComponent>? components = ParseComponents(ref currentToken, data);

            if(components is null)
            {
                return null;
            }

            ASTNode? node = new(Type, components, firstToken, new Range()
            {
                Start = firstToken.TextRange.Start,
                End = currentToken.TextRange.End,
            }, Analyzer, SignatureAnalyzer);

            // Check for a semicolon
            if (!CheckEndOfStatement(currentToken))
            {
                Token lastToken = currentToken.Previous!;

                // Add an error after the last token if no semicolon
                data.AddDiagnostic(lastToken.CharacterRangeAfterToken(), GSCErrorCodes.MissingToken, ";");
                return node;
            }

            currentToken = currentToken.NextConcrete();
            return node;
        }

        protected List<INodeComponent>? ParseComponents(ref Token currentToken, ASTHelper data)
        {
            /*
             * Check for semicolon being reached early, if this occurs:
             * continue thru components until one found that is not optional. Run parse on this component,
             * allowing its error to be pushed. then return an AST node.
             */
            List<INodeComponent> nodeComponents = new();
            for (int i = 0; i < Components.Count; i++)
            {
                INodeComponent component = Components[i].Clone();
                if (CheckEndOfStatement(currentToken) && component is not SemiColonComponent &&
                    component is not ExpressionComponent)
                {
                    while (i < Components.Count && Components[i].Optional)
                    {
                        i++;
                    }

                    if (i < Components.Count)
                    {
                        Components[i].Parse(ref currentToken, data);
                        return Components.GetRange(0, i);
                    }
                }

                bool result = component.Parse(ref currentToken, data);
                if (!result)
                {
                    if(component.Optional)
                    {
                        continue;
                    }
                    return null;
                }

                nodeComponents.Add(component);
            }
            return nodeComponents;
        }

        protected virtual bool CheckEndOfStatement(Token token)
        {
            return token.Is(TokenType.SpecialToken, SpecialTokenTypes.SemiColon);
        }
    }
    
    /// <summary>
    /// Like a standard statement, but uses a colon at the end. E.g. case foo:
    /// </summary>
    internal sealed class CaseLabelNodeFactory : StatementNodeFactory
    {
        public CaseLabelNodeFactory(NodeTypes type, List<INodeComponent> components, DataFlowNodeAnalyser? analyser = null) : base(type, components, analyser) {}

        protected sealed override bool CheckEndOfStatement(Token token)
        {
            return token.Is(TokenType.Operator, OperatorTypes.Colon);
        }
    }

    /// <summary>
    /// Strict bracing, used for e.g. function foo() { }. No option for a single-line variant
    /// </summary>
    internal sealed class BracedBranchingNodeFactory : StatementNodeFactory
    {
        public List<IASTNodeFactory> ChildrenFactories { get; }
        public BracedBranchingNodeFactory(NodeTypes type, List<INodeComponent> components, List<IASTNodeFactory> childrenFactories, 
            DataFlowNodeAnalyser? analyzer = null, SignatureNodeAnalyser? signatureAnalyzer = null) : base(type, components, analyzer, signatureAnalyzer)
        {
            ChildrenFactories = childrenFactories;
        }

        public override ASTNode? Parse(ref Token currentToken, ASTHelper data, List<IASTNodeFactory> parentFactories)
        {
            Token firstToken = currentToken;
            List<INodeComponent>? components = ParseComponents(ref currentToken, data);

            if (components is null)
            {
                return null;
            }

            ASTNode? node = new(Type, components, firstToken, new Range()
            {
                Start = firstToken.TextRange.Start,
                End = currentToken.TextRange.End,
            }, Analyzer, SignatureAnalyzer, new BracedASTBranch(ChildrenFactories));

            return node;
        }
    }

    /// <summary>
    /// Flexi branching, used e.g. for if(), these statements can be one liners with no braces.
    /// </summary>
    internal sealed class FlexiBranchingNodeFactory : StatementNodeFactory
    {
        public List<IASTNodeFactory>? ChildrenFactories { get; }
        public FlexiBranchingNodeFactory(NodeTypes type, List<INodeComponent> components, List<IASTNodeFactory>? childrenFactories,
            DataFlowNodeAnalyser? analyser = null) : base(type, components, analyser)
        {
            ChildrenFactories = childrenFactories;
        }

        public override ASTNode? Parse(ref Token currentToken, ASTHelper data, List<IASTNodeFactory> parentFactories)
        {
            Token firstToken = currentToken;

            List<INodeComponent>? components = ParseComponents(ref currentToken, data);

            if (components is null)
            {
                return null;
            }

            // Determine if a singleton branch or braced.
            bool braced = currentToken.Is(TokenType.Punctuation, PunctuationTypes.OpenBrace);

            List<IASTNodeFactory> childFactories = ChildrenFactories ?? parentFactories;
            ASTBranch branch = braced ? new BracedASTBranch(childFactories) : new SingletonASTBranch(childFactories);

            ASTNode? node = new(Type, components, firstToken, new Range()
            {
                Start = firstToken.TextRange.Start,
                End = currentToken.TextRange.End,
            }, Analyzer, null, branch);

            return node;
        }
    }

    /// <summary>
    /// Optionally branching - used for while loops where they can belong to do-while or while standalone
    /// </summary>
    internal sealed class OptionalFlexiBranchingNodeFactory : StatementNodeFactory
    {
        public List<IASTNodeFactory>? ChildrenFactories { get; }
        public OptionalFlexiBranchingNodeFactory(NodeTypes type, List<INodeComponent> components, List<IASTNodeFactory>? childrenFactories, 
            DataFlowNodeAnalyser? analyzer = null) : base(type, components, analyzer)
        {
            ChildrenFactories = childrenFactories;
        }

        public override ASTNode? Parse(ref Token currentToken, ASTHelper data, List<IASTNodeFactory> parentFactories)
        {
            Token firstToken = currentToken;

            List<INodeComponent>? components = ParseComponents(ref currentToken, data);

            if (components is null)
            {
                return null;
            }

            if(!CheckEndOfStatement(currentToken))
            {
                // Determine if a singleton branch or braced.
                bool braced = currentToken.Is(TokenType.Punctuation, PunctuationTypes.OpenBrace);

                List<IASTNodeFactory> childFactories = ChildrenFactories ?? parentFactories;
                ASTBranch branch = braced ? new BracedASTBranch(childFactories) : new SingletonASTBranch(childFactories);

                return new(Type, components, firstToken, new Range()
                {
                    Start = firstToken.TextRange.Start,
                    End = currentToken.TextRange.End,
                }, Analyzer, null, branch);
            }

            return new(Type, components, firstToken, new Range()
            {
                Start = firstToken.TextRange.Start,
                End = currentToken.TextRange.End,
            }, Analyzer);
        }
    }

    /// <summary>
    /// Empty statement node
    /// </summary>
    internal sealed class EmptyNodeFactory : StatementNodeFactory
    {
        public EmptyNodeFactory() : base(NodeTypes.EmptyStatement,
            new() {})
        { }

        public override bool Matches(Token currentToken)
        {
            return currentToken.Is(TokenType.SpecialToken, SpecialTokenTypes.SemiColon);
        }
    }

    /// <summary>
    /// Braced node
    /// </summary>
    internal sealed class BracedNodeFactory : StatementNodeFactory
    {
        public BracedNodeFactory() : base(NodeTypes.EmptyStatement,
            new() { })
        { }

        public override bool Matches(Token currentToken)
        {
            return currentToken.Is(TokenType.Punctuation, PunctuationTypes.OpenBrace);
        }

        public override ASTNode? Parse(ref Token currentToken, ASTHelper data, List<IASTNodeFactory> parentFactories)
        {
            Token firstToken = currentToken;
            List<INodeComponent>? components = ParseComponents(ref currentToken, data);

            if (components is null)
            {
                return null;
            }
            
            return new(Type, components, firstToken, new Range()
            {
                Start = firstToken.TextRange.Start,
                End = currentToken.TextRange.End,
            }, Analyzer, null, new BracedASTBranch(parentFactories));
        }
    }

    /// <summary>
    /// Dev block node
    /// </summary>
    internal sealed class DevBlockNodeFactory : StatementNodeFactory
    {
        public DevBlockNodeFactory() : base(NodeTypes.EmptyStatement,
            new() { })
        { }

        public override bool Matches(Token currentToken)
        {
            return currentToken.Is(TokenType.Punctuation, PunctuationTypes.OpenDevBlock);
        }

        public override ASTNode? Parse(ref Token currentToken, ASTHelper data, List<IASTNodeFactory> parentFactories)
        {
            Token firstToken = currentToken;
            List<INodeComponent>? components = ParseComponents(ref currentToken, data);

            if (components is null)
            {
                return null;
            }

            return new(Type, components, firstToken, new Range()
            {
                Start = firstToken.TextRange.Start,
                End = currentToken.TextRange.End,
            }, Analyzer, null, new DevBlockASTBranch(parentFactories));
        }
    }

    /// <summary>
    /// Expression node - lowest precedence
    /// </summary>
    internal sealed class ExpressionNodeFactory : StatementNodeFactory
    {
        public ExpressionNodeFactory() : base(NodeTypes.ExpressionStatement, 
            new()
            {
                new ExpressionComponent()
            }, 
            new ExpressionStatementAnalyser())
        {}

        public override bool Matches(Token currentToken)
        {
            return Expression.IsOperatorOrOperand(currentToken);
        }
    }
}
