using System.Collections.Immutable;
using GSCode.Core.Diagnostics;
using GSCode.Parser;
using GSCode.Parser.Lexing;
using GSCode.Parser.Syntax;
using GSCode.Parser.Syntax.Ast;
using GSCode.Workspace.Api;

namespace GSCode.Workspace.Analysis;

/// <summary>
/// Reports writes to things that cannot be written: the implicit <c>.size</c> member, and
/// engine object fields the field data marks read-only.
///
/// The two carry different confidence, so they carry different severities. <c>.size</c> being
/// read-only is a language-spec fact, so that is an error. A field's read-only flag comes from
/// the curated mod-tools data, which can contain mistakes, so that is a warning. The owner's
/// entity kind isn't inferred here, so a field is only flagged when every kind declaring that
/// name agrees it is read-only.
/// </summary>
public static class ReadOnlyWriteLint
{
    public static ImmutableArray<Diagnostic> Analyze(ParseResult result, ObjectFields objectFields)
    {
        ImmutableArray<Diagnostic>.Builder diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        Inspect(result.Tree.Root, objectFields, diagnostics);

        return diagnostics.ToImmutable();
    }

    private static void Inspect(AstNode node, ObjectFields objectFields, ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        MemberNode? written = WrittenMember(node);
        if ( written is not null )
        {
            InspectWrite(written, objectFields, diagnostics);
        }

        foreach ( AstNode child in AstSearch.ChildrenOf(node) )
        {
            Inspect(child, objectFields, diagnostics);
        }
    }

    /// <summary>The member being written by this node, or null when the node isn't a write.</summary>
    private static MemberNode? WrittenMember(AstNode node)
    {
        // Every assignment operator counts, including the compound forms.
        if ( node is AssignmentNode assignment )
        {
            return assignment.Target as MemberNode;
        }

        // ++ and -- read and write in one step.
        if ( node is PostfixNode postfix && IsIncrementOrDecrement(postfix.Operator) )
        {
            return postfix.Operand as MemberNode;
        }

        if ( node is PrefixNode prefix && IsIncrementOrDecrement(prefix.Operator) )
        {
            return prefix.Operand as MemberNode;
        }

        return null;
    }

    private static bool IsIncrementOrDecrement(TokenKind kind)
    {
        return kind == TokenKind.PlusPlus || kind == TokenKind.MinusMinus;
    }

    private static void InspectWrite(
        MemberNode member,
        ObjectFields objectFields,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        string name = member.NameToken.Text;

        if ( string.Equals(name, "size", StringComparison.OrdinalIgnoreCase) )
        {
            diagnostics.Add(Diagnostic.Create(
                member.NameToken.RootRange, DiagnosticSeverity.Error, GscDiagnosticCode.SizeIsReadOnly));
            return;
        }

        ImmutableArray<ObjectField> declarations = objectFields.FindField(name);
        if ( declarations.Length == 0 || !AllReadOnly(declarations) )
        {
            return;
        }

        diagnostics.Add(Diagnostic.Create(
            member.NameToken.RootRange, DiagnosticSeverity.Warning, GscDiagnosticCode.ReadOnlyFieldWrite, name));
    }

    private static bool AllReadOnly(ImmutableArray<ObjectField> declarations)
    {
        foreach ( ObjectField declaration in declarations )
        {
            if ( !declaration.ReadOnly )
            {
                return false;
            }
        }

        return true;
    }
}
