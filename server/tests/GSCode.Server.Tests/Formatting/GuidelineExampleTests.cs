using GSCode.Core;
using GSCode.Core.Symbols;
using GSCode.Core.Text;
using GSCode.Parser;
using GSCode.Parser.Preprocessing;
using GSCode.Server.Formatting;
using Xunit;

namespace GSCode.Server.Tests.Formatting;

/// <summary>
/// The worked example from FORMATTING.md, formatted from a deliberately messy version of itself.
///
/// This is the document's regression test. A formatting guideline that describes behaviour the
/// formatter does not have is worse than none — people follow it, then Format Document undoes
/// their work. Pinning the example means the doc cannot drift from the code silently.
///
/// Every rule the guideline calls settled appears at least once in the input in its WRONG form:
/// spaces for indent, cuddled braces, tight call parens, `if(`, an indented dev block, and a run of
/// blank lines longer than the cap.
/// </summary>
public class GuidelineExampleTests
{
    private static readonly FormatOptions s_tabs = FormatOptions.Default with { UseTabs = true };

    private static string? Format(string source)
    {
        ParseResult result = ScriptAnalysis.Analyze(
            @"c:\ws\scripts\t.gsc", ScriptLanguage.Gsc, SourceText.From(source), NullInsertProvider.Instance, new NameTable());

        return GscFormatter.Format(result, s_tabs);
    }

    [Fact]
    public void TheGuidelineExampleFormatsAsDocumented()
    {
        const string messy = """
            #using scripts\codescripts\struct;
            #insert scripts\shared\shared.gsh;

            #namespace foo;

            class Boo {
                var far;
                constructor() {
                    far = 1;
                }
                function faz( value = 0 ) {
                    far = value;
                }
            }

            function flop() {
                a = [];
                for(i = 0; i < 10; i++) {
                    a[i] = i;
                }



                foreach(key, value in a) {
                    println("key is "+key);
                }
                switch(v) {
                    case 0:
                        v2 = "0";
                        break;
                    default:
                        break;
                }
                /#
                    debug_only_call();
                #/
            }
            """;

        const string expected = "#using scripts\\codescripts\\struct;\n"
            // Directive sorting is on by default, so each group gets a blank line before it.
            + "\n"
            + "#insert scripts\\shared\\shared.gsh;\n"
            + "\n"
            + "#namespace foo;\n"
            + "\n"
            + "class Boo\n"
            + "{\n"
            + "\tvar far;\n"
            + "\tconstructor()\n"
            + "\t{\n"
            + "\t\tfar = 1;\n"
            + "\t}\n"
            + "\tfunction faz( value = 0 )\n"
            + "\t{\n"
            + "\t\tfar = value;\n"
            + "\t}\n"
            + "}\n"
            + "\n"
            + "function flop()\n"
            + "{\n"
            + "\ta = [];\n"
            + "\tfor ( i = 0; i < 10; i++ )\n"
            + "\t{\n"
            + "\t\ta[ i ] = i;\n"
            + "\t}\n"
            // Three blank lines in, two out: the run collapses to MaxBlankLines, not to one.
            + "\n"
            + "\n"
            + "\tforeach ( key, value in a )\n"
            + "\t{\n"
            + "\t\tprintln( \"key is \" + key );\n"
            + "\t}\n"
            + "\tswitch ( v )\n"
            + "\t{\n"
            + "\t\tcase 0:\n"
            + "\t\t\tv2 = \"0\";\n"
            + "\t\t\tbreak;\n"
            + "\t\tdefault:\n"
            + "\t\t\tbreak;\n"
            + "\t}\n"
            + "\t/#\n"
            + "\tdebug_only_call();\n"
            + "\t#/\n"
            + "}\n";

        Assert.Equal(expected, Format(messy));
    }

    [Fact]
    public void FormattingTheDocumentedFormIsAFixedPoint()
    {
        // The example in the guideline is what the formatter emits, so formatting it must be a
        // no-op. Without this the doc could show a form the formatter would immediately rewrite.
        string once = Format("""
            function flop()
            {
            	a = [];
            	for ( i = 0; i < 10; i++ )
            	{
            		a[ i ] = i;
            	}

            	foreach ( key, value in a )
            	{
            		println( "key is " + key );
            	}
            }
            """)!;

        Assert.Equal(once, Format(once));
    }
}
