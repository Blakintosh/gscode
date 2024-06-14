using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace GSCode.Parser.CFA;

internal readonly ref struct ControlFlowHelper
{
    public List<BasicBlock> ContinueBlocks { get; } = new();
    public List<BasicBlock> BreakBlocks { get; } = new();
    public List<BasicBlock> ReturnBlocks { get; } = new();
    public int Scope { get; } = 0;

    public ControlFlowHelper() { }

    public ControlFlowHelper(List<BasicBlock> returnBlocks, int scope)
    {
        ReturnBlocks = returnBlocks;
        Scope = scope;
    }

    public ControlFlowHelper(List<BasicBlock> returnBlocks, List<BasicBlock> continueBlocks, List<BasicBlock> breakBlocks, int scope) : this(returnBlocks, scope)
    {
        ContinueBlocks = continueBlocks;
        BreakBlocks = breakBlocks;
    }

    public ControlFlowHelper IncreaseScope()
    {
        return new(ReturnBlocks, ContinueBlocks, BreakBlocks, Scope + 1);
    }

    public ControlFlowHelper EnterLoopScope()
    {
        return new(ReturnBlocks, Scope + 1);
    }

    public void ConnectLoopEdges(BasicBlock condition, BasicBlock continuation)
    {
        foreach (BasicBlock block in ContinueBlocks)
        {
            block.ConnectTo(condition);
        }

        foreach (BasicBlock block in BreakBlocks)
        {
            block.ConnectTo(continuation);
        }
    }

    public void ConnectReturnEdges(BasicBlock exit)
    {
        foreach (BasicBlock block in ReturnBlocks)
        {
            block.ConnectTo(exit);
        }
    }
}