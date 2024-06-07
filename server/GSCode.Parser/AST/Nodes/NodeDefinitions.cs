using GSCode.Parser.AST.Expressions;
using GSCode.Parser.SPA.Logic.Analysers;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace GSCode.Parser.AST.Nodes
{
    internal enum NodeTypes
    {
        // Implemented
        File,
        WaittillFrameEndStatement,
        BreakStatement,
        ContinueStatement,
        IfStatement,
        ElseIfStatement,
        ElseStatement,
        FunctionDeclaration,
        WhileLoop,
        DoLoop,
        PrecacheDirective,
        NamespaceDirective,
        UsingAnimTreeDirective,
        ReturnStatement,
        WaitStatement,
        WaitRealTimeStatement,
        SwitchStatement,
        ExpressionStatement,
        ForeachLoop,
        ForLoop,
        UsingDirective,
        EmptyStatement,
        BraceStatement,
        ClassField,
        ClassConstructor,
        ClassDestructor,
        ClassDeclaration,
        CaseStatement,
        DefaultStatement,
        ConstDeclaration
    }

    internal sealed record ASTNode(NodeTypes Type, List<INodeComponent> Components, Token StartToken, Range TextRange, DataFlowNodeAnalyser? Analyser = null, SignatureNodeAnalyser? SignatureAnalyzer = null, ASTBranch? Branch = null);
}
