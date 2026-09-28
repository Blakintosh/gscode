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
/// <c>state_machine = SpawnStruct(); state_machine.name = name;</c> is perfectly legal. An owner
/// the flow typer cannot type yields Unknown and is left alone;
/// silence beats a false error on correct code.
///
/// Owner types come from <see cref="FlowTyper"/>'s own walk rather than a second inference pass,
/// so this can never disagree with the types shown in hovers and inlay hints. The walk is asked
/// for through <c>InferValues</c>, which memoises it per parse, so the three rules reading the
/// typer share one pass over the file instead of taking one each.
///
/// The two rules carry different severities because they carry different confidence. `.size`
/// being read-only is a language-spec fact, so that is an error. A field's read-only flag comes
/// from curated mod-tools data, which can contain mistakes, so that is a warning.
/// </summary>
public static class ReadOnlyWriteLint
{
    public static ImmutableArray<Diagnostic> Analyze(ParseResult result, ObjectFields objectFields, FlowTyper typer)
    {
        ImmutableArray<FieldWrite> writes = typer.InferValues(result).FieldWrites;
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
        if ( !write.OwnerType.MustBe(ScrTypeSet.Array | ScrTypeSet.String) )
        {
            return;
        }

        diagnostics.Add(Diagnostic.Create(
            write.NameRange, DiagnosticSeverity.Error, GscDiagnosticCode.SizeIsReadOnly));
    }

    /// <summary>
    /// Engine object fields belong to entities, so only an owner that MAY be one is reported — an
    /// owner CONFIRMED to be something else (a struct from `SpawnStruct()`, an array literal) is
    /// excluded, and so is an owner the flow truly could not type at all (`IsUnknown`), which is
    /// the "silence beats a false error" case this rule exists to protect. `self` sits in between:
    /// it is never confirmed to be one specific thing (GSC allows threading onto an entity or a
    /// struct — see `ScrImprecision.CallerBoundObject`), but it is also never the
    /// TOTAL uncertainty an untyped parameter or an unresolved call result carries, so it still
    /// passes this gate. The field must also be read-only on EVERY entity kind that declares it:
    /// the owner's exact kind is not inferred, so disagreement between kinds means we cannot be sure.
    ///
    /// Weapon declarations are excluded outright. Weapon fields ARE documented read-only, but on
    /// the weapon value `GetWeapon()` returns — and a weapon is not an entity, so that fact says
    /// nothing about `self.meleedamage`. Since the lattice has no weapon type, an owner that is a
    /// weapon can never be typed anyway; letting weapon flags speak for entity owners would only
    /// ever produce false positives, and did — several of the original 87.
    ///
    /// DORMANT in practice: after the unsourced flags were removed, weapon is the only kind
    /// carrying any read-only flag, and weapon is excluded here — so this cannot currently fire.
    /// That is the intended state, not an oversight. The rule is kept because it is correct: it
    /// costs nothing, and flags that can be sourced for an entity kind will light it up with no
    /// code change.
    /// </summary>
    private static void InspectFieldWrite(
        FieldWrite write,
        ObjectFields objectFields,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        // MayBe rather than exact equality, so `self` (Entity|Struct — see
        // ScrImprecision.CallerBoundObject) still counts as a possible entity. IsUnknown excluded
        // separately: an owner the flow truly could not type (mystery_function()'s return, an
        // untyped parameter) is the full universe too, and MayBe(Entity) would be trivially true
        // for it as well — the lint's whole design is silence on that genuine uncertainty, so it
        // must stay excluded even though self, structurally, ends up MayBe-ing the same bit.
        if ( !write.OwnerType.MayBe(ScrTypeSet.Entity) || write.OwnerType.IsUnknown )
        {
            return;
        }

        ImmutableArray<ObjectField> declarations = objectFields.FindField(write.FieldName);
        if ( !AnyEntityKindDeclaresIt(declarations) || !EveryEntityKindMakesItReadOnly(declarations) )
        {
            return;
        }

        diagnostics.Add(Diagnostic.Create(
            write.NameRange, DiagnosticSeverity.Warning, GscDiagnosticCode.ReadOnlyFieldWrite, write.FieldName));
    }

    /// <summary>The owner is an entity, so a declaration on the weapon value type does not apply.</summary>
    private static bool AppliesToAnEntity(ObjectField declaration)
    {
        return !string.Equals(declaration.EntityKind, "weapon", StringComparison.OrdinalIgnoreCase);
    }

    private static bool AnyEntityKindDeclaresIt(ImmutableArray<ObjectField> declarations)
    {
        foreach ( ObjectField declaration in declarations )
        {
            if ( AppliesToAnEntity(declaration) )
            {
                return true;
            }
        }

        return false;
    }

    private static bool EveryEntityKindMakesItReadOnly(ImmutableArray<ObjectField> declarations)
    {
        foreach ( ObjectField declaration in declarations )
        {
            if ( AppliesToAnEntity(declaration) && !declaration.ReadOnly )
            {
                return false;
            }
        }

        return true;
    }
}
