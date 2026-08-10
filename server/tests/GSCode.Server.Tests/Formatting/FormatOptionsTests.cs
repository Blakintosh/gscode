using GSCode.Core;
using GSCode.Core.Symbols;
using GSCode.Core.Text;
using GSCode.Parser;
using GSCode.Parser.Preprocessing;
using GSCode.Server.Formatting;
using Xunit;

namespace GSCode.Server.Tests.Formatting;

/// <summary>
/// The formatter's configurable behaviour. tabSize and insertSpaces arrive in the LSP payload on
/// every formatting request — the editor resolves them per document — and were being dropped, so
/// every file was reindented to four spaces regardless of what the editor had been told.
/// </summary>
public class FormatOptionsTests
{
    private static string Format(string source, FormatOptions? options = null)
    {
        ParseResult result = ScriptAnalysis.Analyze(
            @"c:\ws\scripts\t.gsc", ScriptLanguage.Gsc, SourceText.From(source), NullInsertProvider.Instance, new NameTable());

        return GscFormatter.Format(result, options) ?? throw new InvalidOperationException("formatter refused the input");
    }

    private const string Body = "function f()\n{\nx = 1;\n}\n";

    private static string BodyLine(string formatted)
    {
        return formatted.Split('\n').First(line => line.Contains("x = 1", StringComparison.Ordinal));
    }

    // --- The all-zero struct trap ---

    [Fact]
    public void DefaultIsNotTheZeroValue()
    {
        // A struct's implicit parameterless constructor zeroes every field and wins over the
        // primary constructor's parameter defaults, so `new()` would silently mean no indent, no
        // paren padding and no blank lines. That shipped once; this pins it.
        Assert.NotEqual(default, FormatOptions.Default);

        Assert.Equal(4, FormatOptions.Default.IndentWidth);
        Assert.True(FormatOptions.Default.PadParens);
        Assert.Equal(2, FormatOptions.Default.MaxBlankLines);
    }

    [Fact]
    public void NoOptionsMeansTheDefaults_NotAZeroedStruct()
    {
        // The API takes a nullable rather than a `default` sentinel for the same reason.
        Assert.Equal(Format(Body, FormatOptions.Default), Format(Body));
    }

    // --- Indentation ---

    [Fact]
    public void IndentsWithTabs_WhenTheEditorInsertsTabs()
    {
        string line = BodyLine(Format(Body, FormatOptions.Default with { UseTabs = true }));

        Assert.StartsWith("\t", line, StringComparison.Ordinal);
        Assert.DoesNotContain(" ", line[..1], StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(4)]
    [InlineData(8)]
    public void HonoursTheEditorsTabSize(int width)
    {
        string line = BodyLine(Format(Body, FormatOptions.Default with { UseTabs = false, IndentWidth = width }));

        Assert.Equal(width, line.Length - line.TrimStart(' ').Length);
    }

    [Fact]
    public void ATabIsOneCharacterPerLevel_WhateverTheTabSize()
    {
        // The point of tabs: the width is the reader's business, not the file's.
        string wide = Format(Body, FormatOptions.Default with { UseTabs = true, IndentWidth = 8 });
        string narrow = Format(Body, FormatOptions.Default with { UseTabs = true, IndentWidth = 2 });

        Assert.Equal(wide, narrow);
    }

    // --- Paren padding ---

    [Fact]
    public void PadsControlFlowParens_ByDefault()
    {
        Assert.Contains("if ( a )", Format("function f()\n{\nif (a)\n{\n}\n}\n"), StringComparison.Ordinal);
    }

    [Fact]
    public void OmitsThePadding_WhenTurnedOff()
    {
        string formatted = Format(
            "function f()\n{\nif ( a )\n{\n}\n}\n", FormatOptions.Default with { PadParens = false });

        Assert.Contains("if (a)", formatted, StringComparison.Ordinal);
        Assert.DoesNotContain("if ( a )", formatted, StringComparison.Ordinal);
    }

    [Fact]
    public void EmptyParensStayTight_EitherWay()
    {
        Assert.Contains("f()", Format(Body), StringComparison.Ordinal);
        Assert.Contains("f()", Format(Body, FormatOptions.Default with { PadParens = false }), StringComparison.Ordinal);
    }

    // --- Blank lines ---

    private static int LongestBlankRun(string text)
    {
        int longest = 0;
        int run = 0;

        // TrimEnd first: the formatter always ends the file with a newline, and splitting on it
        // yields a trailing empty entry that is a line terminator, not a blank line.
        foreach ( string line in text.TrimEnd('\n').Split('\n') )
        {
            run = line.Trim().Length == 0 ? run + 1 : 0;
            longest = Math.Max(longest, run);
        }

        return longest;
    }

    [Fact]
    public void KeepsUpToTwoBlankLines_ByDefault()
    {
        // The doc always claimed two while the code capped at one. Two is also what the stock
        // scripts use: 2,477 double blanks against 152 longer runs.
        string source = "function f()\n{\n}\n\n\nfunction g()\n{\n}\n";

        Assert.Equal(2, LongestBlankRun(Format(source)));
    }

    [Fact]
    public void CollapsesLongerRunsToTheCap()
    {
        string source = "function f()\n{\n}\n\n\n\n\n\nfunction g()\n{\n}\n";

        Assert.Equal(2, LongestBlankRun(Format(source)));
        Assert.Equal(1, LongestBlankRun(Format(source, FormatOptions.Default with { MaxBlankLines = 1 })));
    }

    [Fact]
    public void ZeroRemovesBlankLinesEntirely()
    {
        string source = "function f()\n{\n}\n\n\nfunction g()\n{\n}\n";

        Assert.Equal(0, LongestBlankRun(Format(source, FormatOptions.Default with { MaxBlankLines = 0 })));
    }

    // --- The safety properties still hold under every option ---

    [Theory]
    [InlineData(true, true, 0)]
    [InlineData(false, false, 4)]
    [InlineData(true, false, 1)]
    public void FormattingIsIdempotent_UnderAnyOptions(bool useTabs, bool padParens, int maxBlank)
    {
        FormatOptions options = new(IndentWidth: 3, UseTabs: useTabs, PadParens: padParens, MaxBlankLines: maxBlank);
        string source = "function f()\n{\nif (a)\n{\nx = 1;\n}\n\n\n\nelse\ny = 2;\n}\n";

        string once = Format(source, options);
        string twice = Format(once, options);

        Assert.Equal(once, twice);
    }
}
