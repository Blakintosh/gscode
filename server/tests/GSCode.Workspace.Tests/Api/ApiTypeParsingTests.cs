using GSCode.Core;
using GSCode.Core.Symbols;
using GSCode.Workspace.Api;
using Xunit;

namespace GSCode.Workspace.Tests.Api;

/// <summary>
/// The declared types the loader used to flatten to a display string and drop.
///
/// Measured against Black Ops III's own GSC library, which is what the numbers below refer to. The
/// costly one is `isArray`: 114 declarations set it and none of them produced anything, so
/// `ScrTypeSet.Array` was never once produced by a builtin call — and arrays are the only kind
/// whose pass semantics differ between dialects.
/// </summary>
public class ApiTypeParsingTests
{
    private static string ApiDirectory => Path.Combine(AppContext.BaseDirectory, "Api");

    private static BuiltinApi Gsc()
    {
        return ApiLoader.Load(ApiDirectory, ScriptLanguage.Gsc, GameProfile.BlackOps3);
    }

    // --- the parser itself ---

    [Theory]
    [InlineData("int", ScrTypeSet.Int)]
    [InlineData("float", ScrTypeSet.Float)]
    [InlineData("bool", ScrTypeSet.Bool)]
    [InlineData("string", ScrTypeSet.String)]
    [InlineData("istring", ScrTypeSet.IString)]
    [InlineData("vector", ScrTypeSet.Vector)]
    [InlineData("struct", ScrTypeSet.Struct)]
    [InlineData("entity", ScrTypeSet.Entity)]
    [InlineData("function", ScrTypeSet.Function)]
    public void APlainSpellingParsesToItsType(string dataType, ScrTypeSet expected)
    {
        Assert.Equal(expected, ApiLoader.ParseType(dataType, false));
    }

    [Fact]
    public void NumberIsTheIntFloatUnionRatherThanVague()
    {
        // 349 declarations in BO3's GSC library say "number", and every one was discarded as too
        // vague. It is exactly int|float, which this lattice can hold.
        Assert.Equal(ScrTypeSet.Number, ApiLoader.ParseType("number", false));
    }

    [Theory]
    [InlineData("int | string", ScrTypeSet.Int | ScrTypeSet.String)]
    [InlineData("bool | int", ScrTypeSet.Bool | ScrTypeSet.Int)]
    [InlineData("int | number", ScrTypeSet.Int | ScrTypeSet.Number)]
    [InlineData("number | vector", ScrTypeSet.Number | ScrTypeSet.Vector)]
    [InlineData("istring | string", ScrTypeSet.IString | ScrTypeSet.String)]
    [InlineData("int | number | string", ScrTypeSet.Int | ScrTypeSet.Number | ScrTypeSet.String)]
    public void APipeSeparatedUnionParsesToAUnion(string dataType, ScrTypeSet expected)
    {
        // Unions live inside dataType as pipe-separated text, not in the JSON's unionOf array.
        Assert.Equal(expected, ApiLoader.ParseType(dataType, false));
    }

    [Fact]
    public void AnArrayDeclarationIsAnArray()
    {
        // The one that matters most. Never produced before.
        Assert.Equal(ScrTypeSet.Array, ApiLoader.ParseType("any", true));
        Assert.Equal(ScrTypeSet.Array, ApiLoader.ParseType("string", true));
    }

    [Fact]
    public void TheParameterPackIsAnArray()
    {
        Assert.Equal(ScrTypeSet.Array, ApiLoader.ParseType("vararg", false));
    }

    [Theory]
    [InlineData("any")]
    [InlineData("enum")]
    [InlineData("anim")]
    [InlineData("weapon")]
    public void ASpellingTheLatticeCannotExpressReportsNothing(string dataType)
    {
        // None rather than a guess, so the caller can say WHY it does not know. Calling a weapon an
        // Entity would claim more than the data supports.
        Assert.Equal(ScrTypeSet.None, ApiLoader.ParseType(dataType, false));
    }

    [Fact]
    public void AUnionContainingAnUnmappableMemberIsUnknowable()
    {
        // The value could be that member, so the union as a whole cannot be trusted.
        Assert.Equal(ScrTypeSet.None, ApiLoader.ParseType("int | any", false));
    }

    [Fact]
    public void AMissingTypeReportsNothing()
    {
        Assert.Equal(ScrTypeSet.None, ApiLoader.ParseType(null, false));

        // Even marked as an array: 1,241 of BO3's GSC overloads state no return type at all, and
        // those are void rather than arrays of nothing.
        Assert.Equal(ScrTypeSet.None, ApiLoader.ParseType(null, true));
    }

    // --- against the real bundled library ---

    [Fact]
    public void TheBundledLibraryNowProducesArrayReturns()
    {
        // The end-to-end version of the point above: before this, no builtin in any game could
        // yield an array.
        BuiltinApi api = Gsc();
        int arrayReturns = 0;

        foreach ( BuiltinFunction function in api.All )
        {
            if ( function.ReturnTypes == ScrTypeSet.Array )
            {
                arrayReturns++;
            }
        }

        Assert.True(arrayReturns > 0, "no builtin was found returning an array");
    }

    [Fact]
    public void TheBundledLibraryCarriesConfidence()
    {
        // 1,291 high, 684 medium and 80 low in this file, all previously dropped.
        BuiltinApi api = Gsc();
        bool sawHigh = false;
        bool sawLow = false;

        foreach ( BuiltinFunction function in api.All )
        {
            sawHigh |= function.Confidence == BuiltinConfidence.High;
            sawLow |= function.Confidence == BuiltinConfidence.Low;
        }

        Assert.True(sawHigh);
        Assert.True(sawLow);
    }

    [Fact]
    public void TheBundledLibraryMarksVariadicParameters()
    {
        // Read off the `vararg` data type, not the JSON's `variadic` flag — which is present 55
        // times in this file and null in every one of them, so it never carried the fact at all.
        BuiltinApi api = Gsc();
        int variadic = 0;

        foreach ( BuiltinFunction function in api.All )
        {
            foreach ( BuiltinOverload overload in function.Overloads )
            {
                foreach ( BuiltinParameter parameter in overload.Parameters )
                {
                    if ( parameter.IsVariadic )
                    {
                        variadic++;
                    }
                }
            }
        }

        Assert.True(variadic > 0, "no variadic parameter was found");
    }

    [Fact]
    public void DisplayTextIsUnchanged()
    {
        // MarkdownDocRenderer and the signature help both read TypeText, so the display string has
        // to survive the parsing addition untouched.
        BuiltinFunction? spawn = Gsc().Find("SpawnStruct");
        Assert.NotNull(spawn);
        Assert.NotEmpty(spawn.Overloads);
        Assert.Equal("struct", spawn.Overloads[0].ReturnTypeText);
    }
}
