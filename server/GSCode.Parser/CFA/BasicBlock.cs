using GSCode.Data;
using GSCode.Parser.AST;
using GSCode.Parser.Data;
using GSCode.Parser.Lexical;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using static System.Net.Mime.MediaTypeNames;

namespace GSCode.Parser.CFA;

internal enum CfgNodeType
{
    BasicBlock,
    DecisionNode,
    FunctionEntry,
    FunctionExit,
    EnumerationNode,
    IterationNode
}


internal abstract class CfgNode(CfgNodeType type, int scope)
{
    public LinkedList<CfgNode> Incoming { get; } = new();
    public LinkedList<CfgNode> Outgoing { get; } = new();

    public virtual void ConnectOutgoing(CfgNode other)
    {
        Outgoing.AddLast(other);
    }

    public virtual void ConnectIncoming(CfgNode other)
    {
        Incoming.AddLast(other);
    }

    public static void Connect(CfgNode from, CfgNode to)
    {
        from.ConnectOutgoing(to);
        to.ConnectIncoming(from);
    }

    public CfgNodeType Type { get; } = type;
    public int Scope { get; } = scope;
}


internal class BasicBlock(LinkedList<AstNode> statements, int scope) : CfgNode(CfgNodeType.BasicBlock, scope)
{
    public LinkedList<AstNode> Statements { get; } = statements;
}

internal class DecisionNode(AstNode source, ExprNode condition, int scope) : CfgNode(CfgNodeType.DecisionNode, scope)
{
    public AstNode Source { get; } = source;
    public ExprNode Condition { get; } = condition;
    public CfgNode? WhenTrue { get; set; }
    public CfgNode? WhenFalse { get; set; }
}

internal class IterationNode(AstNode source, AstNode initialisation, ExprNode condition, AstNode increment, int scope) : CfgNode(CfgNodeType.IterationNode, scope)
{
    public AstNode Source { get; } = source;
    public AstNode Initialisation { get; } = initialisation;
    public ExprNode Condition { get; } = condition;
    public AstNode Increment { get; } = increment;
    public CfgNode? Body { get; set; }
    public CfgNode? Continuation { get; set; }
}

internal class EnumerationNode(AstNode source, Token keyIdentifier, ExprNode collection, int scope) : CfgNode(CfgNodeType.EnumerationNode, scope)
{
    public AstNode Source { get; } = source;
    public Token KeyIdentifier { get; } = keyIdentifier;
    public ExprNode Collection { get; } = collection;
    public CfgNode? Body { get; set; }
    public CfgNode? Continuation { get; set; }
}


internal class FunEntryBlock(FunDefnNode source, Token? name) : CfgNode(CfgNodeType.FunctionEntry, 0)
{
    public FunDefnNode Source { get; } = source;
    public Token? Name { get; } = name;
    public CfgNode? Body { get; private set; }

    public override void ConnectOutgoing(CfgNode other)
    {
        base.ConnectOutgoing(other);
        Body = other;
    }
}

internal class FunExitBlock(AstNode source) : CfgNode(CfgNodeType.FunctionExit, 0)
{
    public AstNode Source { get; } = source;
}



