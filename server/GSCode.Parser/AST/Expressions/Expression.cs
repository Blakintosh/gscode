

using GSCode.Data;
using GSCode.Lexer.Types;
using Microsoft.VisualStudio.LanguageServer.Protocol;

namespace GSCode.Parser.AST.Expressions;

internal sealed class Expression
{
    /// <summary>
    /// A successful expression parsing result stores its root tree node here.
    /// </summary>
    public IExpressionNode? Root { get; private set; }

    /// <summary>
    /// Whether the expression is empty.
    /// </summary>
    public bool Empty => Root is EmptyNode;

    /// <summary>
    /// Whether the expression failed to parse during syntatic analysis.
    /// </summary>
    public bool Failed => Root is null;

    /// <summary>
    /// The range of the expression.
    /// </summary>
    public Range Range => Root?.Range ?? throw new InvalidOperationException("Cannot get the range when Root is not defined.");

    public bool Parse(ref Token baseToken, ASTHelper data)
    {
        // Convert tokens to a parseable node list
        List<IExpressionNode> nodes = ConvertTokensToExpressionNodes(ref baseToken);

        if(nodes.Count != 0)
        {
            // Mutate the list to produce the enclosure types, such as (), [[ ]].
            if(TransformEnclosures(nodes, data))
            {
                // Mutate the list through each precedence level to produce the list with operations
                if(ParseOperations(nodes, data))
                {
                    // Error on any nodes remaining
                    return ReviewExpressionNodes(nodes, data);
                }
            }
        }
        else
        {
            Root = new EmptyNode();
            return true;
        }
        return false;
    }

    private static bool ParseOperations(List<IExpressionNode> nodes, ASTHelper data)
    {
        if(!ParseNestedEnclosureOperations(nodes, data))
        {
            return false;
        }

        foreach(OperatorCategory operatorCategory in OperatorData.OperationPrecedencesList)
        {
            bool invertedAssociativity = operatorCategory.Associativity == OperatorAssociativity.RightToLeft;
            int i = invertedAssociativity ? nodes.Count - 1 : 0;
            while(i < nodes.Count && i >= 0)
            {

                bool rewind = TransformUsingCategoryFactories(nodes, operatorCategory, i);
                if(rewind)
                {
                    i = invertedAssociativity ? nodes.Count - 1 : 0;
                }
                else
                {
                    i += invertedAssociativity ? -1 : 1;
                }
            }
        }

        // TODO: there's probably a better way to deal with this
        if (nodes.Count == 1 &&
            (nodes[0].NodeType == ExpressionNodeType.UnresolvedPunctuation ||
            nodes[0].NodeType == ExpressionNodeType.UnresolvedOperator))
        {
            data.AddDiagnostic(nodes[0].Range, GSCErrorCodes.InvalidExpressionTerm);
            return false;
        }

        return true;
    }

    private static bool ParseNestedEnclosureOperations(List<IExpressionNode> nodes, ASTHelper data)
    {
        foreach (IExpressionNode node in nodes)
        {
            if (node.NodeType == ExpressionNodeType.Enclosure)
            {
                EnclosureNode enclosureNode = (EnclosureNode)node;
                if (!ParseOperations(enclosureNode.InteriorNodes, data))
                {
                    return false;
                }
            }
        }
        return true;
    }

    private static bool TransformUsingCategoryFactories(List<IExpressionNode> nodes, OperatorCategory operatorCategory, int i)
    {
        for (int j = 0; j < operatorCategory.OperationFactories.Count; j++)
        {
            if (operatorCategory.OperationFactories[j].Matches(nodes, i))
            {
                // When match performed, run through all factories again for any further matches
                return true;
            }
        }
        return false;
    }

    private bool ReviewExpressionNodes(List<IExpressionNode> nodes, ASTHelper data)
    {
        bool result = ReviewNodes(nodes, data);
        if (result)
        {
            if(nodes.Count == 0)
            {
                Root = new EmptyNode();
                return result;
            }
            Root = nodes[0];
        }
        return result;
    }

    private static bool ReviewNodes(List<IExpressionNode> nodes, ASTHelper data)
    {
        if (nodes.Count > 1)
        {
            // TODO: just fail on the first token of this "node"
            for(int i = 1; i < nodes.Count; i++)
            {
                //data.AddDiagnostic(nodes[i].Range, GSCErrorCodes.TokenNotValidInContext, "CAN'TGETCONTENT");
                // Todo: Make this more friendly asap
                data.AddDiagnostic(nodes[i].Range, GSCErrorCodes.InvalidExpressionTerm);
            }
        }

        // no valid parsing
        if (nodes[0].NodeType == ExpressionNodeType.UnresolvedPunctuation ||
            nodes[0].NodeType == ExpressionNodeType.UnresolvedOperator)
        {
            data.AddDiagnostic(nodes[0].Range, GSCErrorCodes.InvalidExpressionTerm);
            return false;
        }

        return true;
    }

    #region Enclosures

    private static bool CheckDereferenceTokens(List<IExpressionNode> nodes, int i, PunctuationTypes type)
    {
        if (i + 1 < nodes.Count &&
            nodes[i].NodeType == ExpressionNodeType.UnresolvedPunctuation &&
            nodes[i + 1].NodeType == ExpressionNodeType.UnresolvedPunctuation)
        {
            TokenNode firstAsTokenNode = (TokenNode)nodes[i];
            TokenNode secondAsTokenNode = (TokenNode)nodes[i + 1];

            return firstAsTokenNode.SourceToken.Is(TokenType.Punctuation, type) &&
                secondAsTokenNode.SourceToken.Is(TokenType.Punctuation, type);
        }
        return false;
    }
    private static bool CheckOneEnclosureToken(List<IExpressionNode> nodes, int i, PunctuationTypes type)
    {
        if (nodes[i].NodeType == ExpressionNodeType.UnresolvedPunctuation)
        {
            TokenNode firstAsTokenNode = (TokenNode)nodes[i];

            return firstAsTokenNode.SourceToken.Is(TokenType.Punctuation, type);
        }
        return false;
    }

    // Conditions for the enclosures
    private readonly static Func<List<IExpressionNode>, int, bool> dereferenceStartCondition = (List<IExpressionNode> nodes, int i) =>
    {
        return CheckDereferenceTokens(nodes, i, PunctuationTypes.OpenBracket);
    };
    private readonly static Func<List<IExpressionNode>, int, bool> dereferenceEndCondition = (List<IExpressionNode> nodes, int i) =>
    {
        return CheckDereferenceTokens(nodes, i, PunctuationTypes.CloseBracket);
    };
    private readonly static Func<List<IExpressionNode>, int, bool> bracketsStartCondition = (List<IExpressionNode> nodes, int i) =>
    {
        return CheckOneEnclosureToken(nodes, i, PunctuationTypes.OpenBracket);
    };
    private readonly static Func<List<IExpressionNode>, int, bool> bracketsEndCondition = (List<IExpressionNode> nodes, int i) =>
    {
        return CheckOneEnclosureToken(nodes, i, PunctuationTypes.CloseBracket);
    };
    private readonly static Func<List<IExpressionNode>, int, bool> parenthesisStartCondition = (List<IExpressionNode> nodes, int i) =>
    {
        return CheckOneEnclosureToken(nodes, i, PunctuationTypes.OpenParen);
    };
    private readonly static Func<List<IExpressionNode>, int, bool> parenthesisEndCondition = (List<IExpressionNode> nodes, int i) =>
    {
        return CheckOneEnclosureToken(nodes, i, PunctuationTypes.CloseParen);
    };

    private readonly static List<(EnclosureType Type, Func<List<IExpressionNode>, int, bool> Start, Func<List<IExpressionNode>, int, bool> End, int Length, string EndLiteral)> enclosureConditionList = new()
    {
        (EnclosureType.Dereference, dereferenceStartCondition, dereferenceEndCondition, 2, "]]"),
        (EnclosureType.Bracket, bracketsStartCondition, bracketsEndCondition, 1, "]"),
        (EnclosureType.Parenthesis, parenthesisStartCondition, parenthesisEndCondition, 1, ")"),
    };

    /// <summary>
    /// Scans for enclosure openers and converts these to enclosure nodes. This is the highest precedence in expression parsing.
    /// </summary>
    /// <param name="nodes">Reference to the node list</param>
    /// <param name="data">Reference to the data class for diagnostics</param>
    /// <returns>true if transformed with no errors, false otherwise</returns>
    private static bool TransformEnclosures(List<IExpressionNode> nodes, ASTHelper data)
    {
        int i = 0;
        while (i < nodes.Count)
        {
            // Check for and parse any enclosures
            bool enclosureFound = AnyEnclosureStarts(nodes, i, out var enclosureData);
            if (enclosureFound)
            {
                bool enclosureParseSuccess = ParseEnclosure(nodes, data, i, enclosureData.Type, enclosureData.EndLiteral, enclosureData.Length, enclosureData.End);
                if (!enclosureParseSuccess)
                {
                    return false;
                }
            }

            i++;
        }
        return true;
    }

    /// <summary>
    /// Parses a found enclosure, and converts it into an enclosure node containing a set of nodes.
    /// </summary>
    /// <param name="nodes">Reference to node list</param>
    /// <param name="data">Reference to data class</param>
    /// <param name="i">Current index on node list</param>
    /// <param name="type">Enclosure type enum</param>
    /// <param name="endLiteral">String of the end literal if diagnostics required</param>
    /// <param name="length">Length of the end nodes</param>
    /// <param name="closureConditionFunc">Condition function to determine whether to close this enclosure</param>
    /// <returns>true if successful</returns>
    private static bool ParseEnclosure(List<IExpressionNode> nodes, ASTHelper data, int i, EnclosureType type, string endLiteral, int length, Func<List<IExpressionNode>, int, bool> closureConditionFunc)
    {
        List<IExpressionNode> enclosedNodes = new();
        int initialIndex = i;
        Position start = nodes[i].Range.Start;
        i += length;

        while (i < nodes.Count)
        {
            // Check for and parse any nested enclosures
            bool enclosureFound = AnyEnclosureStarts(nodes, i, out var enclosureData);
            if (enclosureFound)
            {
                bool enclosureParseSuccess = ParseEnclosure(nodes, data, i, enclosureData.Type, enclosureData.EndLiteral, enclosureData.Length, enclosureData.End);
                if(!enclosureParseSuccess)
                {
                    return false;
                }
            }

            // Check if at the end of the enclosure
            if(closureConditionFunc(nodes, i))
            {
                Position end = nodes[i].Range.End;
                // TODO won't work as-is: needs to remove multiple brackets e.g.
                nodes.RemoveRange(initialIndex, i - initialIndex + length);
                nodes.Insert(initialIndex, new EnclosureNode(enclosedNodes, type, RangeHelper.From(start, end)));
                return true;
            }

            // Push this node onto the enclosed nodes list
            enclosedNodes.Add(nodes[i++]);
        }

        // No suitable closure found, push an error.
        data.AddDiagnostic(nodes[i - 1].Range, GSCErrorCodes.MissingToken, endLiteral);
        return false;
    }

    /// <summary>
    /// Gets whether any enclosure type opens at this index, and if so, returns its conditional and type data as an output.
    /// </summary>
    /// <param name="nodes">Reference to the node list</param>
    /// <param name="i">Current node index</param>
    /// <param name="enclosureData">Output enclosure data if matched</param>
    /// <returns>true if a match</returns>
    private static bool AnyEnclosureStarts(List<IExpressionNode> nodes, int i, 
        out (EnclosureType Type, Func<List<IExpressionNode>, int, bool> Start, Func<List<IExpressionNode>, int, bool> End, int Length, string EndLiteral) enclosureData)
    {
        enclosureData = default!;
        foreach(var condition in enclosureConditionList)
        {
            if(condition.Start(nodes, i))
            {
                enclosureData = condition;
                return true;
            }
        }
        return false;
    }

    #endregion

    #region Node Creation
    /// <summary>
    /// Converts the entire expression that follows from the reader's index to a series of expression nodes used for parsing.
    /// </summary>
    /// <param name="baseToken">The current token that the parser is looking at.</param>
    /// <returns>A list of expression nodes for parsing</returns>
    private static List<IExpressionNode> ConvertTokensToExpressionNodes(ref Token baseToken)
    {
        List<IExpressionNode> nodes = new();
        Dictionary<OperatorTypes, int> contextualOccurrences = new();

        int parenCount = 0;

        // Perform conversions on tokens until ; or { reached.
        Token currentToken = baseToken;
        while(
            IsExpressionToken(currentToken, contextualOccurrences) &&
            (!currentToken.Is(TokenType.Punctuation, PunctuationTypes.CloseParen) || parenCount > 0)
            )
        {
            baseToken = currentToken.NextConcrete();
            ExpressionNodeType type = currentToken.Type switch
            {
                TokenType.Operator => ExpressionNodeType.UnresolvedOperator,
                TokenType.Punctuation => ExpressionNodeType.UnresolvedPunctuation,
                TokenType.Number => ExpressionNodeType.Literal,
                TokenType.ScriptString => ExpressionNodeType.Literal,
                TokenType.Keyword => ExpressionNodeType.Literal,
                TokenType.Name => ExpressionNodeType.Field,
                _ => ExpressionNodeType.Unknown
            };

            if(currentToken.Is(TokenType.Keyword, KeywordTypes.Thread) ||
                currentToken.Is(TokenType.Keyword, KeywordTypes.New))
            {
                type = ExpressionNodeType.UnresolvedOperator;
            }

            nodes.Add(new TokenNode(type, currentToken));

            if(currentToken.Is(TokenType.Punctuation, PunctuationTypes.OpenParen))
            {
                parenCount++;
            }
            else if(currentToken.Is(TokenType.Punctuation, PunctuationTypes.CloseParen))
            {
                parenCount--;
            }

            if(baseToken.IsEof())
            {
                return nodes;
            }

            currentToken = baseToken;
        }
        return nodes;
    }

    /// <summary>
    /// Asserts the requirement for there to exist an operator type already in the sequence before this operator can be treated as one.
    /// </summary>
    private static readonly Dictionary<OperatorTypes, OperatorTypes> contextualOperators = new()
    {
        {
            OperatorTypes.Colon, OperatorTypes.TernaryStart
        }
    };

    /// <summary>
    /// Asserts that the convert expression tokens method should save occurrences of these operators. This reduces time complexity of
    /// storing data that we don't need to know about for any contextual usage.
    /// </summary>
    private static readonly HashSet<OperatorTypes> saveContextualDataOperators = new()
    {
        OperatorTypes.TernaryStart
    };

    private static bool IsExpressionToken(Token token, Dictionary<OperatorTypes, int> contextualOccurrences)
    {
        // Name, Number, ScriptString, Operator: any
        // Keywords: false, true, thread, undefined
        // Punctuation: parentheses, brackets
        // TODO: yikes

        // Contextual operators: Ternary Else (can only occur if Ternary Then has been encountered)
        if(token.SubType is OperatorTypes operatorType)
        {
            if(contextualOperators.TryGetValue(operatorType, out OperatorTypes value))
            {
                if (!contextualOccurrences.ContainsKey(value)
                    || contextualOccurrences[value] <= 0)
                {
                    return false;
                }

                contextualOccurrences[value]--;
                return true;
            }

            if (saveContextualDataOperators.Contains(operatorType))
            {
                if(!contextualOccurrences.ContainsKey(operatorType))
                {
                    contextualOccurrences[operatorType] = 1;
                    return true;
                }
                contextualOccurrences[operatorType]++;
                return true;
            }
        }

        return IsOperatorOrOperand(token);
    }

    public static bool IsOperatorOrOperand(Token token)
    {
        return token.Is(TokenType.Name) ||
            token.Is(TokenType.Number) ||
            token.Is(TokenType.Operator) ||
            token.Is(TokenType.ScriptString) ||
            // Keyword operands
            token.Is(TokenType.Keyword, KeywordTypes.True) ||
            token.Is(TokenType.Keyword, KeywordTypes.False) ||
            token.Is(TokenType.Keyword, KeywordTypes.Thread) ||
            token.Is(TokenType.Keyword, KeywordTypes.Undefined) ||
            token.Is(TokenType.Keyword, KeywordTypes.AnimTree) ||
            token.Is(TokenType.Keyword, KeywordTypes.Anim) ||
            token.Is(TokenType.Keyword, KeywordTypes.New) ||
            token.Is(TokenType.Keyword, KeywordTypes.Vararg) ||
            // Punctuation
            token.Is(TokenType.Punctuation, PunctuationTypes.OpenParen) ||
            token.Is(TokenType.Punctuation, PunctuationTypes.CloseParen) ||
            token.Is(TokenType.Punctuation, PunctuationTypes.OpenBracket) ||
            token.Is(TokenType.Punctuation, PunctuationTypes.CloseBracket);
    }

    #endregion
}
