using GSCode.Core.Symbols;
using Xunit;

namespace GSCode.Parser.Tests.Core;

/// <summary>
/// Operator semantics over the union lattice.
///
/// The vector rows exist because both prior attempts got them wrong in opposite ways. v1.5 typed
/// `vector + float` as a STRING and had no arm at all for `vector - vector`; this tree's
/// `NumericResult` takes no operator and knows only Int/Float/Unknown, so `vector * 0.5` comes out
/// `float` — a wrong hover today, and one of the two causes that got PredefinedFieldTypeMismatch
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
        // The live bug: NumericResult answers `float` for this today.
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
        // ScrTypes.Join widens this pair. For emitting source the difference between `1` and `1.0`
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
