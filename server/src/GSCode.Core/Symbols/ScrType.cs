namespace GSCode.Core.Symbols;

/// <summary>
/// The coarse PROJECTION of a value's type, for the callers that want one name to show a user.
///
/// This is no longer the lattice, though it was and this summary said so for a while:
/// <see cref="ScrValue"/> is, with disjoint bits, constant values and entity kinds.
/// <c>FlowTyper</c> computes over that and projects to this enum at its public boundary, which is
/// why hover, inlay hints and the two typing lints were untouched when the lattice landed. Named in
/// plain text rather than a cref because it lives in <c>GSCode.Workspace</c>, which Core does not
/// reference and must not.
///
/// So the coarseness is deliberate but its REASON moved: a hint is shown only when the underlying
/// value names exactly one type. A union is not shown as a guess, it is shown as
/// <see cref="Unknown"/> — which never produces a hint or diagnostic, the zero-false-positive rule.
/// Anything that needs the union itself has to ask <see cref="ScrValue"/>, not this.
/// </summary>
public enum ScrType
{
    Unknown = 0,
    Undefined,
    Int,
    Float,
    Bool,
    String,
    IString,
    Vector,
    Struct,
    Array,
    Entity,
    Function,
}

/// <summary>Helpers for the type lattice.</summary>
public static class ScrTypes
{
    /// <summary>The lowercase display name used in inlay hints and hovers.</summary>
    public static string DisplayName(this ScrType type)
    {
        switch ( type )
        {
            case ScrType.Undefined: return "undefined";
            case ScrType.Int: return "int";
            case ScrType.Float: return "float";
            case ScrType.Bool: return "bool";
            case ScrType.String: return "string";
            case ScrType.IString: return "istring";
            case ScrType.Vector: return "vector";
            case ScrType.Struct: return "struct";
            case ScrType.Array: return "array";
            case ScrType.Entity: return "entity";
            case ScrType.Function: return "function";
            default: return "";
        }
    }

    /// <summary>True for a concrete, hint-worthy type (Unknown/Undefined are not shown).</summary>
    public static bool IsKnown(this ScrType type)
    {
        return type is not ScrType.Unknown and not ScrType.Undefined;
    }

    /// <summary>
    /// Merges two types at a control-flow join: equal types survive; int+float widen to
    /// float; anything else disagreeing collapses to Unknown (we never guess a union).
    /// </summary>
    public static ScrType Join(ScrType left, ScrType right)
    {
        if ( left == right )
        {
            return left;
        }

        if ( left == ScrType.Unknown || right == ScrType.Unknown )
        {
            return ScrType.Unknown;
        }

        bool numericPair = (left == ScrType.Int || left == ScrType.Float)
            && (right == ScrType.Int || right == ScrType.Float);
        if ( numericPair )
        {
            return ScrType.Float;
        }

        return ScrType.Unknown;
    }
}
