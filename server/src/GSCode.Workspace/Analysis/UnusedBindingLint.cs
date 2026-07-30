using System.Collections.Immutable;
using GSCode.Core.Diagnostics;
using GSCode.Core.Text;
using GSCode.Parser;
using GSCode.Parser.Lexing;
using GSCode.Parser.Preprocessing;
using GSCode.Parser.Syntax;
using GSCode.Parser.Syntax.Ast;

namespace GSCode.Workspace.Analysis;

/// <summary>
/// Fades a name the author BOUND and the function never mentions again: a parameter, or a
/// <c>waittill</c> output.
///
/// A Hint with the Unnecessary tag, and the severity is the entire point. VS Code's Problems panel
/// shows Errors, Warnings and Information; a Hint never reaches it. All this produces is the faded
/// name in the editor — the reader sees "nothing here uses this" at a glance and is told nothing,
/// asked nothing, and given no count to clear.
///
/// That distinction is what makes it shippable. At any panel-visible severity it would be unusable:
/// 3,996 findings across 463 of BO3's 980 scripts, half the codebase demanding attention it does
/// not deserve.
///
/// The two kinds differ in how ACTIONABLE they are, which is why neither is reported as a problem:
///
/// * A <b>parameter</b> often cannot be removed. GSC passes positionally, and the reason BO3 has so
///   many unused ones is callbacks — a signature fixed by the engine or a dispatcher, where the last
///   parameter is as stuck as the middle one. (A trailing-only restriction was tried on the theory
///   that those were the removable ones; it barely moved the number, which is how that theory died.)
/// * A <b>waittill output</b> is the author's own choice, so a dead one genuinely can go:
///   <c>self waittill( "damage", attacker );</c> becomes <c>self waittill( "damage" );</c> when
///   nothing reads <c>attacker</c>.
///
/// Fading is honest about the only thing actually known in both cases: this name is not used here.
/// </summary>
public static class UnusedBindingLint
{
    public static ImmutableArray<Diagnostic> Analyze(ParseResult result)
    {
        ImmutableArray<Diagnostic>.Builder diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();

        foreach ( AstNode element in result.Tree.Root.Elements )
        {
            Walk(element, diagnostics);
        }

        return diagnostics.ToImmutable();
    }

    private static void Walk(AstNode element, ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        switch ( element )
        {
            case FunctionNode function:
                Inspect(function.Parameters, function.Body, function.HasVarargs, diagnostics);
                return;

            case ClassNode classNode:
                foreach ( AstNode member in classNode.Members )
                {
                    Walk(member, diagnostics);
                }

                return;

            case ConstructorNode constructor:
                Inspect(constructor.Parameters, constructor.Body, hasVarargs: false, diagnostics);
                return;

            case DestructorNode destructor:
                Inspect(destructor.Parameters, destructor.Body, hasVarargs: false, diagnostics);
                return;

            case DevBlockDeclNode devBlock:
                foreach ( AstNode declaration in devBlock.Declarations )
                {
                    Walk(declaration, diagnostics);
                }

                return;
        }
    }

    private static void Inspect(
        ImmutableArray<ParameterNode> parameters,
        AstNode body,
        bool hasVarargs,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        HashSet<string> mentioned = new(StringComparer.OrdinalIgnoreCase);
        List<PToken> waittillBindings = [];
        Collect(body, mentioned, waittillBindings);

        // A varargs function reaches its arguments through the vararg mechanism as well as by name,
        // so an unmentioned PARAMETER says nothing there. Its waittill outputs are unaffected.
        if ( !hasVarargs )
        {
            foreach ( ParameterNode parameter in parameters )
            {
                Report(parameter.NameToken, "Parameter", mentioned, diagnostics);
            }
        }

        foreach ( PToken binding in waittillBindings )
        {
            Report(binding, "waittill output", mentioned, diagnostics);
        }
    }

    private static void Report(
        PToken name,
        string noun,
        HashSet<string> mentioned,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if ( mentioned.Contains(name.Text) )
        {
            return;
        }

        // A macro-supplied name is not the author's, and the range would point into an expansion
        // rather than at anything they wrote — fading a spot they cannot edit.
        if ( name.Provenance.DefinitionSite is not null )
        {
            return;
        }

        Diagnostic unused = Diagnostic.Create(
            name.RootRange, DiagnosticSeverity.Hint, GscDiagnosticCode.UnusedBinding, noun, name.Text);

        diagnostics.Add(unused with { Tags = [DiagnosticTag.Unnecessary] });
    }

    /// <summary>
    /// Every name the body MENTIONS, plus the <c>waittill</c> outputs it BINDS.
    ///
    /// A binding is not a mention of itself, which is the whole reason the two are separated in one
    /// walk: counting `attacker` in <c>waittill( "damage", attacker )</c> as a use would mean no
    /// waittill output was ever unused.
    ///
    /// Mentions are not split into reads and writes. `function f( out ) { out = 1; }` assigns to its
    /// parameter, which does something in GSC when the argument is by-reference; fading that would
    /// be wrong, and telling the two apart needs by-reference knowledge this rule does not have.
    /// </summary>
    private static void Collect(AstNode node, HashSet<string> mentioned, List<PToken> bindings)
    {
        switch ( node )
        {
            case IdentifierNode identifier:
                mentioned.Add(identifier.Token.Text);
                return;

            case CallNode call when IsWaittill(call.Callee):
                // The first argument is the event NAME and is a genuine value; everything after it
                // is an output the engine fills in.
                for ( int index = 0; index < call.Arguments.Length; index++ )
                {
                    if ( index > 0 && call.Arguments[index] is IdentifierNode bound )
                    {
                        bindings.Add(bound.Token);
                        continue;
                    }

                    Collect(call.Arguments[index], mentioned, bindings);
                }

                if ( call.Target is not null )
                {
                    Collect(call.Target, mentioned, bindings);
                }

                return;

            default:
                foreach ( AstNode child in AstSearch.ChildrenOf(node) )
                {
                    Collect(child, mentioned, bindings);
                }

                return;
        }
    }

    /// <summary>
    /// Whether a callee is the <c>waittill</c> family. A callable keyword parses as an
    /// <see cref="IdentifierNode"/> wrapping the keyword token, so the TOKEN KIND is what
    /// distinguishes it from a call to a function that happens to share the name.
    /// </summary>
    private static bool IsWaittill(ExprNode callee)
    {
        return callee is IdentifierNode identifier
            && identifier.Token.Kind is TokenKind.WaitTill or TokenKind.WaitTillMatch;
    }
}
