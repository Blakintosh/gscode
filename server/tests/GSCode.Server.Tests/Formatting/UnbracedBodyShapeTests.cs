using GSCode.Core;
using GSCode.Core.Symbols;
using GSCode.Core.Text;
using GSCode.Parser;
using GSCode.Parser.Preprocessing;
using GSCode.Server.Formatting;
using Xunit;

namespace GSCode.Server.Tests.Formatting;

/// <summary>
/// Every control-flow construct can take a single statement instead of a braced block, and that
/// statement is indented one level even though no brace opened.
///
/// Indentation derives from brace depth, so an unbraced body has nothing of its own to derive from
/// and lands in its header's column unless it is tracked separately. The cases below are the ones
/// that actually differ: each keyword, a nested stack of them, and the terminators that release
/// the whole stack at once.
/// </summary>
public class UnbracedBodyShapeTests
{
    private static readonly FormatOptions s_tabs = FormatOptions.Default with { UseTabs = true };

    private static string Format(string source)
    {
        ParseResult result = ScriptAnalysis.Analyze(
            @"c:\ws\scripts\t.gsc", ScriptLanguage.Gsc, SourceText.From(source), NullInsertProvider.Instance, new NameTable());

        return GscFormatter.Format(result, s_tabs)!;
    }

    private static void RoundTrips(string source)
    {
        string expected = source.ReplaceLineEndings("\n") + "\n";
        Assert.Equal(expected, Format(source));

        // And it must not drift on a second pass.
        Assert.Equal(expected, Format(expected));
    }

    [Fact]
    public void ForeachWithNoBraces()
    {
        // The reported shape.
        RoundTrips("""
            function f( bar )
            {
            	foreach ( foo in bar )
            		value = "lol";
            }
            """);
    }

    [Fact]
    public void EveryKeywordIndentsItsUnbracedBody()
    {
        RoundTrips("""
            function f( bar, n )
            {
            	if ( n )
            		a();
            	while ( n )
            		b();
            	for ( i = 0; i < n; i++ )
            		c();
            	foreach ( foo in bar )
            		d();
            }
            """);
    }

    [Fact]
    public void NestedUnbracedHeadersStack()
    {
        RoundTrips("""
            function f( bar, n )
            {
            	if ( n )
            		foreach ( foo in bar )
            			if ( foo )
            				deep();
            }
            """);
    }

    [Fact]
    public void TheWholeStackIsReleasedByOneTerminator()
    {
        // All three bodies end at the same `;`, so the next statement returns to the top level.
        RoundTrips("""
            function f( bar, n )
            {
            	if ( n )
            		foreach ( foo in bar )
            			deep();
            	after();
            }
            """);
    }

    [Fact]
    public void AnUnbracedBodyFollowedByABracedOne()
    {
        RoundTrips("""
            function f( bar, n )
            {
            	if ( n )
            		a();

            	foreach ( foo in bar )
            	{
            		b();
            	}
            }
            """);
    }

    [Fact]
    public void ABracedBodyIsNotDoubleIndented()
    {
        // The brace already carries a level; the tracker must not add a second.
        RoundTrips("""
            function f( bar )
            {
            	foreach ( foo in bar )
            	{
            		value = "lol";
            	}
            }
            """);
    }

    [Fact]
    public void AFlattenedUnbracedBodyIsReIndented()
    {
        // The bug this tracking exists for: with indentation taken from brace depth alone, the
        // body landed in its header's own column.
        Assert.Equal(
            "function f( bar )\n{\n\tforeach ( foo in bar )\n\t\tvalue = \"lol\";\n}\n",
            Format("function f( bar )\n{\nforeach ( foo in bar )\nvalue = \"lol\";\n}\n"));
    }

    [Fact]
    public void ADoWhileBodyIsIndented()
    {
        RoundTrips("""
            function f( n )
            {
            	do
            		a();
            	while ( n );
            }
            """);
    }
}
