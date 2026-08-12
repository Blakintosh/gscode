using System.Collections.Immutable;
using GSCode.Core.Symbols;
using Xunit;

namespace GSCode.Parser.Tests.Core;

/// <summary>
/// The union lattice the transpiler is built on.
///
/// Most of what is pinned here is a deliberate reversal of v1.5's design, so the tests are written
/// against the mistakes rather than only the behaviour: disjoint bits (v1.5 had `Int = 1&lt;&lt;1 | Bool`),
/// an explicit universe (v1.5's `~0u &amp; ~Error` carried junk bits), unions that do not collapse
/// (`ScrType.Join` widens int+float to float), and must/may in place of a single trust flag.
/// </summary>
public class ScrValueTests
{
    // --- the encoding ---

    [Fact]
    public void EveryMemberIsOneBitAndNoTwoMembersOverlap()
    {
        // The correction to v1.5. `Int = 1<<1 | Bool` bought one implicit coercion and cost a subset
        // test that matched ints against bool, an IsExactly method to undo it, IsNumeric() answering
        // true for booleans, and four suppression rules so type names printed correctly.
        ScrTypeSet seen = ScrTypeSet.None;

        foreach ( ScrTypeSet member in ScrValues.Members )
        {
            Assert.True((ulong)member != 0 && ((ulong)member & ((ulong)member - 1)) == 0, $"{member} is not a single bit");
            Assert.Equal(ScrTypeSet.None, seen & member);
            seen |= member;
        }
    }

    [Fact]
    public void TheUniverseIsExactlyTheMembers()
    {
        // v1.5 wrote `Any = ~0u & ~Error` with `Error = 1 << 60` on a uint enum — C# masks the shift
        // to five bits, so Error was really 1<<28 and Any carried eleven unallocated junk bits.
        ScrTypeSet all = ScrTypeSet.None;
        foreach ( ScrTypeSet member in ScrValues.Members )
        {
            all |= member;
        }

        Assert.Equal(ScrTypeSet.Universe, all);
    }

    [Fact]
    public void TheConvenienceAliasesAreUnionsNotSupertypes()
    {
        Assert.Equal(ScrTypeSet.Int | ScrTypeSet.Float, ScrTypeSet.Number);
        Assert.Equal(ScrTypeSet.String | ScrTypeSet.IString | ScrTypeSet.HashString, ScrTypeSet.AnyString);

        // The point: an int is NOT a bool, however convenient v1.5 found the opposite.
        Assert.Equal(ScrTypeSet.None, ScrTypeSet.Int & ScrTypeSet.Bool);
        Assert.Equal(ScrTypeSet.None, ScrTypeSet.IString & ScrTypeSet.String);
    }

    // --- must / may ---

    [Fact]
    public void MustBeIsSubsetAndMayBeIsIntersection()
    {
        ScrValue exact = ScrValue.Of(ScrTypeSet.Array);
        Assert.True(exact.MustBe(ScrTypeSet.Array));
        Assert.True(exact.MayBe(ScrTypeSet.Array));

        ScrValue union = ScrValue.Union(ScrValue.Of(ScrTypeSet.Array), ScrValue.Of(ScrTypeSet.Struct));
        Assert.False(union.MustBe(ScrTypeSet.Array));
        Assert.True(union.MayBe(ScrTypeSet.Array));
    }

    [Fact]
    public void TheUndecidableCaseIsDistinguishableFromBothDecidedOnes()
    {
        // This is the whole reason for must/may over v1.5's single Indeterminate bool. That bit could
        // say "do not trust this value" but never "it is one of exactly these two, and one is unsafe"
        // — which is the question array pass-semantics turns on.
        ScrValue array = ScrValue.Of(ScrTypeSet.Array);
        ScrValue structure = ScrValue.Of(ScrTypeSet.Struct);
        ScrValue either = ScrValue.Union(array, structure);

        Assert.True(array.MustBe(ScrTypeSet.Array));                        // rewrite it
        Assert.False(structure.MayBe(ScrTypeSet.Array));                    // leave it alone
        Assert.True(either.MayBe(ScrTypeSet.Array) && !either.MustBe(ScrTypeSet.Array)); // escalate
    }

    [Fact]
    public void MustBeIsFalseForTheEmptySet()
    {
        // "no type at all" must not vacuously satisfy every query.
        Assert.False(ScrValue.Nothing.MustBe(ScrTypeSet.Array));
        Assert.False(ScrValue.Nothing.MayBe(ScrTypeSet.Array));
    }

    // --- union ---

    [Fact]
    public void UnionKeepsTheUnionRatherThanCollapsing()
    {
        ScrValue joined = ScrValue.Union(ScrValue.Of(ScrTypeSet.Int), ScrValue.Of(ScrTypeSet.String));

        Assert.Equal(ScrTypeSet.Int | ScrTypeSet.String, joined.Types);
        // ScrTypes.Join would have produced Unknown here, which a rewriter cannot act on.
        Assert.Equal(ScrType.Unknown, joined.ToScrType());
    }

    [Fact]
    public void IntAndFloatDoNotWidenToFloat()
    {
        // ScrTypes.Join widens this pair, which is right for a hover label and wrong for emitting
        // source: `1` and `1.0` are different text.
        ScrValue joined = ScrValue.Union(ScrValue.Of(ScrTypeSet.Int), ScrValue.Of(ScrTypeSet.Float));

        Assert.Equal(ScrTypeSet.Number, joined.Types);
        Assert.False(joined.MustBe(ScrTypeSet.Float));
    }

    [Fact]
    public void UnionWithNothingIsIdentity()
    {
        ScrValue value = ScrValue.Of(ScrTypeSet.Int);

        Assert.Equal(value, ScrValue.Union(value, ScrValue.Nothing));
        Assert.Equal(value, ScrValue.Union(ScrValue.Nothing, value));
    }

    [Fact]
    public void UnionIsCommutativeAndIdempotent()
    {
        ScrValue left = ScrValue.Of(ScrTypeSet.Int);
        ScrValue right = ScrValue.Of(ScrTypeSet.String);

        Assert.Equal(ScrValue.Union(left, right), ScrValue.Union(right, left));
        Assert.Equal(left, ScrValue.Union(left, left));
    }

    [Fact]
    public void AConstantSurvivesOnlyWhenBothSidesAgree()
    {
        ScrValue four = ScrValue.OfConstant(ScrConstant.OfInt(4));
        ScrValue eight = ScrValue.OfConstant(ScrConstant.OfInt(8));

        Assert.Equal(ScrConstant.OfInt(4), ScrValue.Union(four, four).Constant);
        Assert.Null(ScrValue.Union(four, eight).Constant);
    }

    [Fact]
    public void DisagreeingBranchesRecordWhyTheSetWidened()
    {
        // The set is precise, not failed — but a rewriter may still want to know no single path
        // produced it, so the reason is carried rather than the value being marked unknown.
        ScrValue joined = ScrValue.Union(ScrValue.Of(ScrTypeSet.Int), ScrValue.Of(ScrTypeSet.String));

        Assert.Equal(ScrImprecision.BranchDisagreement, joined.Imprecision);
    }

    [Fact]
    public void AgreeingBranchesStayExact()
    {
        ScrValue joined = ScrValue.Union(ScrValue.Of(ScrTypeSet.Int), ScrValue.Of(ScrTypeSet.Int));

        Assert.Equal(ScrImprecision.None, joined.Imprecision);
        Assert.True(joined.IsExact);
    }

    // --- narrowing ---

    [Fact]
    public void WithoutRemovesATypeAndRestrictKeepsOne()
    {
        ScrValue maybeUnassigned = ScrValue.Union(ScrValue.Of(ScrTypeSet.Int), ScrValue.Of(ScrTypeSet.Undefined));

        Assert.Equal(ScrTypeSet.Int, maybeUnassigned.Without(ScrTypeSet.Undefined).Types);
        Assert.Equal(ScrTypeSet.Undefined, maybeUnassigned.Restrict(ScrTypeSet.Undefined).Types);
    }

    [Fact]
    public void NarrowingDropsAConstantItContradicts()
    {
        ScrValue four = ScrValue.OfConstant(ScrConstant.OfInt(4));

        Assert.Null(four.Without(ScrTypeSet.Int).Constant);
        Assert.NotNull(four.Without(ScrTypeSet.String).Constant);
    }

    // --- truthiness ---

    [Theory]
    [InlineData(ScrTypeSet.Array)]
    [InlineData(ScrTypeSet.Struct)]
    [InlineData(ScrTypeSet.Entity)]
    [InlineData(ScrTypeSet.Vector)]
    public void ReferenceKindsAndVectorsAreAlwaysTruthy(ScrTypeSet type)
    {
        // Knowable from the type alone, with no value — which is why truthiness is kept separate
        // from the constant.
        Assert.True(ScrValue.Of(type).Truthiness);
    }

    [Fact]
    public void UndefinedIsFalsyAndAnIntIsUnknowableWithoutItsValue()
    {
        Assert.False(ScrValue.Of(ScrTypeSet.Undefined).Truthiness);
        Assert.Null(ScrValue.Of(ScrTypeSet.Int).Truthiness);
    }

    [Theory]
    [InlineData(0L, false)]
    [InlineData(1L, true)]
    [InlineData(-1L, true)]
    public void AnIntConstantKnowsItsOwnTruthiness(long value, bool expected)
    {
        Assert.Equal(expected, ScrValue.OfConstant(ScrConstant.OfInt(value)).Truthiness);
    }

    [Fact]
    public void AnEmptyStringIsFalsyAndANonEmptyOneIsTruthy()
    {
        Assert.False(ScrValue.OfConstant(ScrConstant.OfString("")).Truthiness);
        Assert.True(ScrValue.OfConstant(ScrConstant.OfString("x")).Truthiness);
    }

    [Fact]
    public void AQuotedLiteralIsJudgedOnWhatIsBetweenTheQuotes()
    {
        // A literal's text is stored AS WRITTEN, so the typer can pass the lexer's already-interned
        // token straight through instead of substringing one per string literal. Truthiness has to
        // see past the quotes for that to be safe — and it does so by position, without allocating.
        Assert.False(ScrValue.OfConstant(ScrConstant.OfString("\"\"")).Truthiness);
        Assert.True(ScrValue.OfConstant(ScrConstant.OfString("\"x\"")).Truthiness);
        Assert.False(ScrValue.OfConstant(ScrConstant.OfString("&\"\"", ScrTypeSet.IString)).Truthiness);
        Assert.True(ScrValue.OfConstant(ScrConstant.OfString("&\"MENU\"", ScrTypeSet.IString)).Truthiness);
    }

    [Fact]
    public void ContentUnquotesOnDemandAndToleratesAnUnquotedValue()
    {
        // Tolerant both ways so a folded concatenation reads back like a literal.
        Assert.Equal("x", ScrConstant.OfString("\"x\"").Content);
        Assert.Equal("MENU", ScrConstant.OfString("&\"MENU\"", ScrTypeSet.IString).Content);
        Assert.Equal("ab", ScrConstant.OfString("ab").Content);
    }

    // --- the dialect fork ---

    [Theory]
    [InlineData(ScrTypeSet.Struct)]
    [InlineData(ScrTypeSet.Entity)]
    [InlineData(ScrTypeSet.Instance)]
    public void StructsEntitiesAndInstancesAliasInEveryDialect(ScrTypeSet type)
    {
        Assert.True(ScrValues.IsByReference(type, arraysByReference: true));
        Assert.True(ScrValues.IsByReference(type, arraysByReference: false));
    }

    [Fact]
    public void AnArrayIsTheOnlyKindWhosePassSemanticsFork()
    {
        // BO3 aliases arrays; every earlier game copies them. This is the single behavioural
        // difference a dialect transpiler has to reason about types for.
        Assert.True(ScrValues.IsByReference(ScrTypeSet.Array, arraysByReference: true));
        Assert.False(ScrValues.IsByReference(ScrTypeSet.Array, arraysByReference: false));
    }

    [Theory]
    [InlineData(ScrTypeSet.Int)]
    [InlineData(ScrTypeSet.Float)]
    [InlineData(ScrTypeSet.Bool)]
    [InlineData(ScrTypeSet.String)]
    [InlineData(ScrTypeSet.Vector)]
    public void ScalarsAreCopiedEverywhere(ScrTypeSet type)
    {
        Assert.False(ScrValues.IsByReference(type, arraysByReference: true));
        Assert.False(ScrValues.IsByReference(type, arraysByReference: false));
    }

    [Fact]
    public void TheAlwaysByReferenceAliasExcludesArray()
    {
        // Getting this wrong would mark every array parameter safe to translate, which is the exact
        // failure the whole lattice exists to prevent.
        Assert.Equal(ScrTypeSet.None, ScrTypeSet.AlwaysByReference & ScrTypeSet.Array);
        Assert.True((ScrTypeSet.AlwaysByReference & ScrTypeSet.Struct) != ScrTypeSet.None);
        Assert.True((ScrTypeSet.AlwaysByReference & ScrTypeSet.Entity) != ScrTypeSet.None);
    }

    // --- assignability ---

    [Fact]
    public void AnIStringIsUsableWhereAStringIsExpectedAndTheReverseHolds()
    {
        Assert.True(ScrValues.IsAssignableTo(ScrTypeSet.IString, ScrTypeSet.String));
        Assert.True(ScrValues.IsAssignableTo(ScrTypeSet.String, ScrTypeSet.IString));
    }

    [Fact]
    public void AnIntIsNotSilentlyAString()
    {
        // GSC will coerce it, but a transpiler emitting the coercion has to be able to see it —
        // so the relation says no rather than hiding it in the encoding the way v1.5 did.
        Assert.False(ScrValues.IsAssignableTo(ScrTypeSet.Int, ScrTypeSet.String));
    }

    [Fact]
    public void AReferenceKindMayAlwaysBeUndefined()
    {
        Assert.True(ScrValues.IsAssignableTo(ScrTypeSet.Undefined, ScrTypeSet.Array));
        Assert.False(ScrValues.IsAssignableTo(ScrTypeSet.Undefined, ScrTypeSet.Int));
    }

    // --- projection onto the coarse lattice the editor speaks ---

    [Theory]
    [InlineData(ScrTypeSet.Int, ScrType.Int)]
    [InlineData(ScrTypeSet.Float, ScrType.Float)]
    [InlineData(ScrTypeSet.Bool, ScrType.Bool)]
    [InlineData(ScrTypeSet.String, ScrType.String)]
    [InlineData(ScrTypeSet.IString, ScrType.IString)]
    [InlineData(ScrTypeSet.Vector, ScrType.Vector)]
    [InlineData(ScrTypeSet.Struct, ScrType.Struct)]
    [InlineData(ScrTypeSet.Array, ScrType.Array)]
    [InlineData(ScrTypeSet.Entity, ScrType.Entity)]
    [InlineData(ScrTypeSet.Function, ScrType.Function)]
    [InlineData(ScrTypeSet.Undefined, ScrType.Undefined)]
    public void AnExactValueProjectsOntoItsScrType(ScrTypeSet types, ScrType expected)
    {
        Assert.Equal(expected, ScrValue.Of(types).ToScrType());
    }

    [Fact]
    public void AUnionProjectsToUnknownWhichIsTheOldBehaviour()
    {
        // The compatibility contract: every existing consumer sees exactly what it saw before.
        Assert.Equal(ScrType.Unknown, ScrValue.Union(ScrValue.Of(ScrTypeSet.Int), ScrValue.Of(ScrTypeSet.String)).ToScrType());
        Assert.Equal(ScrType.Unknown, ScrValue.Unknown.ToScrType());
        Assert.Equal(ScrType.Unknown, ScrValue.Nothing.ToScrType());
    }

    [Fact]
    public void AnIntFloatUnionProjectsToFloatBecauseTheCoarseLatticeWidened()
    {
        // The one union ScrTypes.Join had an answer for. The projection has to reproduce it or a
        // hover that reads "float" today would start reading nothing — while the value underneath
        // still says int|float, which is what a rewriter needs.
        ScrValue joined = ScrValue.Union(ScrValue.Of(ScrTypeSet.Int), ScrValue.Of(ScrTypeSet.Float));

        Assert.Equal(ScrTypeSet.Number, joined.Types);
        Assert.Equal(ScrType.Float, joined.ToScrType());
    }

    [Fact]
    public void RoundTrippingThroughScrTypePreservesEveryMemberItCanCarry()
    {
        foreach ( ScrType type in Enum.GetValues<ScrType>() )
        {
            if ( type == ScrType.Unknown )
            {
                continue;
            }

            Assert.Equal(type, ScrValue.FromScrType(type).ToScrType());
        }
    }

    // --- equality and hashing, which the fixpoint depends on ---

    [Fact]
    public void StructurallyEqualValuesAreEqualAndHashAlike()
    {
        // v1.5's equivalent carried an ImmutableHashSet with default equality, so two identical
        // values compared unequal by reference and any worklist carrying one inside a cycle never
        // converged. That was found end-to-end; this is the direct test it lacked.
        ScrValue left = ScrValue.OfEntity(["player", "actor"]);
        ScrValue right = ScrValue.OfEntity(["player", "actor"]);

        Assert.Equal(left, right);
        Assert.Equal(left.GetHashCode(), right.GetHashCode());
    }

    [Fact]
    public void EntityKindOrderDoesNotAffectEqualityOrHash()
    {
        ScrValue left = ScrValue.OfEntity(["player", "actor"]);
        ScrValue right = ScrValue.OfEntity(["actor", "player"]);

        Assert.Equal(left, right);
        Assert.Equal(left.GetHashCode(), right.GetHashCode());
    }

    [Fact]
    public void DifferentKindsAreNotEqual()
    {
        Assert.NotEqual(ScrValue.OfEntity(["player"]), ScrValue.OfEntity(["actor"]));
        Assert.NotEqual(ScrValue.OfEntity(["player"]), ScrValue.OfEntity(["player", "actor"]));
    }

    [Fact]
    public void ADefaultKindArrayEqualsAnEmptyOne()
    {
        // ImmutableArray's default is not the same object as an empty one, and a value built without
        // kinds must still equal one built with none.
        ScrValue defaulted = ScrValue.Of(ScrTypeSet.Entity);
        ScrValue empty = ScrValue.Of(ScrTypeSet.Entity) with { EntityKinds = ImmutableArray<string>.Empty };

        Assert.Equal(defaulted, empty);
        Assert.Equal(defaulted.GetHashCode(), empty.GetHashCode());
    }

    [Fact]
    public void ValuesDifferingOnlyInImprecisionAreNotEqual()
    {
        // They carry different information for a rewriter, so a fixpoint must not treat them as
        // converged.
        Assert.NotEqual(
            ScrValue.Of(ScrTypeSet.Int),
            ScrValue.Of(ScrTypeSet.Int, ScrImprecision.UntypedParameter));
    }

    [Fact]
    public void ValuesDifferingOnlyInConstantAreNotEqual()
    {
        Assert.NotEqual(ScrValue.OfConstant(ScrConstant.OfInt(4)), ScrValue.OfConstant(ScrConstant.OfInt(8)));
    }

    // --- rendering ---

    [Fact]
    public void ATypeSetRendersReadably()
    {
        Assert.Equal("int", ScrValues.Describe(ScrTypeSet.Int));
        Assert.Equal("int|string", ScrValues.Describe(ScrTypeSet.Int | ScrTypeSet.String));
        Assert.Equal("any", ScrValues.Describe(ScrTypeSet.Universe));
        Assert.Equal("never", ScrValues.Describe(ScrTypeSet.None));
    }

    [Fact]
    public void AValueRendersWithItsConstant()
    {
        Assert.Equal("int 4", ScrValues.Describe(ScrValue.OfConstant(ScrConstant.OfInt(4))));
        Assert.Equal("bool true", ScrValues.Describe(ScrValue.OfConstant(ScrConstant.OfBool(true))));
    }
}
