using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace GSCode.Parser.CFA;

internal readonly ref struct ControlFlowHelper
{
    /// <summary>
    /// The node that should be jumped to when a return statement is encountered.
    /// </summary>
    public required CfgNode ReturnContext { get; init; }

    /// <summary>
    /// The node that should be jumped to when a continue statement is encountered.
    /// </summary>
    public CfgNode? LoopContinueContext { get; init; } = null;

    /// <summary>
    /// The node that should be jumped to when a break statement is encountered.
    /// </summary>
    public CfgNode? BreakContext { get; init; } = null;

    /// <summary>
    /// The node that should be jumped to when the end of a basic block is reached.
    /// </summary>
    public required CfgNode ContinuationContext { get; init; }

    public required int Scope { get; init; }

    public ControlFlowHelper() 
    {
        Scope = 0;
    }

    [SetsRequiredMembers]
    public ControlFlowHelper(ControlFlowHelper parentScope) 
    {
        ReturnContext = parentScope.ReturnContext;
        LoopContinueContext = parentScope.LoopContinueContext;
        BreakContext = parentScope.BreakContext;
        ContinuationContext = parentScope.ContinuationContext;

        Scope = parentScope.Scope;
    }
}