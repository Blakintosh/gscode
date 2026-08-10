using GSCode.Core;
using GSCode.Core.Symbols;
using GSCode.Core.Text;
using GSCode.Parser;
using GSCode.Parser.Lexing;
using GSCode.Parser.Preprocessing;
using GSCode.Server.Formatting;
using Xunit;

namespace GSCode.Server.Tests.Formatting;

public class GscFormatterTests
{
    private static ParseResult Analyze(string source)
    {
        return ScriptAnalysis.Analyze(
            @"c:\ws\scripts\t.gsc", ScriptLanguage.Gsc, SourceText.From(source), NullInsertProvider.Instance, new NameTable());
    }

    private static string? Format(string source)
    {
        return GscFormatter.Format(Analyze(source));
    }

    /// <summary>The non-trivia token kinds+texts of two sources, for the fidelity gate.</summary>
    private static List<string> SignificantTokens(string source)
    {
        SourceText text = SourceText.From(source);
        List<string> significant = [];
        foreach ( Token token in Lexer.Lex(text).Tokens )
        {
            // Whitespace/newlines are the only thing the formatter may change; comments must survive.
            if ( token.Kind == TokenKind.Whitespace || token.Kind == TokenKind.Newline || token.Kind == TokenKind.EndOfFile )
            {
                continue;
            }

            significant.Add(token.Kind + ":" + token.GetText(text).ToString());
        }

        return significant;
    }

    [Fact]
    public void Format_ProducesAllmanBracesAndPadding()
    {
        string source = "#namespace test;\nfunction  test_func(a,b=5){\nif(!isdefined(a)){a=0;}\nwhile(a<b){a++;wait 0.05;}\nreturn a;}\n";

        string? formatted = Format(source);

        Assert.NotNull(formatted);
        Assert.Contains("function test_func( a, b = 5 )\n{", formatted);
        Assert.Contains("    if ( !isdefined( a ) )\n    {\n        a = 0;\n    }", formatted);
        Assert.Contains("    while ( a < b )\n    {\n        a++;\n        wait 0.05;\n    }", formatted);
        Assert.Contains("    return a;\n}", formatted);
    }

    [Fact]
    public void Format_PreservesNonTriviaTokenStream()
    {
        string source = "#using scripts\\shared\\util;\nfunction f(a){ b = a + 1; level.x = 1; c = arr[0]; d = ( 1, 2, 3 ); }\n";

        string? formatted = Format(source);

        Assert.NotNull(formatted);
        Assert.Equal(SignificantTokens(source), SignificantTokens(formatted));
    }

    [Fact]
    public void Format_KeepsBackslashPathsIntact()
    {
        string source = "#using scripts\\shared\\util_shared;\nfunction f(){}\n";

        string? formatted = Format(source);

        Assert.NotNull(formatted);
        Assert.Contains("scripts\\shared\\util_shared", formatted);
    }

    [Fact]
    public void Format_IsIdempotent()
    {
        string source = "function  f( a ){if(a){b=1;}else{b=2;}return b;}\n";

        string? once = Format(source);
        Assert.NotNull(once);
        string? twice = Format(once);

        Assert.Equal(once, twice);
    }

    [Fact]
    public void Format_KeepsTrailingCommentOnItsLine()
    {
        string source = "function f()\n{\n    a = 1; // note\n}\n";

        string? formatted = Format(source);

        Assert.NotNull(formatted);
        Assert.Contains("a = 1; // note", formatted);
    }

    [Fact]
    public void FormatMinimal_TrimsToTheChangedRegion()
    {
        // Only the "a=0;" line needs spacing; the edit must not span the whole file.
        string source = "function f()\n{\n    a=0;\n}\n";

        GscFormatter.FormatEdit? edit = GscFormatter.FormatMinimal(Analyze(source));

        Assert.NotNull(edit);
        Assert.Equal(2, edit.Value.Range.Start.Line);
        // The reflow only reinserts spacing around '='; the edit is that small.
        Assert.Contains(" = ", edit.Value.NewText);
    }

    [Fact]
    public void FormatMinimal_ReturnsNull_WhenAlreadyFormatted()
    {
        string source = "function f()\n{\n    a = 0;\n}\n";

        GscFormatter.FormatEdit? edit = GscFormatter.FormatMinimal(Analyze(source));

        Assert.Null(edit);
    }

    [Fact]
    public void Format_RefusesFileWithSyntaxErrors()
    {
        // A missing close paren/brace leaves the parser with error diagnostics.
        string source = "function f( \n{\n    a = ;\n";

        string? formatted = Format(source);

        Assert.Null(formatted);
    }

    [Fact]
    public void Format_LeavesNewlineTerminatedDefineIntact()
    {
        string source = "#define MAX 10\nfunction f()\n{\n    a = MAX;\n}\n";

        string? formatted = Format(source);

        Assert.NotNull(formatted);
        // The define must not swallow the following declaration onto its line.
        Assert.Contains("#define MAX 10\n", formatted);
    }
}
