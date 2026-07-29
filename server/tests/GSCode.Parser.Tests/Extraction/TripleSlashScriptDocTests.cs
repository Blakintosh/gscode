using GSCode.Core;
using GSCode.Core.Docs;
using GSCode.Core.Symbols;
using GSCode.Core.Text;
using GSCode.Parser;
using GSCode.Parser.Preprocessing;
using Xunit;

namespace GSCode.Parser.Tests.Extraction;

/// <summary>
/// ScriptDoc on the pre-BO3 games. They have no doc-comment delimiter of their own: a doc block is
/// an ordinary <c>/* … */</c> comment with <c>///ScriptDocBegin</c>/<c>///ScriptDocEnd</c> inside
/// it. Only BO3's <c>/@ … @/</c> token was ever looked for, so on CoD4, WaW, MW2 and BO1 no
/// function had documentation at all — the profile recorded which style each game uses and nothing
/// read it.
///
/// The sample is copied from the shape used throughout CoD4's own common_scripts\utility.gsc,
/// banner lines included, because those are what a naive fence strip would fold into the summary.
/// </summary>
public class TripleSlashScriptDocTests
{
    private static GameProfile Cod4 => GameProfile.ByName("cod4")!;

    private const string RealWorldBlock =
        "/*\n" +
        "=============\n" +
        "///ScriptDocBegin\n" +
        "\"Name: isFlashed()\"\n" +
        "\"Summary: Returns true if the player or an AI is flashed\"\n" +
        "\"Module: Utility\"\n" +
        "\"CallOn: An AI\"\n" +
        "\"SPMP: singleplayer\"\n" +
        "///ScriptDocEnd\n" +
        "=============\n" +
        "*/\n" +
        "isFlashed()\n{\n}\n";

    private static FunctionSymbol FirstFunction(string source, GameProfile profile)
    {
        ParseResult result = ScriptAnalysis.Analyze(
            @"c:\cod4\raw\common_scripts\utility.gsc",
            ScriptLanguage.Gsc,
            SourceText.From(source),
            NullInsertProvider.Instance,
            new NameTable(),
            profile);

        return result.Extraction.Functions[0];
    }

    [Fact]
    public void ACod4DocBlockIsAssociatedWithItsFunction()
    {
        FunctionSymbol function = FirstFunction(RealWorldBlock, Cod4);

        Assert.Equal("isFlashed()", function.Doc.Name);
        Assert.Equal("Returns true if the player or an AI is flashed", function.Doc.Summary);
        Assert.Equal("Utility", function.Doc.Module);
        Assert.Equal("An AI", function.Doc.CallOn);
    }

    [Fact]
    public void TheBannerLinesAroundTheFenceAreNotPartOfTheDoc()
    {
        // `=============` sits outside the fence in stock code. Keeping it would put a row of
        // equals signs on every hover.
        FunctionSymbol function = FirstFunction(RealWorldBlock, Cod4);

        Assert.DoesNotContain("=====", function.Doc.Summary, StringComparison.Ordinal);
        Assert.DoesNotContain("=====", function.Doc.Name, StringComparison.Ordinal);
    }

    [Fact]
    public void AnOrdinaryCommentAboveAFunctionIsNotDocumentation()
    {
        // The fence is the ONLY thing separating the two, so without requiring it every banner and
        // copyright header above a function would become its hover text.
        string source = "/*\n=============\nCopyright, all rights reserved.\n=============\n*/\nfoo()\n{\n}\n";

        FunctionSymbol function = FirstFunction(source, Cod4);

        Assert.Same(ScriptDocComment.None, function.Doc);
    }

    [Fact]
    public void BlackOps3StillUsesItsOwnDelimiters()
    {
        // The other dialect is untouched: /@ @/ is its doc comment, and a plain block comment above
        // a function is not one.
        string source = "/@\n\"Name: foo()\"\n\"Summary: Does a thing.\"\n@/\nfunction foo()\n{\n}\n";

        FunctionSymbol function = FirstFunction(source, GameProfile.BlackOps3);

        Assert.Equal("foo()", function.Doc.Name);
        Assert.Equal("Does a thing.", function.Doc.Summary);
    }

    [Fact]
    public void ABlockCommentIsNotADocOnBlackOps3EvenWithTheFence()
    {
        // BO3 scripts do not write the fence, and treating one as a doc there would be inventing a
        // second syntax the game never had.
        string source = "/*\n///ScriptDocBegin\n\"Name: foo()\"\n///ScriptDocEnd\n*/\nfunction foo()\n{\n}\n";

        FunctionSymbol function = FirstFunction(source, GameProfile.BlackOps3);

        Assert.Same(ScriptDocComment.None, function.Doc);
    }
}
