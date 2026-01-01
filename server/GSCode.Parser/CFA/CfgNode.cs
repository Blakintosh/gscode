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
    IterationNode,
    SwitchNode,
    SwitchCaseDecisionNode,
    ClassEntry,
    ClassExit,
    ClassMembersBlock,
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

    public static void Disconnect(CfgNode from, CfgNode to)
    {
        from.Outgoing.Remove(to);
        to.Incoming.Remove(from);
    }

    public CfgNodeType Type { get; } = type;
    public int Scope { get; } = scope;
}


internal class BasicBlock(LinkedList<AstNode> statements, int scope) : CfgNode(CfgNodeType.BasicBlock, scope)
{
    public LinkedList<AstNode> Statements { get; } = statements;
}

internal class DecisionNode(DecisionAstNode source, ExprNode condition, int scope) : CfgNode(CfgNodeType.DecisionNode, scope)
{
    public DecisionAstNode Source { get; } = source;
    public ExprNode Condition { get; } = condition;
    public CfgNode? WhenTrue { get; set; }
    public CfgNode? WhenFalse { get; set; }
}

internal class SwitchNode(SwitchStmtNode source, CfgNode continuation, int scope) : CfgNode(CfgNodeType.SwitchNode, scope)
{
    public SwitchStmtNode Source { get; } = source;
    public CfgNode? FirstLabel { get; set; }
    public CfgNode Continuation { get; set; } = continuation;
}


internal class SwitchCaseDecisionNode(CaseStmtNode caseStmt, SwitchNode parentSwitch, bool hasDefault, int scope) : CfgNode(CfgNodeType.SwitchCaseDecisionNode, scope)
{
    public CaseStmtNode CaseStmt { get; } = caseStmt;
    public LinkedList<CaseLabelNode> Labels { get; } = caseStmt.Labels;
    public SwitchNode ParentSwitch { get; } = parentSwitch;
    public bool HasDefault { get; } = hasDefault;
    public CfgNode WhenTrue { get; set; } = null!;
    public CfgNode WhenFalse { get; set; } = null!;
}

internal class IterationNode(ForStmtNode source, ExprNode? initialisation, ExprNode? condition, ExprNode? increment, int scope) : CfgNode(CfgNodeType.IterationNode, scope)
{
    public ForStmtNode Source { get; } = source;
    public ExprNode? Initialisation { get; } = initialisation;
    public ExprNode? Condition { get; } = condition;
    public ExprNode? Increment { get; } = increment;
    public CfgNode? Body { get; set; }
    public CfgNode? Continuation { get; set; }
}

internal class EnumerationNode(ForeachStmtNode source, int scope) : CfgNode(CfgNodeType.EnumerationNode, scope)
{
    public ForeachStmtNode Source { get; } = source;
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

internal class ClassEntryBlock(ClassDefnNode source, Token? name) : CfgNode(CfgNodeType.ClassEntry, 0)
{
    public ClassDefnNode Source { get; } = source;
    public Token? Name { get; } = name;
    public CfgNode? Body { get; private set; }
}

internal class ClassExitBlock(ClassDefnNode source) : CfgNode(CfgNodeType.ClassExit, 0)
{
    public ClassDefnNode Source { get; } = source;
}

internal class ClassMembersBlock(LinkedList<AstNode> statements, int scope) : CfgNode(CfgNodeType.ClassMembersBlock, scope)
{
    public LinkedList<AstNode> Statements { get; } = statements;
}