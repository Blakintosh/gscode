namespace GSCode.Core.Symbols;

/// <summary>
/// A binary operator, as SEMANTICS rather than as syntax.
///
/// Deliberately not <c>TokenKind</c>: that lives in GSCode.Parser, which Core cannot reference, and
/// the deeper reason is that this table answers "what does multiplication mean on these two types",
/// a question with no tokens in it. A caller maps its own token kind onto this; a transpiler asking
/// what an expression evaluates to needs no token stream at all.
/// </summary>
public enum ScrBinaryOp
{
    Add, Subtract, Multiply, Divide, Modulo,
    Equal, NotEqual, StrictEqual, StrictNotEqual,
    Less, LessOrEqual, Greater, GreaterOrEqual,
    And, Or,
    BitAnd, BitOr, BitXor, ShiftLeft, ShiftRight,
}

/// <summary>A prefix operator, as semantics.</summary>
public enum ScrUnaryOp
{
    Not,        // !
    Negate,     // -
    BitNot,     // ~
    AddressOf,  // &amp;
}

/// <summary>
/// The result of applying an operator: the value it produces, and whether the operand types made
/// sense at all.
/// </summary>
/// <param name="Value">What the expression evaluates to.</param>
/// <param name="Diagnosis">Whether the engine would accept these operands.</param>
public readonly record struct ScrOperatorResult(ScrValue Value, ScrOperandDiagnosis Diagnosis)
{
    public static ScrOperatorResult Ok(ScrValue value)
    {
        return new ScrOperatorResult(value, ScrOperandDiagnosis.Fine);
    }
}

/// <summary>What, if anything, is wrong with an operator's operands.</summary>
public enum ScrOperandDiagnosis
{
    /// <summary>The operands are acceptable, or not known well enough to object.</summary>
    Fine = 0,

    /// <summary>No reading of these operand types makes the operator meaningful.</summary>
    UnsupportedOperands,

    /// <summary>A division or modulo whose divisor is a known zero.</summary>
    DivisionByZero,
}

/// <summary>
/// Type and constant semantics for GSC's operators, as one table with one interpreter.
///
/// v1.5 spread this over 536 lines in which the four equality operators and two logicals were six
/// near-identical 36-line bodies and the five bitwise operators were five copies of a three-line
/// function — so its vector rules were buried inside a shared numeric helper and its holes were
/// invisible. This tree's version is smaller but wrong in a way that ships today: <c>NumericResult</c>
/// takes no operator and knows only Int/Float/Unknown, so <c>vector * 0.5</c> types as
/// <c>float</c>, which is one of the two causes that got <c>PredefinedFieldTypeMismatch</c>
/// withdrawn after it reported 46 findings on Black Ops III with none of them real.
///
/// Two structural rules, both learned from v1.5's failures:
///
/// 1. **Vector rules come before the string-concat fallback.** v1.5 checked string concatenation
///    early enough that <c>vector + float</c> came out a <c>string</c>.
/// 2. **Match with <see cref="ScrValue.MustBe"/> / <see cref="ScrValue.MayBe"/>, never exact set
///    equality.** Every v1.5 rule was written <c>left.Type == ScrDataTypes.Vector</c>, which stops
///    matching the moment a union flows in — and after any branch merge a union is the normal case.
/// </summary>
public static class ScrOperators
{
    /// <summary>Applies a binary operator to two operand values.</summary>
    public static ScrOperatorResult Apply(ScrBinaryOp op, ScrValue left, ScrValue right)
    {
        switch ( op )
        {
            case ScrBinaryOp.Add:
                return Additive(op, left, right);

            case ScrBinaryOp.Subtract:
                return Additive(op, left, right);

            case ScrBinaryOp.Multiply:
            case ScrBinaryOp.Divide:
                return Scaling(op, left, right);

            case ScrBinaryOp.Modulo:
                return Modulo(left, right);

            case ScrBinaryOp.Equal:
            case ScrBinaryOp.NotEqual:
            case ScrBinaryOp.StrictEqual:
            case ScrBinaryOp.StrictNotEqual:
            case ScrBinaryOp.Less:
            case ScrBinaryOp.LessOrEqual:
            case ScrBinaryOp.Greater:
            case ScrBinaryOp.GreaterOrEqual:
            case ScrBinaryOp.And:
            case ScrBinaryOp.Or:
                // Every comparison and logical yields a bool whatever it is handed. Folding them
                // needs care v1.5 did not take: its `==` folded `left.BooleanValue == right.BooleanValue`,
                // so `5 == 3` came out TRUE because both are truthy, and its `&&` folded the same
                // way, which is XNOR rather than AND. Three TODOs in that file admit it. Folded here
                // only from real constants, in Compare/Logical below.
                return Comparison(op, left, right);

            case ScrBinaryOp.BitAnd:
            case ScrBinaryOp.BitOr:
            case ScrBinaryOp.BitXor:
            case ScrBinaryOp.ShiftLeft:
            case ScrBinaryOp.ShiftRight:
                return Bitwise(op, left, right);

            default:
                return ScrOperatorResult.Ok(ScrValue.Unknown);
        }
    }

    /// <summary>Applies a prefix operator.</summary>
    public static ScrOperatorResult Apply(ScrUnaryOp op, ScrValue operand)
    {
        switch ( op )
        {
            case ScrUnaryOp.Not:
            {
                bool? truth = operand.Truthiness;
                return ScrOperatorResult.Ok(truth is bool known
                    ? ScrValue.OfConstant(ScrConstant.OfBool(!known))
                    : ScrValue.Of(ScrTypeSet.Bool));
            }

            case ScrUnaryOp.BitNot:
            {
                if ( operand.Constant is { Type: ScrTypeSet.Int } constant )
                {
                    return ScrOperatorResult.Ok(ScrValue.OfConstant(ScrConstant.OfInt(~constant.Integer)));
                }

                return ScrOperatorResult.Ok(ScrValue.Of(ScrTypeSet.Int));
            }

            case ScrUnaryOp.AddressOf:
                return ScrOperatorResult.Ok(ScrValue.Of(ScrTypeSet.Function));

            case ScrUnaryOp.Negate:
            {
                // Negating a vector is legal and yields a vector; both v1.5 and this tree said Unknown.
                if ( operand.MustBe(ScrTypeSet.Vector) )
                {
                    if ( operand.Constant is { Type: ScrTypeSet.Vector } vector )
                    {
                        return ScrOperatorResult.Ok(ScrValue.OfConstant(
                            ScrConstant.OfVector(new Vec3(-vector.Vector.X, -vector.Vector.Y, -vector.Vector.Z))));
                    }

                    return ScrOperatorResult.Ok(ScrValue.Of(ScrTypeSet.Vector));
                }

                if ( operand.Constant is { Type: ScrTypeSet.Int } integer )
                {
                    return ScrOperatorResult.Ok(ScrValue.OfConstant(ScrConstant.OfInt(-integer.Integer)));
                }

                if ( operand.Constant is { Type: ScrTypeSet.Float } real )
                {
                    return ScrOperatorResult.Ok(ScrValue.OfConstant(ScrConstant.OfFloat(-real.Real)));
                }

                if ( operand.MustBe(ScrTypeSet.Number) )
                {
                    return ScrOperatorResult.Ok(operand with { Constant = null });
                }

                return ScrOperatorResult.Ok(NumericOrUnknown(operand));
            }

            default:
                return ScrOperatorResult.Ok(ScrValue.Unknown);
        }
    }

    /// <summary>
    /// <c>+</c> and <c>-</c>. Vector arithmetic is decided first, then string concatenation, then
    /// numbers — the order is the fix for v1.5 typing <c>vector + float</c> as a string.
    /// </summary>
    private static ScrOperatorResult Additive(ScrBinaryOp op, ScrValue left, ScrValue right)
    {
        bool leftVector = left.MustBe(ScrTypeSet.Vector);
        bool rightVector = right.MustBe(ScrTypeSet.Vector);

        if ( leftVector && rightVector )
        {
            // v1.5 handled `vector + vector` and simply had no arm for `vector - vector`, which fell
            // through to "any".
            return ScrOperatorResult.Ok(FoldVectorPair(op, left, right));
        }

        // A vector on exactly one side. Adding a scalar to a vector is not a thing the engine does,
        // and v1.5 returned `string` for it through a mask that asked whether one side carried both
        // the Vector and Number bits.
        if ( leftVector != rightVector && (leftVector || rightVector)
            && !left.IsUnknown && !right.IsUnknown )
        {
            return new ScrOperatorResult(ScrValue.Of(ScrTypeSet.Vector), ScrOperandDiagnosis.UnsupportedOperands);
        }

        if ( op == ScrBinaryOp.Add && (left.MustBe(ScrTypeSet.AnyString) || right.MustBe(ScrTypeSet.AnyString)) )
        {
            // Content rather than Text: a literal keeps its quotes, and concatenating those would
            // produce `"a""b"`. The unquoting happens HERE, where a fold is rare, instead of on
            // every literal read.
            if ( left.Constant is { } a && right.Constant is { } b
                && a.Content is string first && b.Content is string second )
            {
                return ScrOperatorResult.Ok(ScrValue.OfConstant(ScrConstant.OfString(first + second)));
            }

            return ScrOperatorResult.Ok(ScrValue.Of(ScrTypeSet.String));
        }

        return ScrOperatorResult.Ok(Arithmetic(op, left, right));
    }

    /// <summary>
    /// <c>*</c> and <c>/</c>. A vector scaled by a number is a vector; the reverse — dividing a
    /// number BY a vector — is not, though v1.5 accepted it by writing the check symmetrically.
    /// </summary>
    private static ScrOperatorResult Scaling(ScrBinaryOp op, ScrValue left, ScrValue right)
    {
        bool leftVector = left.MustBe(ScrTypeSet.Vector);
        bool rightVector = right.MustBe(ScrTypeSet.Vector);

        if ( leftVector && rightVector )
        {
            // Neither multiplication nor division is defined between two vectors.
            return new ScrOperatorResult(ScrValue.Of(ScrTypeSet.Vector), ScrOperandDiagnosis.UnsupportedOperands);
        }

        if ( leftVector && right.MustBe(ScrTypeSet.Number) )
        {
            if ( op == ScrBinaryOp.Divide && IsZeroConstant(right) )
            {
                return new ScrOperatorResult(ScrValue.Of(ScrTypeSet.Vector), ScrOperandDiagnosis.DivisionByZero);
            }

            return ScrOperatorResult.Ok(FoldVectorScale(op, left, right));
        }

        // `2 * ( 1, 0, 0 )` is fine; `2 / ( 1, 0, 0 )` is not.
        if ( rightVector && left.MustBe(ScrTypeSet.Number) )
        {
            if ( op == ScrBinaryOp.Divide )
            {
                return new ScrOperatorResult(ScrValue.Of(ScrTypeSet.Vector), ScrOperandDiagnosis.UnsupportedOperands);
            }

            return ScrOperatorResult.Ok(FoldVectorScale(op, right, left));
        }

        if ( op == ScrBinaryOp.Divide && IsZeroConstant(right) )
        {
            return new ScrOperatorResult(ScrValue.Of(ScrTypeSet.Number), ScrOperandDiagnosis.DivisionByZero);
        }

        return ScrOperatorResult.Ok(Arithmetic(op, left, right));
    }

    private static ScrOperatorResult Modulo(ScrValue left, ScrValue right)
    {
        if ( IsZeroConstant(right) )
        {
            return new ScrOperatorResult(ScrValue.Of(ScrTypeSet.Int), ScrOperandDiagnosis.DivisionByZero);
        }

        if ( left.Constant is { Type: ScrTypeSet.Int } a && right.Constant is { Type: ScrTypeSet.Int } b && b.Integer != 0 )
        {
            return ScrOperatorResult.Ok(ScrValue.OfConstant(ScrConstant.OfInt(a.Integer % b.Integer)));
        }

        return ScrOperatorResult.Ok(ScrValue.Of(ScrTypeSet.Int));
    }

    private static ScrOperatorResult Comparison(ScrBinaryOp op, ScrValue left, ScrValue right)
    {
        if ( TryFoldComparison(op, left, right, out bool folded) )
        {
            return ScrOperatorResult.Ok(ScrValue.OfConstant(ScrConstant.OfBool(folded)));
        }

        return ScrOperatorResult.Ok(ScrValue.Of(ScrTypeSet.Bool));
    }

    private static ScrOperatorResult Bitwise(ScrBinaryOp op, ScrValue left, ScrValue right)
    {
        if ( left.Constant is { Type: ScrTypeSet.Int } a && right.Constant is { Type: ScrTypeSet.Int } b )
        {
            switch ( op )
            {
                case ScrBinaryOp.BitAnd: return ScrOperatorResult.Ok(ScrValue.OfConstant(ScrConstant.OfInt(a.Integer & b.Integer)));
                case ScrBinaryOp.BitOr: return ScrOperatorResult.Ok(ScrValue.OfConstant(ScrConstant.OfInt(a.Integer | b.Integer)));
                case ScrBinaryOp.BitXor: return ScrOperatorResult.Ok(ScrValue.OfConstant(ScrConstant.OfInt(a.Integer ^ b.Integer)));

                // A shift count outside 0..63 is undefined rather than wrapped, so it is not folded.
                case ScrBinaryOp.ShiftLeft when b.Integer is >= 0 and < 64:
                    return ScrOperatorResult.Ok(ScrValue.OfConstant(ScrConstant.OfInt(a.Integer << (int)b.Integer)));
                case ScrBinaryOp.ShiftRight when b.Integer is >= 0 and < 64:
                    return ScrOperatorResult.Ok(ScrValue.OfConstant(ScrConstant.OfInt(a.Integer >> (int)b.Integer)));
            }
        }

        return ScrOperatorResult.Ok(ScrValue.Of(ScrTypeSet.Int));
    }

    /// <summary>
    /// Numeric result typing, replacing <c>NumericResult</c>.
    ///
    /// The old one was asymmetric in a way that asserted types it did not know:
    /// <c>Float + Unknown</c> came out <c>Float</c> while <c>Int + Unknown</c> came out
    /// <c>Unknown</c>. Here an operand that could be anything makes the result a number at best,
    /// never a specific one.
    /// </summary>
    private static ScrValue Arithmetic(ScrBinaryOp op, ScrValue left, ScrValue right)
    {
        if ( TryFoldArithmetic(op, left, right, out ScrValue folded) )
        {
            return folded;
        }

        bool leftNumeric = left.MustBe(ScrTypeSet.Number | ScrTypeSet.Bool);
        bool rightNumeric = right.MustBe(ScrTypeSet.Number | ScrTypeSet.Bool);

        if ( !leftNumeric || !rightNumeric )
        {
            // Not enough is known to name a type. Number is the widest honest answer, since a
            // division always produces one and anything else here is an operand we cannot see.
            return ScrValue.Of(ScrTypeSet.Number, ScrImprecision.UnsupportedExpression);
        }

        // Division always produces a float, even between two ints.
        if ( op == ScrBinaryOp.Divide )
        {
            return ScrValue.Of(ScrTypeSet.Float);
        }

        // A bool is 0 or 1, so it behaves as an int in arithmetic — v1.5 had this backwards and
        // produced Float for `true + false`, because Bool sat inside its Number mask.
        bool leftInt = left.MustBe(ScrTypeSet.Int | ScrTypeSet.Bool);
        bool rightInt = right.MustBe(ScrTypeSet.Int | ScrTypeSet.Bool);

        if ( leftInt && rightInt )
        {
            return ScrValue.Of(ScrTypeSet.Int);
        }

        if ( left.MustBe(ScrTypeSet.Float) && right.MustBe(ScrTypeSet.Float) )
        {
            return ScrValue.Of(ScrTypeSet.Float);
        }

        // One int, one float, or a union of the two: the result is one or the other and the lattice
        // can say so exactly rather than guessing float.
        return ScrValue.Of(ScrTypeSet.Number);
    }

    private static bool TryFoldArithmetic(ScrBinaryOp op, ScrValue left, ScrValue right, out ScrValue folded)
    {
        folded = default;

        if ( left.Constant is not { } a || right.Constant is not { } b )
        {
            return false;
        }

        // A constant's Type is always a single bit, so equality is the right test.
        if ( a.Type is not (ScrTypeSet.Int or ScrTypeSet.Float) )
        {
            return false;
        }

        if ( b.Type is not (ScrTypeSet.Int or ScrTypeSet.Float) )
        {
            return false;
        }

        bool bothInt = a.Type == ScrTypeSet.Int && b.Type == ScrTypeSet.Int;

        if ( op == ScrBinaryOp.Divide )
        {
            if ( b.AsDouble() == 0 )
            {
                return false;
            }

            folded = ScrValue.OfConstant(ScrConstant.OfFloat(a.AsDouble() / b.AsDouble()));
            return true;
        }

        if ( bothInt )
        {
            long result = op switch
            {
                ScrBinaryOp.Add => a.Integer + b.Integer,
                ScrBinaryOp.Subtract => a.Integer - b.Integer,
                ScrBinaryOp.Multiply => a.Integer * b.Integer,
                _ => 0,
            };

            folded = ScrValue.OfConstant(ScrConstant.OfInt(result));
            return true;
        }

        double real = op switch
        {
            ScrBinaryOp.Add => a.AsDouble() + b.AsDouble(),
            ScrBinaryOp.Subtract => a.AsDouble() - b.AsDouble(),
            ScrBinaryOp.Multiply => a.AsDouble() * b.AsDouble(),
            _ => 0,
        };

        folded = ScrValue.OfConstant(ScrConstant.OfFloat(real));
        return true;
    }

    private static bool TryFoldComparison(ScrBinaryOp op, ScrValue left, ScrValue right, out bool result)
    {
        result = false;

        if ( op is ScrBinaryOp.And or ScrBinaryOp.Or )
        {
            if ( left.Truthiness is not bool a || right.Truthiness is not bool b )
            {
                return false;
            }

            result = op == ScrBinaryOp.And ? a && b : a || b;
            return true;
        }

        if ( left.Constant is not { } x || right.Constant is not { } y )
        {
            return false;
        }

        bool numeric = x.Type is ScrTypeSet.Int or ScrTypeSet.Float && y.Type is ScrTypeSet.Int or ScrTypeSet.Float;

        switch ( op )
        {
            case ScrBinaryOp.Less when numeric: result = x.AsDouble() < y.AsDouble(); return true;
            case ScrBinaryOp.LessOrEqual when numeric: result = x.AsDouble() <= y.AsDouble(); return true;
            case ScrBinaryOp.Greater when numeric: result = x.AsDouble() > y.AsDouble(); return true;
            case ScrBinaryOp.GreaterOrEqual when numeric: result = x.AsDouble() >= y.AsDouble(); return true;

            // `==` compares VALUES, which is the correction to v1.5 folding `5 == 3` to true because
            // both operands were truthy.
            case ScrBinaryOp.Equal when numeric: result = x.AsDouble() == y.AsDouble(); return true;
            case ScrBinaryOp.NotEqual when numeric: result = x.AsDouble() != y.AsDouble(); return true;

            case ScrBinaryOp.Equal: result = x == y; return true;
            case ScrBinaryOp.NotEqual: result = x != y; return true;

            // `===` requires the same type as well as the same value, so no int/float widening.
            case ScrBinaryOp.StrictEqual: result = x.Type == y.Type && x == y; return true;
            case ScrBinaryOp.StrictNotEqual: result = !(x.Type == y.Type && x == y); return true;

            default: return false;
        }
    }

    private static ScrValue FoldVectorPair(ScrBinaryOp op, ScrValue left, ScrValue right)
    {
        if ( left.Constant is { Type: ScrTypeSet.Vector } a && right.Constant is { Type: ScrTypeSet.Vector } b )
        {
            Vec3 result = op == ScrBinaryOp.Add
                ? new Vec3(a.Vector.X + b.Vector.X, a.Vector.Y + b.Vector.Y, a.Vector.Z + b.Vector.Z)
                : new Vec3(a.Vector.X - b.Vector.X, a.Vector.Y - b.Vector.Y, a.Vector.Z - b.Vector.Z);

            return ScrValue.OfConstant(ScrConstant.OfVector(result));
        }

        return ScrValue.Of(ScrTypeSet.Vector);
    }

    private static ScrValue FoldVectorScale(ScrBinaryOp op, ScrValue vector, ScrValue scalar)
    {
        if ( vector.Constant is { Type: ScrTypeSet.Vector } v && scalar.Constant is { } s
            && s.Type is ScrTypeSet.Int or ScrTypeSet.Float )
        {
            double factor = s.AsDouble();
            if ( op == ScrBinaryOp.Divide )
            {
                if ( factor == 0 )
                {
                    return ScrValue.Of(ScrTypeSet.Vector);
                }

                return ScrValue.OfConstant(ScrConstant.OfVector(
                    new Vec3(v.Vector.X / factor, v.Vector.Y / factor, v.Vector.Z / factor)));
            }

            return ScrValue.OfConstant(ScrConstant.OfVector(
                new Vec3(v.Vector.X * factor, v.Vector.Y * factor, v.Vector.Z * factor)));
        }

        return ScrValue.Of(ScrTypeSet.Vector);
    }

    /// <summary>
    /// Whether the value is a known zero.
    ///
    /// Read off the CONSTANT, never off truthiness. v1.5 tested <c>BooleanValue == false</c>, which
    /// is a proxy that misses <c>2 - 2</c> — nothing folded there, so nothing was falsy — and fires
    /// on <c>x / ""</c>, which is not a division by zero.
    /// </summary>
    private static bool IsZeroConstant(ScrValue value)
    {
        return value.Constant is { } constant
            && constant.Type is ScrTypeSet.Int or ScrTypeSet.Float
            && constant.AsDouble() == 0;
    }

    private static ScrValue NumericOrUnknown(ScrValue operand)
    {
        return operand.MayBe(ScrTypeSet.Number)
            ? ScrValue.Of(ScrTypeSet.Number, ScrImprecision.UnsupportedExpression)
            : ScrValue.Unknown;
    }
}
