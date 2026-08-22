using System.Collections.Immutable;
using GSCode.Core;
using GSCode.Core.Symbols;
using GSCode.Core.Text;
using GSCode.Parser;
using GSCode.Parser.Extraction;
using GSCode.Parser.Preprocessing;
using GSCode.Workspace.Database;
using Xunit;

namespace GSCode.Workspace.Tests.Database;

/// <summary>
/// The workspace half of semantic highlighting: parameters and locals, the two legend slots
/// <see cref="SemanticTokenBuilder"/> cannot fill because <see cref="SymbolKind"/> has no member
/// for either. Emitted from the same per-function walk find-references and rename use, so the
/// names highlighting colours as locals and the names those features answer on cannot drift apart.
/// </summary>
public class LocalSemanticTokensTests
{
    private static ImmutableArray<SemanticToken> Tokens(string source, GameProfile? profile = null)
    {
        ParseResult result = ScriptAnalysis.Analyze(
            @"c:\bo3\share\raw\scripts\main.gsc",
            ScriptLanguage.Gsc,
            SourceText.From(source),
            NullInsertProvider.Instance,
            new NameTable(),
            profile);

        return LocalReferences.SemanticTokens(result, profile);
    }

    private static int CountOf(
        ImmutableArray<SemanticToken> tokens, SemanticTokenType type, int? line = null)
    {
        return tokens.Count(token => token.Type == type && (line is null || token.Line == line));
    }

    [Fact]
    public void AParameterIsClassifiedAtItsDeclarationAndEveryUse()
    {
        //               0         1
        //               0123456789012345678
        string source = "function f( count )\n{\n\tuse( count );\n\treturn count;\n}\n";

        ImmutableArray<SemanticToken> tokens = Tokens(source);

        Assert.Equal(3, CountOf(tokens, SemanticTokenType.Parameter));
        Assert.Contains(tokens, token =>
            token.Line == 0 && token.StartChar == 12 && token.Type == SemanticTokenType.Parameter);
    }

    [Fact]
    public void ALocalIsClassifiedAtItsWritesAndReads()
    {
        string source = "function f()\n{\n\ttotal = 1;\n\ttotal += 2;\n\tuse( total );\n}\n";

        Assert.Equal(3, CountOf(Tokens(source), SemanticTokenType.Variable));
    }

    [Fact]
    public void TheBindingFormsAllCount()
    {
        // A foreach key/value pair and a waittill output are writes, so the names they introduce
        // are locals from that point on — the same rule the reference walk applies.
        string source =
            "function f( list )\n{\n\tforeach ( key, value in list )\n\t{\n\t\tuse( key, value );\n\t}\n"
            + "\tself waittill( \"damage\", attacker );\n\tuse( attacker );\n}\n";

        ImmutableArray<SemanticToken> tokens = Tokens(source);

        Assert.Equal(4, CountOf(tokens, SemanticTokenType.Variable, line: 2)
            + CountOf(tokens, SemanticTokenType.Variable, line: 4));
        Assert.Equal(2, CountOf(tokens, SemanticTokenType.Variable, line: 6)
            + CountOf(tokens, SemanticTokenType.Variable, line: 7));
    }

    [Fact]
    public void ANameNothingBindsStaysUncoloured()
    {
        // A bare read of a name that is never a parameter and never written is undefined, and
        // painting it like a variable would dress up exactly what the unassigned-variable lint
        // reports.
        string source = "function f()\n{\n\tuse( ghost );\n}\n";

        Assert.Empty(Tokens(source));
    }

    [Fact]
    public void AGlobalObjectIsNotALocalHoweverItIsUsed()
    {
        // `level.foo = x` WRITES through level, but level itself is the engine's, and the grammar
        // already colours it as a language variable — repainting it as a plain local would flicker
        // and lose that distinction.
        string source = "function f()\n{\n\tx = 1;\n\tlevel.time = x;\n}\n";

        ImmutableArray<SemanticToken> tokens = Tokens(source);

        Assert.Equal(2, CountOf(tokens, SemanticTokenType.Variable));
        Assert.DoesNotContain(tokens, token => token.Line == 3 && token.StartChar == 1);
    }

    [Fact]
    public void AClassMemberIsNotALocalButARealLocalBesideItIs()
    {
        string source =
            "class Foo\n{\n\tvar id;\n\n\tfunction play()\n\t{\n\t\tid = 1;\n\t\tx = id;\n\t}\n}\n";

        ImmutableArray<SemanticToken> tokens = Tokens(source);

        // Only `x` on line 7 — both uses of `id` belong to the class, not the method.
        SemanticToken only = Assert.Single(tokens, token => token.Type == SemanticTokenType.Variable);
        Assert.Equal(7, only.Line);
        Assert.Equal(2, only.StartChar);
    }

    [Fact]
    public void AnInfinityWardFileScopeConstantIsNotALocal()
    {
        string source = "SPEED = 1.0;\nrun()\n{\n\tSPEED = 2.0;\n\tx = SPEED;\n}\n";

        ImmutableArray<SemanticToken> tokens = Tokens(source, GameProfile.ModernWarfare2);

        // Only `x`. SPEED is readable from every function in the file, so it is not this one's.
        Assert.Single(tokens, token => token.Type == SemanticTokenType.Variable);
    }

    [Fact]
    public void CalleesAndFieldNamesAreLeftToTheReferenceClassification()
    {
        // `helper()` names a function and `.origin` names a field — both are the reference
        // index's to classify, and the body walk never records them as locals in the first place.
        string source = "function f()\n{\n\tx = helper();\n\ty = x.origin;\n}\n";

        ImmutableArray<SemanticToken> tokens = Tokens(source);

        Assert.Equal(3, CountOf(tokens, SemanticTokenType.Variable));
        Assert.DoesNotContain(tokens, token => token.Type == SemanticTokenType.Function);
        Assert.DoesNotContain(tokens, token => token.Type == SemanticTokenType.Property);
    }
}
