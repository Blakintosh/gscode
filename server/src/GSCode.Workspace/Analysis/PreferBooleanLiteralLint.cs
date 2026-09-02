using System.Collections.Immutable;
using GSCode.Core.Diagnostics;
using GSCode.Parser;
using GSCode.Parser.Lexing;
using GSCode.Parser.Syntax.Ast;
using GSCode.Core.Symbols;
using GSCode.Workspace.Api;
using GSCode.Workspace.Typing;

namespace GSCode.Workspace.Analysis;

/// <summary>
/// Hints that a literal <c>0</c>/<c>1</c> passed to a builtin parameter declared <c>bool</c>
/// should be <c>false</c>/<c>true</c>. Scoped exactly to declared-bool parameters: an int
/// parameter legitimately takes 0 and 1, and flagging those was the v1 bug this rule's
/// original test was written to pin down.
///
/// Overloads must agree. If any overload declares something other than bool at that position,
/// the call is left alone, since which overload the author meant is unknowable here.
///
/// The same rule covers ENGINE FIELDS declared bool: `self.dogibbing = 1` should be `= true`. GSC
/// has no bool type of its own — 0 is false and anything else is true — so an int there is legal
/// and this stays a Hint. It is the field data that knows the field is a flag, which is why the
/// suggestion is worth making at all.
///
/// Field writes are scoped exactly as <see cref="ReadOnlyWriteLint"/> scopes its own: the owner
/// must be a known entity, weapon declarations do not speak for entity owners, and every entity
/// kind declaring the name must agree it is bool. A field name is not evidence on its own.
/// </summary>
public static class PreferBooleanLiteralLint
{
    private static void InspectFieldWrites(
        ParseResult result,
        ObjectFields objectFields,
        FlowTyper typer,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        ImmutableArray<FieldWrite> writes = typer.InferValues(result).FieldWrites;

        foreach ( FieldWrite write in writes )
        {
            // Value is null for `+=` and `++`, which have no single assigned value to judge.
            if ( write.Value is null || write.OwnerType != ScrType.Entity )
            {
                continue;
            }

            if ( !IsIntegerZeroOrOne(write.Value) )
            {
                continue;
            }

            if ( !EveryEntityKindDeclaresItBool(objectFields.FindField(write.FieldName)) )
            {
                continue;
            }

            diagnostics.Add(Diagnostic.Create(
                write.Value.Range,
                DiagnosticSeverity.Hint,
                GscDiagnosticCode.PreferBooleanLiteral,
                "Field",
                write.FieldName,
                IsOne(write.Value) ? "true" : "false"));
        }
    }

    /// <summary>
    /// Whether every entity kind that declares this field types it bool. Weapon declarations are
    /// skipped: a weapon is what GetWeapon() returns, not an entity, so its types say nothing
    /// about an entity owner -- the same scoping ReadOnlyWriteLint applies for the same reason.
    /// </summary>
    private static bool EveryEntityKindDeclaresItBool(ImmutableArray<ObjectField> declarations)
    {
        bool sawEntityKind = false;

        foreach ( ObjectField declaration in declarations )
        {
            if ( string.Equals(declaration.EntityKind, "weapon", StringComparison.OrdinalIgnoreCase) )
            {
                continue;
            }

            if ( !string.Equals(declaration.Type, "bool", StringComparison.OrdinalIgnoreCase) )
            {
                return false;
            }

            sawEntityKind = true;
        }

        return sawEntityKind;
    }

    /// <summary>
    /// This rule's whole judgement about ONE node, with no descent of its own, so
    /// <see cref="NodeLintPass"/> can run it from the shared walk. The field-write half is a
    /// separate pass over the flow typer's output — see <see cref="InspectRest"/>.
    /// </summary>
    internal static void InspectNode(AstNode node, BuiltinApi builtins, ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if ( node is CallNode call )
        {
            InspectCall(call, builtins, diagnostics);
        }
    }

    /// <summary>Everything this rule does that is not per-node: the field writes the typer found.</summary>
    internal static void InspectRest(
        ParseResult result,
        ObjectFields objectFields,
        FlowTyper typer,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        InspectFieldWrites(result, objectFields, typer, diagnostics);
    }

    private static void InspectCall(CallNode call, BuiltinApi builtins, ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        // Builtins have no namespace, so only a bare identifier callee can name one.
        if ( call.Callee is not IdentifierNode callee )
        {
            return;
        }

        BuiltinFunction? builtin = builtins.Find(callee.Token.Text);
        if ( builtin is null )
        {
            return;
        }

        for ( int index = 0; index < call.Arguments.Length; index++ )
        {
            if ( !IsIntegerZeroOrOne(call.Arguments[index]) )
            {
                continue;
            }

            if ( !AllOverloadsDeclareBool(builtin, index, out string parameterName) )
            {
                continue;
            }

            string replacement = IsOne(call.Arguments[index]) ? "true" : "false";
            diagnostics.Add(Diagnostic.Create(
                call.Arguments[index].Range,
                DiagnosticSeverity.Hint,
                GscDiagnosticCode.PreferBooleanLiteral,
                "Parameter",
                parameterName,
                replacement));
        }
    }

    /// <summary>
    /// True when every overload declares parameter <paramref name="index"/> as exactly bool.
    /// An exact text match, so int and number never qualify.
    /// </summary>
    private static bool AllOverloadsDeclareBool(BuiltinFunction builtin, int index, out string parameterName)
    {
        parameterName = "";

        if ( builtin.Overloads.Length == 0 )
        {
            return false;
        }

        foreach ( BuiltinOverload overload in builtin.Overloads )
        {
            if ( index >= overload.Parameters.Length )
            {
                return false;
            }

            BuiltinParameter parameter = overload.Parameters[index];
            if ( !string.Equals(parameter.TypeText, "bool", StringComparison.OrdinalIgnoreCase) )
            {
                return false;
            }

            parameterName = parameter.Name;
        }

        return true;
    }

    private static bool IsIntegerZeroOrOne(ExprNode argument)
    {
        if ( argument is not LiteralNode literal || literal.Token.Kind != TokenKind.Integer )
        {
            return false;
        }

        string text = literal.Token.Text;
        return string.Equals(text, "0", StringComparison.Ordinal) || string.Equals(text, "1", StringComparison.Ordinal);
    }

    private static bool IsOne(ExprNode argument)
    {
        return argument is LiteralNode literal && string.Equals(literal.Token.Text, "1", StringComparison.Ordinal);
    }
}
