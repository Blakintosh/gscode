using GSCode.Data;
using GSCode.Data.Models;
using GSCode.Lexer.Types;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;

namespace GSCode.Parser.AST.Nodes;

internal abstract class ASTBranch
{
    internal ASTBranch(List<IASTNodeFactory> childrenFactories)
    {
        ChildrenFactories = childrenFactories;
    }

    /// <summary>
    /// List of children nodes stored in this branch.
    /// </summary>
    public List<ASTNode> Children { get; } = new();

    public int ChildrenCount => Children.Count;

    /// <summary>
    /// Whether this branch should push a new stack frame on the symbol table when entered.
    /// This defaults to true, which covers most cases, except for e.g. if() statements, which do not.
    /// </summary>
    public virtual bool PushScope { get; } = true;

    /// <summary>
    /// Opens a new branch in the AST.
    /// </summary>
    /// <param name="currentToken">The current node in the token linked list</param>
    /// <param name="data">Reference to the AST data class</param>
    /// <returns>true if should proceed with parsing this as a branch</returns>
    public abstract bool Open(ref Token currentToken, ASTHelper data);

    /// <summary>
    /// Checks if the branch should be closed at this position in the AST, and closes the branch if so.
    /// </summary>
    /// <param name="currentToken">The current node in the token linked list</param>
    /// <returns>true if closed</returns>
    public abstract bool Close(ref Token currentToken);

    /// <summary>
    /// Edge case handler to produce diagnostics if the end of the file is reached before this branch terminates.
    /// </summary>
    /// <param name="eofToken">The end-of-file node in the token linked list. It is not safe to assume that it has a previous node.</param>
    /// <param name="data">Reference to the AST data class</param>
    public abstract void EofReached(Token eofToken, ASTHelper data);

    /// <summary>
    /// Gets the last child node of this branch.
    /// </summary>
    /// <returns>An ASTNode child.</returns>
    public ASTNode GetLastChild() 
    {
        return GetChild(ChildrenCount - 1); 
    }

    /// <summary>
    /// List of factories to match through to get children nodes.
    /// </summary>
    public List<IASTNodeFactory> ChildrenFactories { get; }

    /// <summary>
    /// Adds a child node to this branch.
    /// </summary>
    public void AddChild(ASTNode child)
    {
        Children.Add(child);
    }

    /// <summary>
    /// Gets the child node at the specified index.
    /// </summary>
    /// <param name="index"></param>
    /// <returns>An ASTNode child.</returns>
    public ASTNode GetChild(int index)
    {
        if (index >= 0 && index < Children.Count)
        {
            return Children[index];
        }
        throw new ArgumentOutOfRangeException(nameof(index));
    }
}

internal sealed class BracedASTBranch : ASTBranch
{
    public BracedASTBranch(List<IASTNodeFactory> childrenFactories) : base(childrenFactories) {}

    public override bool Close(ref Token currentToken)
    {
        if(currentToken.Is(TokenType.Punctuation, PunctuationTypes.CloseBrace))
        {
            currentToken = currentToken.NextConcrete();
            return true;
        }
        return false;
    }

    public override void EofReached(Token eofToken, ASTHelper data)
    {
        Range lastTokenRange = eofToken.Previous?.TextRange ?? RangeHelper.From(0, 0, 0, 1);

        data.AddDiagnostic(lastTokenRange, GSCErrorCodes.MissingToken, "}");
    }

    public override bool Open(ref Token currentToken, ASTHelper data)
    {
        if (currentToken.Is(TokenType.Punctuation, PunctuationTypes.OpenBrace))
        {
            currentToken = currentToken.NextConcrete();
            return true;
        }
        data.AddDiagnostic(currentToken.TextRange, GSCErrorCodes.MissingToken, "{");
        return false;
    }
}

internal sealed class SingletonASTBranch : ASTBranch
{
    public SingletonASTBranch(List<IASTNodeFactory> childrenFactories) : base(childrenFactories) { }

    private bool _firstIteration = true;

    public override bool Close(ref Token currentToken)
    {
        if(_firstIteration)
        {
            _firstIteration = false;
            return false;
        }
        return true;
    }

    public override void EofReached(Token eofToken, ASTHelper data) {}

    public override bool Open(ref Token currentToken, ASTHelper data)
    {
        return true;
    }
}

internal sealed class RootASTBranch : ASTBranch
{
    public RootASTBranch() : base(NodeData.RootFactories) { }

    public override bool Close(ref Token currentToken)
    {
        return false;
    }

    public override void EofReached(Token eofToken, ASTHelper data) { }

    public override bool Open(ref Token currentToken, ASTHelper data)
    {
        return true;
    }
}

internal sealed class DevBlockASTBranch : ASTBranch
{
    public DevBlockASTBranch(List<IASTNodeFactory> childrenFactories) : base(childrenFactories) { }

    public override bool Close(ref Token currentToken)
    {
        if (currentToken.Is(TokenType.Punctuation, PunctuationTypes.CloseDevBlock))
        {
            currentToken = currentToken.NextConcrete();
            return true;
        }
        return false;
    }

    public override void EofReached(Token eofToken, ASTHelper data)
    {
        Range lastTokenRange = eofToken.Previous?.TextRange ?? RangeHelper.From(0, 0, 0, 1);

        data.AddDiagnostic(lastTokenRange, GSCErrorCodes.MissingToken, "#/");
    }

    public override bool Open(ref Token currentToken, ASTHelper data)
    {
        if (currentToken.Is(TokenType.Punctuation, PunctuationTypes.OpenDevBlock))
        {
            currentToken = currentToken.NextConcrete();
            return true;
        }
        data.AddDiagnostic(currentToken.TextRange, GSCErrorCodes.MissingToken, "/#");
        return false;
    }
}
