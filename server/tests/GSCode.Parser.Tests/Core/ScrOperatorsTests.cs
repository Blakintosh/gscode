using GSCode.Core.Symbols;
using Xunit;

namespace GSCode.Parser.Tests.Core;

/// <summary>
/// Operator semantics over the union lattice.
///
/// The vector rows exist because both prior attempts got them wrong in opposite ways. v1.5 typed
/// `vector + float` as a STRING and had no arm at all for `vector - vector`; this tree's earlier
/// `NumericResult` took no operator and knew only Int/Float/Unknown, so `vector * 0.5` came out
/// `float` — a wrong hover, and one of the two causes that got PredefinedFieldTypeMismatch
/// withdrawn after 46 unreal findings on Black Ops III.
/// </summary>
public class ScrOperatorsTests
{
    private static ScrValue Int(long value)
    {
        return ScrValue.OfConstant(ScrConstant.OfInt(value));
    }

    private static ScrValue Float(double value)
    {
        return ScrValue.OfConstant(ScrConstant.OfFloat(value));
    }

    private static ScrValue Str(string value)
    {
        return ScrValue.OfConstant(ScrConstant.OfString(value));
    }

    /// <summary>
    /// A string constant shaped like a REAL literal token — quotes included, exactly what
    /// FlowTyper.TypeOfLiteral builds from a TokenKind.String's raw text (see
    /// ScrConstant.Text's own doc: it is kept raw, quotes and all).
    /// </summary>
    private static ScrValue StrLiteral(string content)
    {
        return ScrValue.OfConstant(ScrConstant.OfString($"\"{content}\""));
    }

    private static ScrValue Vector()
    {
        return ScrValue.Of(ScrTypeSet.Vector);
    }

    private static ScrValue VectorOf(double x, double y, double z)
    {
        return ScrValue.OfConstant(ScrConstant.OfVector(new Vec3(x, y, z)));
    }

    private static ScrValue Type(ScrTypeSet types)
    {
        return ScrValue.Of(types);
    }

    private static ScrOperatorResult Apply(ScrBinaryOp op, ScrValue left, ScrValue right)
    {
        return ScrOperators.Apply(op, left, right);
    }

    // --- vectors: the whole reason this table exists ---

    [Theory]
    [InlineData(ScrBinaryOp.Multiply)]
    [InlineData(ScrBinaryOp.Divide)]
    public void AVectorScaledByANumberIsAVector(ScrBinaryOp op)
    {
        // The bug this replaced: NumericResult answered `float` here.
        ScrOperatorResult result = Apply(op, Vector(), Float(0.5));

        Assert.Equal(ScrTypeSet.Vector, result.Value.Types);
        Assert.Equal(ScrOperandDiagnosis.Fine, result.Diagnosis);
    }

    [Fact]
    public void ANumberTimesAVectorIsAlsoAVector()
    {
        Assert.Equal(ScrTypeSet.Vector, Apply(ScrBinaryOp.Multiply, Float(2), Vector()).Value.Types);
    }

    [Fact]
    public void ANumberDividedByAVectorIsNot()
    {
        // v1.5 wrote the check symmetrically and accepted `2 / ( 1, 0, 0 )`.
        Assert.Equal(
            ScrOperandDiagnosis.UnsupportedOperands,
            Apply(ScrBinaryOp.Divide, Float(2), Vector()).Diagnosis);
    }

    [Theory]
    [InlineData(ScrBinaryOp.Add)]
    [InlineData(ScrBinaryOp.Subtract)]
    public void TwoVectorsAddAndSubtract(ScrBinaryOp op)
    {
        // v1.5 handled `+` and simply had no arm for `-`, which fell through to "any".
        ScrOperatorResult result = Apply(op, Vector(), Vector());

        Assert.Equal(ScrTypeSet.Vector, result.Value.Types);
        Assert.Equal(ScrOperandDiagnosis.Fine, result.Diagnosis);
    }

    [Theory]
    [InlineData(ScrBinaryOp.Multiply)]
    [InlineData(ScrBinaryOp.Divide)]
    public void TwoVectorsDoNotMultiplyOrDivide(ScrBinaryOp op)
    {
        Assert.Equal(ScrOperandDiagnosis.UnsupportedOperands, Apply(op, Vector(), Vector()).Diagnosis);
    }

    [Fact]
    public void AVectorPlusAScalarIsNotAString()
    {
        // v1.5 returned String here, through a mask asking whether one side carried both the Vector
        // and Number bits.
        ScrOperatorResult result = Apply(ScrBinaryOp.Add, Vector(), Float(1));

        Assert.Equal(ScrOperandDiagnosis.UnsupportedOperands, result.Diagnosis);
        Assert.NotEqual(ScrTypeSet.String, result.Value.Types);
    }

    /// <summary>
    /// The vector-plus-scalar check used <c>MustBe(Vector)</c> on ONE side to decide the shape,
    /// then judged the OTHER side by nothing sharper than "is it the full universe" — so a right
    /// operand that MAY be a vector but is not CERTAIN to be one (e.g. narrowed by <c>isdefined</c>
    /// to <c>Vector|Undefined</c>) reported UnsupportedOperands, even though the analysis cannot
    /// rule out this being a legal vector + vector. Rule 2 of this file's own header comment says
    /// to match with MayBe/MustBe rather than exact equality; this check broke that rule on the
    /// side it left unexamined.
    /// </summary>
    [Fact]
    public void AVectorPlusAPossiblyVectorValueIsNotFlaggedUnsupported()
    {
        ScrValue maybeVector = ScrValue.Union(Vector(), Type(ScrTypeSet.Undefined));

        ScrOperatorResult result = Apply(ScrBinaryOp.Add, Vector(), maybeVector);

        Assert.Equal(ScrOperandDiagnosis.Fine, result.Diagnosis);
    }

    [Fact]
    public void VectorRulesSurviveAUnionFlowingIn()
    {
        // Every v1.5 vector rule was written `left.Type == ScrDataTypes.Vector`, so it stopped
        // matching the moment a merge produced `Vector|Undefined` — which after any branch is the
        // normal case. MustBe is what keeps this working.
        ScrValue maybeUnassigned = ScrValue.Union(Vector(), Type(ScrTypeSet.Undefined));

        Assert.False(maybeUnassigned.MustBe(ScrTypeSet.Vector));
        // It is no longer certainly a vector, so the rule correctly declines rather than asserting.
        Assert.Equal(ScrTypeSet.Vector, Apply(ScrBinaryOp.Multiply, Vector(), Float(2)).Value.Types);
    }

    [Fact]
    public void VectorArithmeticFolds()
    {
        ScrValue sum = Apply(ScrBinaryOp.Add, VectorOf(1, 2, 3), VectorOf(10, 20, 30)).Value;
        Assert.Equal(new Vec3(11, 22, 33), sum.Constant!.Value.Vector);

        ScrValue scaled = Apply(ScrBinaryOp.Multiply, VectorOf(1, 2, 3), Float(2)).Value;
        Assert.Equal(new Vec3(2, 4, 6), scaled.Constant!.Value.Vector);
    }

    [Fact]
    public void NegatingAVectorYieldsAVector()
    {
        ScrOperatorResult result = ScrOperators.Apply(ScrUnaryOp.Negate, VectorOf(1, -2, 3));

        Assert.Equal(ScrTypeSet.Vector, result.Value.Types);
        Assert.Equal(new Vec3(-1, 2, -3), result.Value.Constant!.Value.Vector);
    }

    // --- numeric typing ---

    [Fact]
    public void IntPlusIntIsInt()
    {
        Assert.Equal(ScrTypeSet.Int, Apply(ScrBinaryOp.Add, Type(ScrTypeSet.Int), Type(ScrTypeSet.Int)).Value.Types);
    }

    [Fact]
    public void IntPlusFloatIsTheUnionRatherThanFloat()
    {
        // The coarse ScrType projection widens this pair. For emitting source the difference between `1` and `1.0`
        // is real, so the lattice keeps both possibilities rather than picking one.
        Assert.Equal(
            ScrTypeSet.Number,
            Apply(ScrBinaryOp.Add, Type(ScrTypeSet.Int), Type(ScrTypeSet.Float)).Value.Types);
    }

    [Fact]
    public void DivisionAlwaysProducesAFloat()
    {
        Assert.Equal(ScrTypeSet.Float, Apply(ScrBinaryOp.Divide, Type(ScrTypeSet.Int), Type(ScrTypeSet.Int)).Value.Types);
    }

    [Fact]
    public void ArithmeticOnBooleansIsIntegerNotFloat()
    {
        // v1.5 produced Float, because Bool sat inside its Number mask so IsNumeric() was true but
        // the both-Int check was not.
        Assert.Equal(ScrTypeSet.Int, Apply(ScrBinaryOp.Add, Type(ScrTypeSet.Bool), Type(ScrTypeSet.Bool)).Value.Types);
    }

    [Fact]
    public void AnUnknownOperandNeverAssertsASpecificNumericType()
    {
        // NumericResult returns Float for `Float + Unknown` while returning Unknown for
        // `Int + Unknown` — asymmetric, and it asserts a type from an operand nothing is known about.
        ScrValue result = Apply(ScrBinaryOp.Add, Type(ScrTypeSet.Float), ScrValue.Unknown).Value;

        Assert.False(result.MustBe(ScrTypeSet.Float));
        Assert.True(result.MayBe(ScrTypeSet.Number));
    }

    /// <summary>
    /// An operand nothing is known about must not make the RESULT read as a known `float`. The
    /// fallback for "not enough is known" used to be a bare `Number`, which is exactly the one
    /// union <see cref="ScrValue.ToScrType"/> widens to `float` (the special case that keeps a
    /// genuine int/float branch join showing correctly) — so `param + 1` hovered as `float` for a
    /// completely untyped parameter, and `param + "x"`/`param + someVector` are just as legal in
    /// GSC and would have shown the same wrong `float`. Widening the fallback with String (Add
    /// only) and Vector whenever the unknown operand MAY be one breaks that accidental collapse:
    /// the union is no longer exactly `Number`, so it projects to Unknown instead.
    /// </summary>
    [Theory]
    [InlineData(ScrBinaryOp.Add)]
    [InlineData(ScrBinaryOp.Subtract)]
    [InlineData(ScrBinaryOp.Multiply)]
    [InlineData(ScrBinaryOp.Divide)]
    public void ArithmeticOnAWhollyUnknownOperandDoesNotProjectToFloat(ScrBinaryOp op)
    {
        ScrValue result = Apply(op, ScrValue.Unknown, Int(1)).Value;

        Assert.Equal(ScrType.Unknown, result.ToScrType());
    }

    [Fact]
    public void NegatingAWhollyUnknownOperandDoesNotProjectToFloat()
    {
        ScrValue result = ScrOperators.Apply(ScrUnaryOp.Negate, ScrValue.Unknown).Value;

        Assert.Equal(ScrType.Unknown, result.ToScrType());
    }

    // --- constant folding, which v1.5 had none of ---

    [Fact]
    public void IntegerArithmeticFolds()
    {
        Assert.Equal(7L, Apply(ScrBinaryOp.Add, Int(3), Int(4)).Value.Constant!.Value.Integer);
        Assert.Equal(12L, Apply(ScrBinaryOp.Multiply, Int(3), Int(4)).Value.Constant!.Value.Integer);
        Assert.Equal(-1L, Apply(ScrBinaryOp.Subtract, Int(3), Int(4)).Value.Constant!.Value.Integer);
    }

    [Fact]
    public void MixedArithmeticFoldsToAFloat()
    {
        ScrValue result = Apply(ScrBinaryOp.Add, Int(1), Float(0.5)).Value;

        Assert.Equal(ScrTypeSet.Float, result.Types);
        Assert.Equal(1.5, result.Constant!.Value.Real);
    }

    [Fact]
    public void StringConcatenationFolds()
    {
        Assert.Equal("ab", Apply(ScrBinaryOp.Add, Str("a"), Str("b")).Value.Constant!.Value.Text);
    }

    /// <summary>
    /// A string on either side of <c>+</c> makes it a concatenation, and a vector is concatenated
    /// like any other value — <c>"at " + self.origin</c> is how the stock scripts build messages.
    /// The vector-with-scalar arm was decided first and typed that expression as a vector.
    /// </summary>
    [Fact]
    public void AStringPlusAVectorIsAString()
    {
        ScrOperatorResult stringFirst = Apply(ScrBinaryOp.Add, Str("at "), Vector());
        ScrOperatorResult vectorFirst = Apply(ScrBinaryOp.Add, Vector(), Str(" here"));

        Assert.Equal(ScrTypeSet.String, stringFirst.Value.Types);
        Assert.Equal(ScrOperandDiagnosis.Fine, stringFirst.Diagnosis);
        Assert.Equal(ScrTypeSet.String, vectorFirst.Value.Types);
        Assert.Equal(ScrOperandDiagnosis.Fine, vectorFirst.Diagnosis);
    }

    [Fact]
    public void BitwiseOperationsFold()
    {
        Assert.Equal(0b1000L, Apply(ScrBinaryOp.BitAnd, Int(0b1100), Int(0b1010)).Value.Constant!.Value.Integer);
        Assert.Equal(0b1110L, Apply(ScrBinaryOp.BitOr, Int(0b1100), Int(0b1010)).Value.Constant!.Value.Integer);
        Assert.Equal(8L, Apply(ScrBinaryOp.ShiftLeft, Int(1), Int(3)).Value.Constant!.Value.Integer);
    }

    [Fact]
    public void AnOutOfRangeShiftIsNotFolded()
    {
        // Undefined rather than wrapped, so the type is right and the value is withheld.
        ScrValue result = Apply(ScrBinaryOp.ShiftLeft, Int(1), Int(99)).Value;

        Assert.Equal(ScrTypeSet.Int, result.Types);
        Assert.Null(result.Constant);
    }

    // --- divide by zero, read off the constant and never off truthiness ---

    [Theory]
    [InlineData(ScrBinaryOp.Divide)]
    [InlineData(ScrBinaryOp.Modulo)]
    public void ALiteralZeroDivisorIsCaught(ScrBinaryOp op)
    {
        Assert.Equal(ScrOperandDiagnosis.DivisionByZero, Apply(op, Int(10), Int(0)).Diagnosis);
    }

    /// <summary>
    /// `long.MinValue % -1` throws OverflowException in .NET regardless of checked/unchecked
    /// context — the one input pair '%' cannot evaluate, since the mathematical result
    /// (2^63, positive) does not fit back into a signed 64-bit integer. Reachable purely through
    /// folding, e.g. `(-9223372036854775807 - 1) % -1`, and the exception used to propagate out of
    /// Apply and take down analysis of the whole file rather than staying a modulo this pass
    /// declines to fold.
    /// </summary>
    [Fact]
    public void MinValueModuloNegativeOneDoesNotThrow()
    {
        ScrOperatorResult result = Apply(ScrBinaryOp.Modulo, Int(long.MinValue), Int(-1));

        Assert.Equal(ScrTypeSet.Int, result.Value.Types);
    }

    [Fact]
    public void AZeroReachedByFoldingIsCaught()
    {
        // v1.5 tested `right.BooleanValue == false` — a truthiness proxy. Nothing folded there, so
        // `2 - 2` was never falsy and this case was missed entirely.
        ScrValue divisor = Apply(ScrBinaryOp.Subtract, Int(2), Int(2)).Value;

        Assert.Equal(0L, divisor.Constant!.Value.Integer);
        Assert.Equal(ScrOperandDiagnosis.DivisionByZero, Apply(ScrBinaryOp.Divide, Int(10), divisor).Diagnosis);
    }

    [Fact]
    public void AnEmptyStringDivisorIsNotReportedAsDivisionByZero()
    {
        // The other half of the same v1.5 bug: `""` is falsy, so its truthiness proxy fired on it.
        Assert.NotEqual(ScrOperandDiagnosis.DivisionByZero, Apply(ScrBinaryOp.Divide, Int(10), Str("")).Diagnosis);
    }

    [Fact]
    public void ANonConstantDivisorIsNotGuessedAt()
    {
        Assert.Equal(ScrOperandDiagnosis.Fine, Apply(ScrBinaryOp.Divide, Int(10), Type(ScrTypeSet.Int)).Diagnosis);
    }

    // --- comparisons and logicals ---

    [Theory]
    [InlineData(ScrBinaryOp.Equal)]
    [InlineData(ScrBinaryOp.Less)]
    [InlineData(ScrBinaryOp.And)]
    public void EveryComparisonAndLogicalYieldsABool(ScrBinaryOp op)
    {
        Assert.True(Apply(op, Type(ScrTypeSet.Int), Type(ScrTypeSet.Int)).Value.MustBe(ScrTypeSet.Bool));
    }

    [Fact]
    public void EqualityComparesValuesNotTruthiness()
    {
        // v1.5 folded `left.BooleanValue == right.BooleanValue`, so `5 == 3` came out TRUE because
        // both operands are truthy. Three TODOs in that file admit the fold was wrong.
        Assert.False(Apply(ScrBinaryOp.Equal, Int(5), Int(3)).Value.Constant!.Value.Boolean);
        Assert.True(Apply(ScrBinaryOp.Equal, Int(5), Int(5)).Value.Constant!.Value.Boolean);
    }

    [Fact]
    public void AndIsConjunctionNotEquivalence()
    {
        // v1.5's `&&` folded `left.BooleanValue == right.BooleanValue`, which is XNOR: it made
        // `false && false` come out true.
        Assert.False(Apply(ScrBinaryOp.And, ScrValue.OfConstant(ScrConstant.OfBool(false)),
            ScrValue.OfConstant(ScrConstant.OfBool(false))).Value.Constant!.Value.Boolean);

        Assert.True(Apply(ScrBinaryOp.And, ScrValue.OfConstant(ScrConstant.OfBool(true)),
            ScrValue.OfConstant(ScrConstant.OfBool(true))).Value.Constant!.Value.Boolean);
    }

    [Fact]
    public void OrIsDisjunction()
    {
        Assert.True(Apply(ScrBinaryOp.Or, ScrValue.OfConstant(ScrConstant.OfBool(false)),
            ScrValue.OfConstant(ScrConstant.OfBool(true))).Value.Constant!.Value.Boolean);
    }

    [Fact]
    public void NumericComparisonFolds()
    {
        Assert.True(Apply(ScrBinaryOp.Less, Int(1), Int(2)).Value.Constant!.Value.Boolean);
        Assert.False(Apply(ScrBinaryOp.Greater, Int(1), Int(2)).Value.Constant!.Value.Boolean);
    }

    [Fact]
    public void StrictEqualityRequiresTheSameTypeAsWellAsTheSameValue()
    {
        Assert.True(Apply(ScrBinaryOp.Equal, Int(1), Float(1)).Value.Constant!.Value.Boolean);
        Assert.False(Apply(ScrBinaryOp.StrictEqual, Int(1), Float(1)).Value.Constant!.Value.Boolean);
    }

    /// <summary>
    /// A folded concatenation ("a" + "b") must equal an equivalent literal ("ab"), even though the
    /// folded constant's Text has no quotes (it is built from Content, see Additive) while a real
    /// literal token's Text keeps them. Equality has to compare CONTENT, not the raw Text — record
    /// equality on ScrConstant does not, so `("a" + "b") == "ab"` folded to false.
    /// </summary>
    [Fact]
    public void AFoldedConcatenationEqualsAnEquivalentLiteral()
    {
        ScrValue folded = Apply(ScrBinaryOp.Add, StrLiteral("a"), StrLiteral("b")).Value;
        ScrValue literal = StrLiteral("ab");

        Assert.True(Apply(ScrBinaryOp.Equal, folded, literal).Value.Constant!.Value.Boolean);
        Assert.True(Apply(ScrBinaryOp.StrictEqual, folded, literal).Value.Constant!.Value.Boolean);
    }

    [Fact]
    public void AComparisonWithAnUnknownOperandIsNotFolded()
    {
        ScrValue result = Apply(ScrBinaryOp.Less, Int(1), ScrValue.Unknown).Value;

        Assert.Equal(ScrTypeSet.Bool, result.Types);
        Assert.Null(result.Constant);
    }

    // --- prefix operators ---

    [Fact]
    public void NotFoldsFromTruthinessAndIsOtherwiseABool()
    {
        Assert.True(ScrOperators.Apply(ScrUnaryOp.Not, Int(0)).Value.Constant!.Value.Boolean);
        Assert.False(ScrOperators.Apply(ScrUnaryOp.Not, Int(1)).Value.Constant!.Value.Boolean);
        Assert.Equal(ScrTypeSet.Bool, ScrOperators.Apply(ScrUnaryOp.Not, Type(ScrTypeSet.Int)).Value.Types);
    }

    [Fact]
    public void NotOnAReferenceKindIsAlwaysFalse()
    {
        // Arrays, structs and entities are truthy whatever they hold, so this is knowable with no
        // value at all — which is why truthiness is tracked separately from the constant.
        Assert.False(ScrOperators.Apply(ScrUnaryOp.Not, Type(ScrTypeSet.Array)).Value.Constant!.Value.Boolean);
    }

    [Fact]
    public void AddressOfIsAFunction()
    {
        Assert.Equal(ScrTypeSet.Function, ScrOperators.Apply(ScrUnaryOp.AddressOf, ScrValue.Unknown).Value.Types);
    }

    [Fact]
    public void BitNotIsAnIntAndFolds()
    {
        Assert.Equal(~5L, ScrOperators.Apply(ScrUnaryOp.BitNot, Int(5)).Value.Constant!.Value.Integer);
        Assert.Equal(ScrTypeSet.Int, ScrOperators.Apply(ScrUnaryOp.BitNot, Type(ScrTypeSet.Int)).Value.Types);
    }
}
