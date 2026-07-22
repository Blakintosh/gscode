using System.Collections.Immutable;
using GSCode.Core.Diagnostics;
using GSCode.Core.Symbols;
using GSCode.Parser;
using GSCode.Workspace.Api;
using GSCode.Workspace.Typing;

namespace GSCode.Workspace.Analysis;

/// <summary>
/// Reports writes to things that cannot be written: the implicit <c>.size</c> member, and engine
/// object fields the field data marks read-only.
///
/// The governing rule is **report only when the owner's type makes the field read-only**, never
/// on the field name alone. Names collide across worlds — `name` is read-only on the engine's
/// player and weapon, but is an ordinary field on a struct you made — so
/// <c>state_machine = SpawnStruct(); state_machine.name = name;</c> is perfectly legal and was
/// previously flagged. An owner the flow typer cannot type yields Unknown and is left alone;
/// silence beats a false error on correct code.
///
/// Owner types come from <see cref="FlowTyper"/>'s own walk rather than a second inference pass,
/// so this can never disagree with the types shown in hovers and inlay hints.
///
/// The two rules carry different severities because they carry different confidence. `.size`
/// being read-only is a language-spec fact, so that is an error. A field's read-only flag comes
/// from curated mod-tools data, which can contain mistakes, so that is a warning.
/// </summary>
public static class ReadOnlyWriteLint
{
    public static ImmutableArray<Diagnostic> Analyze(ParseResult result, ObjectFields objectFields, FlowTyper typer)
    {
        typer.InferAssignments(result, out ImmutableArray<FieldWrite> writes);
        if ( writes.IsEmpty )
        {
            return [];
        }

        ImmutableArray<Diagnostic>.Builder diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();

        foreach ( FieldWrite write in writes )
        {
            if ( string.Equals(write.FieldName, "size", StringComparison.OrdinalIgnoreCase) )
            {
                InspectSizeWrite(write, diagnostics);
                continue;
            }

            InspectFieldWrite(write, objectFields, diagnostics);
        }

        return diagnostics.ToImmutable();
    }

    /// <summary>
    /// `.size` is the implicit read-only member of arrays and strings. On anything else — a
    /// struct or an entity you populated yourself — `size` is just a field name, so only the two
    /// types that actually own the implicit member are reported.
    /// </summary>
    private static void InspectSizeWrite(FieldWrite write, ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if ( write.OwnerType is not (ScrType.Array or ScrType.String) )
        {
            return;
        }

        diagnostics.Add(Diagnostic.Create(
            write.NameRange, DiagnosticSeverity.Error, GscDiagnosticCode.SizeIsReadOnly));
    }

    /// <summary>
    /// Engine object fields belong to entities, so only an owner known to be one is reported.
    /// The field must also be read-only on EVERY entity kind that declares it: the owner's exact
    /// kind is not inferred, so disagreement between kinds means we cannot be sure.
    ///
    /// DORMANT as things stand: no field in the bundled data carries a read-only flag, so this
    /// never fires. The 362 flags it used to consult were applied by hand during the manual
    /// import and turned out to have no source — `ScriptObjectFields.xlsx` has no such column —
    /// and they produced 87 warnings on shipped code. Rather than keep guesses that told users
    /// their working code was broken, the data was emptied and the code kept: the rule is right
    /// IF the flags are, so it costs nothing to leave the path in place for data that can be
    /// sourced. See FOLLOWUPS.md.
    /// </summary>
    private static void InspectFieldWrite(
        FieldWrite write,
        ObjectFields objectFields,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if ( write.OwnerType != ScrType.Entity )
        {
            return;
        }

        ImmutableArray<ObjectField> declarations = objectFields.FindField(write.FieldName);
        if ( declarations.Length == 0 || !AllReadOnly(declarations) )
        {
            return;
        }

        diagnostics.Add(Diagnostic.Create(
            write.NameRange, DiagnosticSeverity.Warning, GscDiagnosticCode.ReadOnlyFieldWrite, write.FieldName));
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
