using GSCode.Core;
using GSCode.Core.Symbols;
using GSCode.Core.Text;
using GSCode.Parser;
using GSCode.Parser.Preprocessing;
using GSCode.Server.Formatting;
using Xunit;

namespace GSCode.Server.Tests.Formatting;

/// <summary>
/// `else if` is one chained construct, not an `else` whose body happens to be an `if`.
///
/// Treating it as a body left an indent level owed that the `if`'s own `{` never released, so a
/// braced `else if ( x ) { … }` came out a level too deep with its closing brace misaligned — and
/// because the tracker resets on `}` rather than decrementing, the damage compounded across the
/// nested blocks inside it.
/// </summary>
public class ElseIfChainTests
{
    private static readonly FormatOptions s_tabs = FormatOptions.Default with { UseTabs = true };

    private static string? Format(string source)
    {
        ParseResult result = ScriptAnalysis.Analyze(
            @"c:\ws\scripts\t.gsc", ScriptLanguage.Gsc, SourceText.From(source), NullInsertProvider.Instance, new NameTable());

        return GscFormatter.Format(result, s_tabs);
    }

    [Fact]
    public void ABracedElseIfKeepsItsLevel()
    {
        // The reported bug, verbatim: the `{`, the foreach inside it and both closing braces all
        // drifted a level right.
        const string source = """
            function f( arg1, entities, func )
            {
            	if ( arg1 )
            	{
            		x = 1;
            	}
            	else if ( isdefined( arg1 ) )
            	{
            		foreach ( ent in entities )
            		{
            			ent thread [[ func ]]( arg1 );
            		}
            	}
            }
            """;

        Assert.Equal(source.ReplaceLineEndings("\n") + "\n", Format(source));
    }

    [Fact]
    public void ALongElseIfChainStaysFlat()
    {
        // Every arm sits at the same level; nothing accumulates down the chain.
        const string source = """
            function f( v )
            {
            	if ( v == 0 )
            	{
            		a();
            	}
            	else if ( v == 1 )
            	{
            		b();
            	}
            	else if ( v == 2 )
            	{
            		c();
            	}
            	else
            	{
            		d();
            	}
            }
            """;

        Assert.Equal(source.ReplaceLineEndings("\n") + "\n", Format(source));
    }

    [Fact]
    public void AnUnbracedElseIfStillIndentsItsBody()
    {
        // The chain itself is flat, but the statement under it is a body and gets its level.
        const string source = """
            function f( v )
            {
            	if ( v == 0 )
            		a();
            	else if ( v == 1 )
            		b();
            }
            """;

        Assert.Equal(source.ReplaceLineEndings("\n") + "\n", Format(source));
    }

    [Fact]
    public void AnUnbracedElseWhoseBodyIsNotAnIfStillIndents()
    {
        // `else while` is a genuine unbraced body, not a chain, so it must keep its level. This is
        // the case the `else if` exemption must not swallow.
        const string source = """
            function f( v )
            {
            	if ( v )
            		a();
            	else
            		while ( v )
            			b();
            }
            """;

        Assert.Equal(source.ReplaceLineEndings("\n") + "\n", Format(source));
    }

    [Fact]
    public void FormattingIsIdempotent()
    {
        const string messy = """
            function f( v, entities, func )
            {
            if ( v ) { a(); }
            else if ( isdefined( v ) ) {
            foreach ( ent in entities ) { ent thread [[func]]( v ); }
            }
            }
            """;

        string once = Format(messy)!;
        Assert.Equal(once, Format(once));
    }
}
