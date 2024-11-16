using GSCode.Data;
using GSCode.Parser.AST;
using GSCode.Parser.Data;
using Microsoft.VisualStudio.LanguageServer.Protocol;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using static System.Net.Mime.MediaTypeNames;

namespace GSCode.Parser.CFA;


internal enum ControlFlowType
{
    FunctionEntry, // Entry point of a function
    FunctionExit, // Exit point of a function
    Loop, // Loop statements
    If, // If, else-if statements
    Switch, // Switch statements
    Logic // Standard logic
}

internal class BasicBlock
{
    /// <summary>
    /// The incoming edges to this node.
    /// </summary>
    public List<BasicBlock> Incoming { get; } = new();
    /// <summary>
    /// The outgoing edges from this node.
    /// </summary>
    public List<BasicBlock> Outgoing { get; } = new();

    public AstNode? Decision { get; }

    public ControlFlowType Type { get; }

    public int Scope { get; }

    /// <summary>
    /// The AST nodes that form the logic of this branch.
    /// </summary>
    public ReadOnlyCollection<AstNode> Logic { get; } = ReadOnlyCollection<AstNode>.Empty;

    /// <summary>
    /// Whether this logic block has a jump instruction and should not connect to control flow.
    /// </summary>
    public bool Jumps { get; private set; } = false;

    public BasicBlock(int scope, ControlFlowType type = ControlFlowType.Logic)
    {
        Scope = scope;
        Type = type;
    }

    public BasicBlock(List<AstNode> logic, int scope, bool jumps = false) : this(scope, ControlFlowType.Logic)
    {
        Logic = logic.AsReadOnly();
        Jumps = jumps;
    }

    public BasicBlock(AstNode decisionNode, int scope, ControlFlowType type) : this(scope, type)
    {
        Decision = decisionNode;
    }

    public void ConnectTo(BasicBlock next)
    {
        Outgoing.Add(next);
        next.Incoming.Add(this);
    }

    public void ConnectNonJumpTo(BasicBlock next)
    {
        if (!Jumps)
        {
            ConnectTo(next);
        }
    }
}