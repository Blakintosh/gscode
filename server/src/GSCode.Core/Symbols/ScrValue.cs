using System.Collections.Immutable;
using System.Text;

namespace GSCode.Core.Symbols;

/// <summary>
/// The set of GSC types a value may hold, as disjoint flags.
///
/// Every member is ONE bit. That is the whole design decision, and it is a deliberate reversal of
/// v1.5's <c>ScrDataTypes</c>, which encoded coercions structurally — <c>Int = 1&lt;&lt;1 | Bool</c>,
/// <c>IString = (1&lt;&lt;4) | String</c>, <c>Number = Int | Float</c>. Overlapping bits bought one
/// implicit conversion and cost: a subset test that matched ints against bool, an <c>IsExactly</c>
/// method written purely to undo it, <c>IsNumeric()</c> answering true for booleans, an
/// <c>isint()</c> narrowing that kept bools, four hand-written suppression rules so type names
/// printed correctly, and a regression test class for the resulting false positives.
///
/// Coercion is a RELATION between types, not a property of their encoding. It lives in
/// <see cref="ScrValues.IsAssignableTo"/>, where it can be read.
///
/// <see cref="Number"/> and <see cref="AnyString"/> below are convenience aliases for writing rules.
/// They are ordinary unions of disjoint bits, never subset claims about their members.
/// </summary>
[Flags]
public enum ScrTypeSet : ulong
{
    /// <summary>No type at all. The empty set — a value that cannot exist, or nothing known yet.</summary>
    None = 0,

    /// <summary>
    /// A first-class member of the set, not a bottom and not an error. <c>Int | Undefined</c> is
    /// "assigned on one path and not the other", which is exactly what makes <c>isdefined</c>
    /// narrowing mean something.
    /// </summary>
    Undefined = 1UL << 0,

    Bool = 1UL << 1,
    Int = 1UL << 2,
    Float = 1UL << 3,
    String = 1UL << 4,

    /// <summary>A localized string, <c>&amp;"MENU_TITLE"</c>.</summary>
    IString = 1UL << 5,

    /// <summary>Treyarch's <c>#"canonicalized"</c>. BO1 and BO3 only; a hash, not a string.</summary>
    HashString = 1UL << 6,

    Vector = 1UL << 7,

    /// <summary>An untyped bag of fields — <c>spawnstruct()</c>. Passed by REFERENCE in every game.</summary>
    Struct = 1UL << 8,

    /// <summary>
    /// Passed by reference on Black Ops III and COPIED by value on every earlier game. The one kind
    /// in the language whose pass semantics fork by dialect, which is why telling it from
    /// <see cref="Struct"/> is the most load-bearing distinction here.
    /// </summary>
    Array = 1UL << 9,

    /// <summary>A player, or anything from <c>Spawn( … )</c>. Passed by reference in every game.</summary>
    Entity = 1UL << 10,

    Function = 1UL << 11,

    /// <summary>A BO3 class instance, <c>new Foo()</c>. Distinct from <see cref="Struct"/>, which has no class.</summary>
    Instance = 1UL << 12,

    /// <summary>Convenience alias. A union of two disjoint bits, not a supertype of either.</summary>
    Number = Int | Float,

    /// <summary>Convenience alias for the three string-ish kinds.</summary>
    AnyString = String | IString | HashString,

    /// <summary>
    /// Everything, written as an explicit OR of the real members.
    ///
    /// Deliberately not <c>~0</c>. v1.5 wrote <c>Any = ~0u &amp; ~Error</c> with
    /// <c>Error = 1 &lt;&lt; 60</c> on a <c>uint</c> enum — and C# masks a shift count to five bits,
    /// so <c>Error</c> was really <c>1 &lt;&lt; 28</c> and <c>Any</c> carried eleven unallocated junk
    /// bits that survived every mask and broke equality against it.
    /// </summary>
    Universe = Undefined | Bool | Int | Float | String | IString | HashString
        | Vector | Struct | Array | Entity | Function | Instance,

    /// <summary>
    /// The kinds passed by reference in EVERY dialect. <see cref="Array"/> is excluded precisely
    /// because it is not one of them before Black Ops III — it is the whole reason this alias exists.
    /// </summary>
    AlwaysByReference = Struct | Entity | Instance,
}

/// <summary>Why a value is not an exact, single, known type.</summary>
/// <remarks>
/// A linter needs none of this: it stays silent on anything uncertain and silence costs nothing. A
/// transpiler must still emit something for every expression, so "unknown" is only actionable when
/// it comes with a reason — a parameter nothing has told us about is a different problem from two
/// branches that genuinely disagree, and they have different fallbacks.
/// </remarks>
public enum ScrImprecision
{
    /// <summary>Exact. With a single-bit type set, this is the only state safe to rewrite blind.</summary>
    None = 0,

    /// <summary>A parameter, which nothing in this function types. Call-site inference is what lifts this.</summary>
    UntypedParameter,

    /// <summary>The value came from a script function, whose body this pass does not re-type.</summary>
    ScriptFunctionReturn,

    /// <summary>No entry for the called name in this game's builtin library.</summary>
    BuiltinNotInLibrary,

    /// <summary>The library declared a type this lattice cannot express — <c>any</c>, <c>number</c>.</summary>
    BuiltinTypeUnmapped,

    /// <summary>The library entry is present but marked low-confidence or unverified.</summary>
    BuiltinUnverified,

    /// <summary>An element read out of an array. Element types are not modelled.</summary>
    ArrayElement,

    /// <summary>A field on a struct. Scripts invent these freely, so the engine data says nothing.</summary>
    StructField,

    /// <summary>A field whose owner's entity kind was not inferred, so the declaring kind is unknown.</summary>
    UnknownFieldOwner,

    /// <summary>The token came from a macro expansion, so its position is not what the author wrote.</summary>
    MacroExpanded,

    /// <summary>
    /// A union produced by a control-flow join. The set is PRECISE — this is not a failure — but it
    /// records that no single path produced it, which a rewriter may want to surface.
    /// </summary>
    BranchDisagreement,

    /// <summary>An expression form this pass does not type.</summary>
    UnsupportedExpression,

    /// <summary>A global the selected dialect does not have, e.g. <c>world</c> before Black Ops III.</summary>
    DialectGlobalAbsent,
}

/// <summary>
/// A compile-time constant, carried when a value has exactly one type and that type's value is known.
/// </summary>
/// <remarks>
/// New in this tree. v1.5 is often described as having had constant tracking and did not: its
/// <c>ScrData</c> carried one value-level fact, <c>bool? BooleanValue</c>, and every arithmetic
/// operator returned a fresh valueless type. Its divide-by-zero check tested
/// <c>right.BooleanValue == false</c> — falsiness standing in for zero, which misses <c>2 - 2</c>
/// and fires on <c>""</c>.
/// </remarks>
public readonly record struct ScrConstant
{
    private ScrConstant(ScrTypeSet type, long integer, double real, bool boolean, string? text, Vec3 vector)
    {
        Type = type;
        Integer = integer;
        Real = real;
        Boolean = boolean;
        Text = text;
        Vector = vector;
    }

    /// <summary>Which of the payloads below is meaningful. Always a single bit.</summary>
    public ScrTypeSet Type { get; }

    public long Integer { get; }
    public double Real { get; }
    public bool Boolean { get; }

    /// <summary>
    /// A string constant's text, which for a LITERAL is the token exactly as written — quotes and
    /// any leading marker included.
    ///
    /// Stored raw because the lexer's token text is already interned, so keeping it costs nothing,
    /// while stripping the quotes on the way in cost one substring per string literal — 52,338 of
    /// them in Black Ops III's scripts alone, on the most common node kind there is, for a value
    /// almost nothing reads. Use <see cref="Content"/> where the characters themselves are wanted.
    /// </summary>
    public string? Text { get; }

    public Vec3 Vector { get; }

    /// <summary>
    /// The characters between the quotes, allocated on demand. Tolerant of an unquoted string, so a
    /// value produced by folding a concatenation reads back the same way a literal does.
    /// </summary>
    public string? Content
    {
        get
        {
            if ( Text is null )
            {
                return null;
            }

            int start = Text.IndexOf('"');
            int end = Text.LastIndexOf('"');

            return start >= 0 && end > start ? Text[(start + 1)..end] : Text;
        }
    }

    public static ScrConstant OfInt(long value)
    {
        return new ScrConstant(ScrTypeSet.Int, value, 0, false, null, default);
    }

    public static ScrConstant OfFloat(double value)
    {
        return new ScrConstant(ScrTypeSet.Float, 0, value, false, null, default);
    }

    public static ScrConstant OfBool(bool value)
    {
        return new ScrConstant(ScrTypeSet.Bool, 0, 0, value, null, default);
    }

    /// <summary>A string constant. <paramref name="type"/> distinguishes plain / localized / hashed.</summary>
    public static ScrConstant OfString(string value, ScrTypeSet type = ScrTypeSet.String)
    {
        return new ScrConstant(type, 0, 0, false, value, default);
    }

    public static ScrConstant OfVector(Vec3 value)
    {
        return new ScrConstant(ScrTypeSet.Vector, 0, 0, false, null, value);
    }

    public static ScrConstant OfUndefined()
    {
        return new ScrConstant(ScrTypeSet.Undefined, 0, 0, false, null, default);
    }

    /// <summary>The numeric value of an int or float constant, for arithmetic that widens.</summary>
    public double AsDouble()
    {
        return Type == ScrTypeSet.Int ? Integer : Real;
    }

    /// <summary>
    /// GSC truthiness: <c>0</c>, <c>0.0</c>, <c>""</c> and <c>undefined</c> are falsy; everything
    /// else — including every vector, array, struct and entity — is truthy.
    /// </summary>
    public bool IsTruthy()
    {
        switch ( Type )
        {
            case ScrTypeSet.Undefined: return false;
            case ScrTypeSet.Bool: return Boolean;
            case ScrTypeSet.Int: return Integer != 0;
            case ScrTypeSet.Float: return Real != 0;
            case ScrTypeSet.String:
            case ScrTypeSet.IString:
            case ScrTypeSet.HashString:
            {
                // Emptiness decided by the quote POSITIONS rather than by unquoting, because this
                // runs eagerly for every constant and an allocation here would undo the reason the
                // text is kept raw at all.
                if ( Text is null )
                {
                    return false;
                }

                int start = Text.IndexOf('"');
                int end = Text.LastIndexOf('"');

                return start >= 0 && end > start ? end > start + 1 : Text.Length > 0;
            }

            default: return true;
        }
    }

    public override string ToString()
    {
        switch ( Type )
        {
            case ScrTypeSet.Undefined: return "undefined";
            case ScrTypeSet.Bool: return Boolean ? "true" : "false";
            case ScrTypeSet.Int: return Integer.ToString(System.Globalization.CultureInfo.InvariantCulture);
            case ScrTypeSet.Float: return Real.ToString("R", System.Globalization.CultureInfo.InvariantCulture);
            case ScrTypeSet.Vector: return Vector.ToString();
            default: return Text is null ? "" : "\"" + Text + "\"";
        }
    }
}

/// <summary>Three doubles. A vector constant's payload.</summary>
public readonly record struct Vec3(double X, double Y, double Z)
{
    public override string ToString()
    {
        System.Globalization.CultureInfo culture = System.Globalization.CultureInfo.InvariantCulture;
        return "( " + X.ToString("R", culture) + ", " + Y.ToString("R", culture) + ", " + Z.ToString("R", culture) + " )";
    }
}

/// <summary>
/// What is known about one value: which types it may hold, its constant value if it has one, its
/// truthiness, its entity kinds, and why it is not exact.
///
/// The union is the point. <see cref="ScrType"/> collapses any disagreement to <c>Unknown</c>, which
/// is right for a lint that must not guess and wrong for a rewriter that must still emit something:
/// <c>int|string</c> is a usable fact and <c>Unknown</c> is not. Two branches assigning an int and a
/// string produce <c>Int | String</c> here, and <c>ScrTypes.Join</c> would have produced nothing.
///
/// Precision is expressed by <see cref="MustBe"/> versus <see cref="MayBe"/> rather than by a
/// trust flag. v1.5 carried one <c>Indeterminate</c> boolean meaning "do not rely on this", which
/// could say a value was untrustworthy but never that it was one of exactly two things, one of them
/// unsafe. That distinction is the whole question for array pass-semantics.
/// </summary>
public readonly record struct ScrValue
{
    /// <summary>Nothing known. Every type is possible.</summary>
    public static ScrValue Unknown { get; } = new()
    {
        Types = ScrTypeSet.Universe,
        Imprecision = ScrImprecision.UnsupportedExpression,
    };

    /// <summary>The empty set: a value that cannot exist. What a void call yields.</summary>
    public static ScrValue Nothing { get; } = new() { Types = ScrTypeSet.None };

    public ScrTypeSet Types { get; init; }

    /// <summary>The folded value, when <see cref="Types"/> is a single bit and the value is known.</summary>
    public ScrConstant? Constant { get; init; }

    /// <summary>
    /// Tri-state truthiness, kept separate from <see cref="Constant"/> on purpose: GSC's rules make
    /// every array, struct, vector and entity truthy, which is knowable without knowing the value.
    /// Never use it as a proxy for zero — that was v1.5's divide-by-zero bug.
    /// </summary>
    public bool? Truthiness { get; init; }

    /// <summary>
    /// Entity kinds this value may be — <c>player</c>, <c>actor</c>, <c>vehicle</c>, <c>weapon</c>.
    /// Sourced from the bundled API data's <c>instanceType</c>/<c>subType</c>, per game, rather than
    /// from a hardcoded table. Empty means the kind is unknown, not that there is none.
    /// </summary>
    public ImmutableArray<string> EntityKinds { get; init; }

    /// <summary>The class of a BO3 <c>new Foo()</c>, when known.</summary>
    public string? InstanceClass { get; init; }

    public ScrImprecision Imprecision { get; init; }

    /// <summary>A value of exactly one type, with no constant.</summary>
    public static ScrValue Of(ScrTypeSet types, ScrImprecision imprecision = ScrImprecision.None)
    {
        return new ScrValue
        {
            Types = types,
            Imprecision = imprecision,
            Truthiness = TruthinessOf(types),
        };
    }

    /// <summary>A value with a known constant. Its type comes from the constant.</summary>
    public static ScrValue OfConstant(ScrConstant constant)
    {
        return new ScrValue
        {
            Types = constant.Type,
            Constant = constant,
            Truthiness = constant.IsTruthy(),
        };
    }

    /// <summary>An entity, optionally narrowed to particular kinds.</summary>
    public static ScrValue OfEntity(ImmutableArray<string> kinds, ScrImprecision imprecision = ScrImprecision.None)
    {
        return new ScrValue
        {
            Types = ScrTypeSet.Entity,
            EntityKinds = kinds,
            Imprecision = imprecision,
            Truthiness = true,
        };
    }

    /// <summary>True when the set is a single bit — the only shape a rewriter can act on directly.</summary>
    public bool IsExact
    {
        get { return Types != ScrTypeSet.None && (Types & (Types - 1)) == 0; }
    }

    /// <summary>True when nothing has been established: the whole universe is still possible.</summary>
    public bool IsUnknown
    {
        get { return Types == ScrTypeSet.Universe; }
    }

    /// <summary>
    /// Every possible type is within <paramref name="expected"/> — the value IS one of these.
    /// The safe question: <c>MustBe( Array )</c> is what a rewriter acts on.
    /// </summary>
    public bool MustBe(ScrTypeSet expected)
    {
        return Types != ScrTypeSet.None && (Types & ~expected) == ScrTypeSet.None;
    }

    /// <summary>
    /// Some possible type is within <paramref name="expected"/> — the value MIGHT be one of these.
    /// True with <see cref="MustBe"/> false is the case that has to be escalated rather than guessed.
    /// </summary>
    public bool MayBe(ScrTypeSet expected)
    {
        return (Types & expected) != ScrTypeSet.None;
    }

    /// <summary>
    /// Set union, for a control-flow join. Never widens and never collapses: <c>Int</c> joined with
    /// <c>Float</c> is <c>Int | Float</c>, not <c>Float</c>, because <c>1</c> and <c>1.0</c> are
    /// different text to emit.
    ///
    /// A constant survives only if both sides carry the same one. Truthiness survives only if both
    /// agree. Entity kinds union. Imprecision takes the more severe side, and a disagreement in
    /// types is itself recorded as <see cref="ScrImprecision.BranchDisagreement"/>.
    /// </summary>
    public static ScrValue Union(ScrValue left, ScrValue right)
    {
        if ( left.Types == ScrTypeSet.None )
        {
            return right;
        }

        if ( right.Types == ScrTypeSet.None )
        {
            return left;
        }

        ScrTypeSet types = left.Types | right.Types;
        bool disagreed = left.Types != right.Types;

        return new ScrValue
        {
            Types = types,
            Constant = left.Constant is { } a && right.Constant is { } b && a == b ? a : null,
            Truthiness = left.Truthiness == right.Truthiness ? left.Truthiness : null,
            EntityKinds = UnionKinds(left.EntityKinds, right.EntityKinds),
            InstanceClass = string.Equals(left.InstanceClass, right.InstanceClass, StringComparison.OrdinalIgnoreCase)
                ? left.InstanceClass
                : null,
            Imprecision = disagreed && left.Imprecision == ScrImprecision.None && right.Imprecision == ScrImprecision.None
                ? ScrImprecision.BranchDisagreement
                : (ScrImprecision)Math.Max((int)left.Imprecision, (int)right.Imprecision),
        };
    }

    /// <summary>Removes types from the set — the <c>isdefined</c>-style narrowing primitive.</summary>
    public ScrValue Without(ScrTypeSet removed)
    {
        ScrTypeSet remaining = Types & ~removed;
        if ( remaining == Types )
        {
            return this;
        }

        return this with
        {
            Types = remaining,
            Constant = Constant is { } constant && (constant.Type & removed) != ScrTypeSet.None ? null : Constant,
            Truthiness = remaining == ScrTypeSet.None ? null : Truthiness,
        };
    }

    /// <summary>Keeps only these types — the positive narrowing primitive.</summary>
    public ScrValue Restrict(ScrTypeSet kept)
    {
        return Without(~kept & ScrTypeSet.Universe);
    }

    /// <summary>
    /// Projects onto the coarse <see cref="ScrType"/> the editor surfaces speak.
    ///
    /// This is what keeps every existing consumer — hover, inlay hints, and the two typing lints —
    /// working unchanged while the walk underneath carries far more. A union has no single-value
    /// answer, so anything that is not exactly one type projects to <see cref="ScrType.Unknown"/>,
    /// which is precisely the old behaviour.
    /// </summary>
    public ScrType ToScrType()
    {
        switch ( Types )
        {
            case ScrTypeSet.Undefined: return ScrType.Undefined;
            case ScrTypeSet.Bool: return ScrType.Bool;
            case ScrTypeSet.Int: return ScrType.Int;
            case ScrTypeSet.Float: return ScrType.Float;
            case ScrTypeSet.String: return ScrType.String;
            case ScrTypeSet.IString: return ScrType.IString;
            // The lattice separates a Treyarch #"hash" from a string; ScrType has no member for it,
            // and its int-like runtime shape is the closer of the two available answers.
            case ScrTypeSet.HashString: return ScrType.Int;
            case ScrTypeSet.Vector: return ScrType.Vector;
            case ScrTypeSet.Struct: return ScrType.Struct;
            case ScrTypeSet.Array: return ScrType.Array;
            case ScrTypeSet.Entity: return ScrType.Entity;
            case ScrTypeSet.Function: return ScrType.Function;
            // A class instance is a struct with a name, and ScrType cannot carry the name.
            case ScrTypeSet.Instance: return ScrType.Struct;

            // The one union the coarse lattice had an answer for: ScrTypes.Join widens an int/float
            // disagreement to float. Reproduced here rather than collapsing to Unknown, because this
            // is a PROJECTION — it has to give the editor exactly what the old lattice gave it, and
            // a hover that used to read "float" must not start reading nothing. The richer value
            // underneath still says Int|Float, which is what a rewriter needs.
            case ScrTypeSet.Number: return ScrType.Float;

            default: return ScrType.Unknown;
        }
    }

    /// <summary>Widens a coarse <see cref="ScrType"/> into a value, for the boundary going the other way.</summary>
    public static ScrValue FromScrType(ScrType type)
    {
        switch ( type )
        {
            case ScrType.Undefined: return Of(ScrTypeSet.Undefined);
            case ScrType.Int: return Of(ScrTypeSet.Int);
            case ScrType.Float: return Of(ScrTypeSet.Float);
            case ScrType.Bool: return Of(ScrTypeSet.Bool);
            case ScrType.String: return Of(ScrTypeSet.String);
            case ScrType.IString: return Of(ScrTypeSet.IString);
            case ScrType.Vector: return Of(ScrTypeSet.Vector);
            case ScrType.Struct: return Of(ScrTypeSet.Struct);
            case ScrType.Array: return Of(ScrTypeSet.Array);
            case ScrType.Entity: return Of(ScrTypeSet.Entity);
            case ScrType.Function: return Of(ScrTypeSet.Function);
            default: return Unknown;
        }
    }

    /// <summary>
    /// Structural equality, hand-written because <see cref="ImmutableArray{T}"/> compares by
    /// reference and the compiler-generated record equality would inherit that.
    ///
    /// This is not a nicety. v1.5's equivalent used <c>ImmutableHashSet</c> with default equality, so
    /// two structurally identical values compared unequal, and any dataflow worklist carrying one
    /// inside a cycle never converged. Anything that participates in a fixpoint needs real equality
    /// and a hash that agrees with it — and a direct test, which v1.5 lacked.
    /// </summary>
    public bool Equals(ScrValue other)
    {
        return Types == other.Types
            && Nullable.Equals(Constant, other.Constant)
            && Truthiness == other.Truthiness
            && Imprecision == other.Imprecision
            && string.Equals(InstanceClass, other.InstanceClass, StringComparison.OrdinalIgnoreCase)
            && KindsEqual(EntityKinds, other.EntityKinds);
    }

    public override int GetHashCode()
    {
        HashCode hash = new();
        hash.Add(Types);
        hash.Add(Constant);
        hash.Add(Truthiness);
        hash.Add(Imprecision);
        hash.Add(InstanceClass, StringComparer.OrdinalIgnoreCase);

        // Order-independent, matching KindsEqual: two values whose kinds arrived in a different
        // order are equal, so they must hash the same.
        int kinds = 0;
        if ( !EntityKinds.IsDefaultOrEmpty )
        {
            foreach ( string kind in EntityKinds )
            {
                kinds ^= StringComparer.OrdinalIgnoreCase.GetHashCode(kind);
            }
        }

        hash.Add(kinds);
        return hash.ToHashCode();
    }

    public override string ToString()
    {
        return ScrValues.Describe(this);
    }

    /// <summary>What is knowable about truthiness from the type set alone.</summary>
    private static bool? TruthinessOf(ScrTypeSet types)
    {
        if ( types == ScrTypeSet.None )
        {
            return null;
        }

        // Reference kinds and vectors are always truthy in GSC, whatever they hold.
        if ( (types & ~(ScrTypeSet.Vector | ScrTypeSet.Struct | ScrTypeSet.Array | ScrTypeSet.Entity | ScrTypeSet.Instance)) == ScrTypeSet.None )
        {
            return true;
        }

        if ( types == ScrTypeSet.Undefined )
        {
            return false;
        }

        return null;
    }

    private static ImmutableArray<string> UnionKinds(ImmutableArray<string> left, ImmutableArray<string> right)
    {
        if ( left.IsDefaultOrEmpty )
        {
            return right;
        }

        if ( right.IsDefaultOrEmpty )
        {
            return left;
        }

        ImmutableArray<string>.Builder builder = ImmutableArray.CreateBuilder<string>();
        builder.AddRange(left);
        foreach ( string kind in right )
        {
            if ( !builder.Contains(kind, StringComparer.OrdinalIgnoreCase) )
            {
                builder.Add(kind);
            }
        }

        return builder.ToImmutable();
    }

    private static bool KindsEqual(ImmutableArray<string> left, ImmutableArray<string> right)
    {
        if ( left.IsDefaultOrEmpty && right.IsDefaultOrEmpty )
        {
            return true;
        }

        if ( left.IsDefaultOrEmpty || right.IsDefaultOrEmpty || left.Length != right.Length )
        {
            return false;
        }

        foreach ( string kind in left )
        {
            if ( !right.Contains(kind, StringComparer.OrdinalIgnoreCase) )
            {
                return false;
            }
        }

        return true;
    }
}

/// <summary>Helpers over <see cref="ScrValue"/> and <see cref="ScrTypeSet"/>.</summary>
public static class ScrValues
{
    /// <summary>
    /// Whether a value of <paramref name="from"/> is acceptable where <paramref name="to"/> is
    /// expected.
    ///
    /// Written as a relation rather than encoded in the bits, which is the correction to v1.5. Note
    /// it is one-directional: an istring is usable as a string, and the reverse also holds because
    /// a localized lookup falls back to the literal — but int-to-string does NOT hold here even
    /// though GSC will coerce it, since a transpiler emitting the coercion needs to see it.
    /// </summary>
    public static bool IsAssignableTo(ScrTypeSet from, ScrTypeSet to)
    {
        if ( from == ScrTypeSet.None || to == ScrTypeSet.None )
        {
            return false;
        }

        ScrTypeSet widened = to;

        if ( (to & ScrTypeSet.AnyString) != ScrTypeSet.None )
        {
            widened |= ScrTypeSet.AnyString;
        }

        // A bool is 0 or 1 wherever a number is wanted.
        if ( (to & ScrTypeSet.Number) != ScrTypeSet.None )
        {
            widened |= ScrTypeSet.Bool;
        }

        // Every reference kind may be undefined before it is assigned.
        if ( (to & (ScrTypeSet.Struct | ScrTypeSet.Array | ScrTypeSet.Entity | ScrTypeSet.Instance)) != ScrTypeSet.None )
        {
            widened |= ScrTypeSet.Undefined;
        }

        return (from & ~widened) == ScrTypeSet.None;
    }

    /// <summary>
    /// Whether a value of this type is passed by reference under the given dialect.
    ///
    /// The whole dialect fork in one predicate: structs, entities and class instances alias in every
    /// game, and arrays alias only where <c>GameProfile.ArraysPassedByReference</c> holds.
    /// </summary>
    public static bool IsByReference(ScrTypeSet type, bool arraysByReference)
    {
        if ( type == ScrTypeSet.Array )
        {
            return arraysByReference;
        }

        return type is ScrTypeSet.Struct or ScrTypeSet.Entity or ScrTypeSet.Instance;
    }

    /// <summary>A readable rendering of a type set: <c>int</c>, <c>int|string</c>, <c>any</c>.</summary>
    public static string Describe(ScrTypeSet types)
    {
        if ( types == ScrTypeSet.None )
        {
            return "never";
        }

        if ( types == ScrTypeSet.Universe )
        {
            return "any";
        }

        StringBuilder builder = new();
        foreach ( ScrTypeSet member in Members )
        {
            if ( (types & member) == ScrTypeSet.None )
            {
                continue;
            }

            if ( builder.Length > 0 )
            {
                builder.Append('|');
            }

            builder.Append(NameOf(member));
        }

        return builder.ToString();
    }

    /// <summary>A readable rendering of a whole value, with its constant when it has one.</summary>
    public static string Describe(ScrValue value)
    {
        string types = Describe(value.Types);
        return value.Constant is { } constant ? types + " " + constant : types;
    }

    /// <summary>The single-bit members, in display order. The universe is their OR.</summary>
    public static readonly ImmutableArray<ScrTypeSet> Members =
    [
        ScrTypeSet.Undefined, ScrTypeSet.Bool, ScrTypeSet.Int, ScrTypeSet.Float,
        ScrTypeSet.String, ScrTypeSet.IString, ScrTypeSet.HashString, ScrTypeSet.Vector,
        ScrTypeSet.Struct, ScrTypeSet.Array, ScrTypeSet.Entity, ScrTypeSet.Function,
        ScrTypeSet.Instance,
    ];

    private static string NameOf(ScrTypeSet member)
    {
        switch ( member )
        {
            case ScrTypeSet.Undefined: return "undefined";
            case ScrTypeSet.Bool: return "bool";
            case ScrTypeSet.Int: return "int";
            case ScrTypeSet.Float: return "float";
            case ScrTypeSet.String: return "string";
            case ScrTypeSet.IString: return "istring";
            case ScrTypeSet.HashString: return "hash";
            case ScrTypeSet.Vector: return "vector";
            case ScrTypeSet.Struct: return "struct";
            case ScrTypeSet.Array: return "array";
            case ScrTypeSet.Entity: return "entity";
            case ScrTypeSet.Function: return "function";
            case ScrTypeSet.Instance: return "instance";
            default: return "?";
        }
    }
}
