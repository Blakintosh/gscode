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


internal class SwitchHelper
{
    [SetsRequiredMembers]
    public SwitchHelper(CfgNode continuation)
    {
        Continuation = continuation;
        UnmatchedNode = continuation;
    }

    /// <summary>
    /// The node that should be jumped to when control flow leaves the switch statement.
    /// </summary>
    public required CfgNode Continuation { get; init; }

    /// <summary>
    /// The node that should be jumped to when none of the case labels are matched.
    /// This is continuation, or the default body if default is present.
    /// </summary>
    public required CfgNode UnmatchedNode { get; set; }

    /// <summary>
    /// Whether this switch statement has a default label.
    /// </summary>
    public bool ContainsDefaultLabel { get; set; } = false;
}