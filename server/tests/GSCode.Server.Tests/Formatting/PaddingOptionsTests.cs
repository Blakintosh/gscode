using GSCode.Core;
using GSCode.Core.Symbols;
using GSCode.Core.Text;
using GSCode.Parser;
using GSCode.Parser.Preprocessing;
using GSCode.Server.Formatting;
using Xunit;

namespace GSCode.Server.Tests.Formatting;

/// <summary>
/// The three padding knobs act on three different bracket kinds and nothing else: `PadParens` on
/// control-flow and grouping parentheses, `PadCallParens` on call and declaration parentheses,
/// `PadBrackets` on subscripts and array literals. Each may be off while the others stay on, which
/// is how the common hand-written `if ( foo(a[i]) )` style is reached.
/// </summary>
public class PaddingOptionsTests
{
    private static string Format(string statement, FormatOptions options)
    {
        ParseResult result = ScriptAnalysis.Analyze(
            @"c:\ws\scripts\t.gsc",
            ScriptLanguage.Gsc,
            SourceText.From("function f( a, i )\n{\n\t" + statement + "\n}\n"),
            NullInsertProvider.Instance,
            new NameTable());

        string? formatted = GscFormatter.Format(result, options with { UseTabs = true });
        Assert.NotNull(formatted);
        return formatted;
    }

    private const string Input = "if (IS_EQUAL(GetDvarInt(a[i], 0), 3)) x = (a) + foo();";

    [Fact]
    public void EverythingPaddedByDefault()
    {
        Assert.Contains(
            "if ( IS_EQUAL( GetDvarInt( a[ i ], 0 ), 3 ) ) x = ( a ) + foo();",
            Format(Input, FormatOptions.Default), StringComparison.Ordinal);
    }

    [Fact]
    public void TightCallsKeepPaddedConditions()
    {
        Assert.Contains(
            "if ( IS_EQUAL(GetDvarInt(a[ i ], 0), 3) ) x = ( a ) + foo();",
            Format(Input, FormatOptions.Default with { PadCallParens = false }), StringComparison.Ordinal);
    }

    [Fact]
    public void TightBracketsLeaveParensAlone()
    {
        Assert.Contains(
            "if ( IS_EQUAL( GetDvarInt( a[i], 0 ), 3 ) ) x = ( a ) + foo();",
            Format(Input, FormatOptions.Default with { PadBrackets = false }), StringComparison.Ordinal);
    }

    [Fact]
    public void EverythingTight()
    {
        Assert.Contains(
            "if (IS_EQUAL(GetDvarInt(a[i], 0), 3)) x = (a) + foo();",
            Format(Input, FormatOptions.Default with { PadParens = false, PadCallParens = false, PadBrackets = false }),
            StringComparison.Ordinal);
    }

    [Fact]
    public void TightConditionsKeepPaddedCalls()
    {
        Assert.Contains(
            "if (IS_EQUAL( GetDvarInt( a[ i ], 0 ), 3 )) x = (a) + foo();",
            Format(Input, FormatOptions.Default with { PadParens = false }), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("[[ptr]]();", "[[ptr]]();")]
    [InlineData("a = [];", "a = [];")]
    [InlineData("foo();", "foo();")]
    public void AdjacentAndEmptyBracketsStayTightWhenUnpadded(string input, string expected)
    {
        Assert.Contains(
            expected,
            Format(input, FormatOptions.Default with { PadBrackets = false, PadCallParens = false }),
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("if (a) x = 1;", "if( a ) x = 1;")]
    [InlineData("while (a) x = 1;", "while( a ) x = 1;")]
    [InlineData("for (i = 0; i < 1; i++) x = 1;", "for( i = 0; i < 1; i++ ) x = 1;")]
    [InlineData("foreach (a in i) x = 1;", "foreach( a in i ) x = 1;")]
    [InlineData("switch (a) { case 1: break; }", "switch( a )")]
    public void TheKeywordSpaceIsItsOwnSetting(string input, string expected)
    {
        Assert.Contains(
            expected,
            Format(input, FormatOptions.Default with { SpaceBeforeControlParen = false }), StringComparison.Ordinal);
    }

    [Fact]
    public void TightKeywordAndTightInteriorCombine()
    {
        Assert.Contains(
            "if(a) x = 1;",
            Format("if ( a ) x = 1;", FormatOptions.Default with { SpaceBeforeControlParen = false, PadParens = false }),
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("return (a);", "return ( a );")]
    [InlineData("x = isdefined(a);", "x = isdefined( a );")]
    [InlineData("x = foo(a);", "x = foo( a );")]
    public void OnlyControlFlowKeywordsAreAffected(string input, string expected)
    {
        Assert.Contains(
            expected,
            Format(input, FormatOptions.Default with { SpaceBeforeControlParen = false }), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("for (;;) { break; }", "for ( ;; )")]
    [InlineData("for ( ;; ) { break; }", "for ( ;; )")]
    [InlineData("for (i = 0;;) { break; }", "for ( i = 0;; )")]
    [InlineData("for (;; i++) { break; }", "for ( ;; i++ )")]
    [InlineData("for (i = 0; i < 1;) { break; }", "for ( i = 0; i < 1; )")]
    [InlineData("for ( ;;\n\t) { break; }", "for ( ;; )")]
    [InlineData("for ( i = 0; i < 1;\n\t) { break; }", "for ( i = 0; i < 1; )")]
    public void AnEmptyForClauseStaysOnItsLine(string input, string expected)
    {
        // Reported: `for ( ;;` followed by `)` on a line of its own, whatever the settings.
        string formatted = Format(input, FormatOptions.Default);
        Assert.Contains(expected, formatted, StringComparison.Ordinal);
        Assert.DoesNotContain(";\n", formatted[..formatted.IndexOf('{', formatted.IndexOf("for"))], StringComparison.Ordinal);
    }

    [Fact]
    public void DeclarationParensFollowTheCallSetting()
    {
        Assert.StartsWith(
            "function f(a, i)\n",
            Format("x = 1;", FormatOptions.Default with { PadCallParens = false }), StringComparison.Ordinal);
    }
}
