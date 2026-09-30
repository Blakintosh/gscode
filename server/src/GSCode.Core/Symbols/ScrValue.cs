using System.Collections.Immutable;
using System.Globalization;
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
/// Coercion is a RELATION between types, not a property of their encoding.
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
            case ScrTypeSet.Int: return Integer.ToString(CultureInfo.InvariantCulture);
            case ScrTypeSet.Float: return Real.ToString("R", CultureInfo.InvariantCulture);
            case ScrTypeSet.Vector: return Vector.ToString();
            // Content, not Text: a literal's Text already carries its own quotes, and wrapping
            // those in another pair produced `""foo""` instead of `"foo"`.
            default: return Content is null ? "" : "\"" + Content + "\"";
        }
    }

    /// <summary>
    /// Structural equality by VALUE, not by the raw <see cref="Text"/> a string-kind constant
    /// happens to carry.
    ///
    /// Two string constants can mean the same thing while spelling it differently: a real literal
    /// token's <see cref="Text"/> keeps its quotes, while a folded concatenation's does not (see
    /// <see cref="Text"/>'s own doc — it is built from <see cref="Content"/> in
    /// <c>ScrOperators.Additive</c>, precisely to avoid producing quotes-within-quotes). The
    /// compiler-generated record equality compared <see cref="Text"/> directly, so
    /// <c>("a" + "b") == "ab"</c> folded to <c>false</c> and a Union that agreed on the same string
    /// from two differently-spelled sources dropped its constant. Comparing <see cref="Content"/>
    /// for the three string-ish kinds is the fix; every other kind still compares its own payload,
    /// where there is no such spelling ambiguity.
    /// </summary>
    public bool Equals(ScrConstant other)
    {
        if ( Type != other.Type )
        {
            return false;
        }

        switch ( Type )
        {
            case ScrTypeSet.Int: return Integer == other.Integer;
            case ScrTypeSet.Float: return Real.Equals(other.Real);
            case ScrTypeSet.Bool: return Boolean == other.Boolean;
            case ScrTypeSet.Vector: return Vector == other.Vector;
            case ScrTypeSet.String:
            case ScrTypeSet.IString:
            case ScrTypeSet.HashString:
                return string.Equals(Content, other.Content, StringComparison.Ordinal);
            // Undefined, or any future single-bit kind this switch does not yet know: Type
            // equality (checked above) is the whole answer, since no payload field is meaningful.
            default: return true;
        }
    }

    /// <summary>Agrees with <see cref="Equals(ScrConstant)"/> — hashes by value, not by raw Text.</summary>
    public override int GetHashCode()
    {
        switch ( Type )
        {
            case ScrTypeSet.Int: return HashCode.Combine(Type, Integer);
            case ScrTypeSet.Float: return HashCode.Combine(Type, Real);
            case ScrTypeSet.Bool: return HashCode.Combine(Type, Boolean);
            case ScrTypeSet.Vector: return HashCode.Combine(Type, Vector);
            case ScrTypeSet.String:
            case ScrTypeSet.IString:
            case ScrTypeSet.HashString:
                return HashCode.Combine(Type, Content is null ? 0 : StringComparer.Ordinal.GetHashCode(Content));
            default: return Type.GetHashCode();
        }
    }
}

/// <summary>Three doubles. A vector constant's payload.</summary>
public readonly record struct Vec3(double X, double Y, double Z)
{
    public override string ToString()
    {
        CultureInfo culture = CultureInfo.InvariantCulture;
        return "( " + X.ToString("R", culture) + ", " + Y.ToString("R", culture) + ", " + Z.ToString("R", culture) + " )";
    }
}

/// <summary>
/// The function a pointer value refers to.
///
/// <paramref name="Namespace"/> is null for a bare <c>&amp;helper</c> and set for
/// <c>&amp;ns::helper</c>, which is the same shape the symbol keys use — so a consumer resolving
/// one can ask the database directly rather than re-parsing a joined string.
/// </summary>
public readonly record struct ScrFunctionRef(string? Namespace, string Name);

/// <summary>
/// What is known about one value: which types it may hold, its constant value if it has one, and
/// its truthiness.
///
/// The union is the point. <see cref="ScrType"/> collapses any disagreement to <c>Unknown</c>, which
/// is right for a hover label and too coarse for a rule: two branches assigning an int and a string
/// produce <c>Int | String</c> here, which is still enough to say the value is never an array.
///
/// Precision is <see cref="MustBe"/> versus <see cref="MayBe"/> rather than a trust flag: a flag can
/// say a value is untrustworthy but never that it is one of exactly two things, and a rule that must
/// not guess acts on the first question and stays silent on the second.
/// </summary>
public readonly record struct ScrValue
{
    /// <summary>Nothing known. Every type is possible.</summary>
    public static ScrValue Unknown { get; } = new() { Types = ScrTypeSet.Universe };

    public ScrTypeSet Types { get; init; }

    /// <summary>The folded value, when <see cref="Types"/> is a single bit and the value is known.</summary>
    public ScrConstant? Constant { get; init; }

    /// <summary>
    /// Tri-state truthiness, kept separate from <see cref="Constant"/> on purpose: GSC's rules make
    /// every array, struct, vector and entity truthy, which is knowable without knowing the value.
    /// Never use it as a proxy for zero — that was v1.5's divide-by-zero bug.
    /// </summary>
    public bool? Truthiness { get; init; }

    /// <summary>The class of a BO3 <c>new Foo()</c>, when known.</summary>
    public string? InstanceClass { get; init; }

    /// <summary>
    /// The function a <c>&amp;name</c> pointer refers to, when known.
    ///
    /// Carried for the same reason as <see cref="InstanceClass"/>: which function a pointer holds is
    /// part of the value's identity, not merely its type. Without it every pointer is
    /// indistinguishably "a function", and a <c>[[ ptr ]]( ... )</c> call cannot be connected back
    /// to the declaration whose parameter names and documentation it should show.
    /// </summary>
    public ScrFunctionRef? FunctionTarget { get; init; }

    /// <summary>A value of exactly one type, with no constant.</summary>
    public static ScrValue Of(ScrTypeSet types)
    {
        return new ScrValue
        {
            Types = types,
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

    /// <summary>True when nothing has been established: the whole universe is still possible.</summary>
    public bool IsUnknown
    {
        get { return Types == ScrTypeSet.Universe; }
    }

    /// <summary>
    /// Every possible type is within <paramref name="expected"/> — the value IS one of these.
    /// The safe question, and the one a rule that must not guess acts on.
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
    /// <c>Float</c> is <c>Int | Float</c>, not <c>Float</c>: the join records what each branch
    /// produced, and widening is the projection's job, not the lattice's.
    ///
    /// A constant survives only if both sides carry the same one. Truthiness survives only if both
    /// agree.
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

        return new ScrValue
        {
            Types = types,
            Constant = left.Constant is { } a && right.Constant is { } b && a == b ? a : null,
            Truthiness = left.Truthiness == right.Truthiness ? left.Truthiness : null,
            InstanceClass = string.Equals(left.InstanceClass, right.InstanceClass, StringComparison.OrdinalIgnoreCase)
                ? left.InstanceClass
                : null,
            // Two branches assigning DIFFERENT pointers leave the target unknown, exactly as they do
            // for a class: a label naming one of the two would be wrong half the time it is shown.
            FunctionTarget = Nullable.Equals(left.FunctionTarget, right.FunctionTarget)
                ? left.FunctionTarget
                : null,
        };
    }

    /// <summary>
    /// Removes types from the set — the <c>isdefined</c>-style narrowing primitive.
    ///
    /// Every field that depended on the removed bits is recomputed or cleared, not carried over.
    /// Truthiness is asked again, since removing what made it uncertain can make it certain:
    /// <c>Struct|Undefined</c> narrowed to <c>Struct</c> is definitely true. InstanceClass and
    /// FunctionTarget carry a value's IDENTITY and are cleared once their own type bit
    /// (Instance/Function) is gone.
    /// </summary>
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
            Truthiness = TruthinessOf(remaining),
            InstanceClass = (remaining & ScrTypeSet.Instance) == ScrTypeSet.None ? null : InstanceClass,
            FunctionTarget = (remaining & ScrTypeSet.Function) == ScrTypeSet.None ? null : FunctionTarget,
        };
    }

    /// <summary>Keeps only these types — the positive narrowing primitive.</summary>
    public ScrValue Restrict(ScrTypeSet kept)
    {
        return Without(~kept & ScrTypeSet.Universe);
    }

    /// <summary>
    /// Projects onto the coarse <see cref="ScrType"/> the editor surfaces speak — hover, inlay hints
    /// and the two typing lints. A union has no single-value answer, so anything that is not exactly
    /// one type projects to <see cref="ScrType.Unknown"/>.
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

            // The one union the projection answers: an int/float disagreement widens to float, so a
            // genuine int/float branch join still hovers "float". The value underneath still says
            // Int|Float.
            case ScrTypeSet.Number: return ScrType.Float;

            default: return ScrType.Unknown;
        }
    }

    /// <summary>
    /// The name to SHOW for this value, which is not always the name of its projection.
    ///
    /// <see cref="ToScrType"/> is a lossy projection kept lossy on purpose — the coarse enum is what
    /// the typing lints compare against, and widening it would change what they judge. But two of
    /// the losses are visible to a reader and read as wrong: a <c>new Foo()</c> shown as
    /// <c>struct</c> throws away the class name the value already carries, and a <c>#"hash"</c>
    /// shown as <c>int</c> names the runtime shape rather than the thing written.
    ///
    /// So display goes through here and judgement goes through <see cref="ToScrType"/>. Everything
    /// else falls back to the projection, which keeps every other label byte-identical.
    /// </summary>
    public string DisplayName()
    {
        if ( Types == ScrTypeSet.Instance && !string.IsNullOrEmpty(InstanceClass) )
        {
            return InstanceClass;
        }

        if ( Types == ScrTypeSet.HashString )
        {
            return "hash";
        }

        return ToScrType().DisplayName();
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
    /// Structural equality, hand-written so a class name compares case-insensitively, as GSC's
    /// names do; the compiler-generated record equality would compare it ordinally.
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
            && string.Equals(InstanceClass, other.InstanceClass, StringComparison.OrdinalIgnoreCase)
            && Nullable.Equals(FunctionTarget, other.FunctionTarget);
    }

    public override int GetHashCode()
    {
        HashCode hash = new();
        hash.Add(Types);
        hash.Add(Constant);
        hash.Add(Truthiness);
        hash.Add(InstanceClass, StringComparer.OrdinalIgnoreCase);
        hash.Add(FunctionTarget);
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
}

/// <summary>Helpers over <see cref="ScrValue"/> and <see cref="ScrTypeSet"/>.</summary>
public static class ScrValues
{
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
