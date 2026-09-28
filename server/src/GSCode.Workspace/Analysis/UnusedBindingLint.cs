using System.Collections.Immutable;
using GSCode.Core.Diagnostics;
using GSCode.Parser;
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
        // A waittill output is the thing being judged, so binding it is not a mention of it —
        // otherwise no output would ever be unused. Every other use is a mention, WRITES included:
        // `function f( out ) { out = 1; }` does something when the argument is by-reference, and
        // telling that apart needs by-reference knowledge this rule does not have.
        HashSet<string> mentioned = new(StringComparer.OrdinalIgnoreCase);
        List<PToken> waittillBindings = [];
        foreach ( LocalUse use in LocalUses.Of(body) )
        {
            if ( use.Kind == LocalUseKind.EventBinding )
            {
                waittillBindings.Add(use.Token);
            }
            else
            {
                mentioned.Add(use.Token.Text);
            }
        }

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
}
