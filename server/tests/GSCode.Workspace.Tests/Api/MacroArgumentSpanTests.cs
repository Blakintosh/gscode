using System.Collections.Immutable;
using GSCode.Core;
using GSCode.Core.Symbols;
using GSCode.Core.Text;
using GSCode.Parser;
using GSCode.Parser.Preprocessing;
using GSCode.Workspace.Api;
using Xunit;

namespace GSCode.Workspace.Tests.Api;

/// <summary>
/// The macro parameter-name inlay hints, as the handler computes them: from an invocation's name
/// range to a POSITION for each argument.
///
/// The hint answers with a line and a character, so unlike the hover it is wrong visibly and
/// silently — a label one character off sits inside the argument it names. These pin the spans
/// against the file they came from rather than against a hand-written offset.
/// </summary>
public class MacroArgumentSpanTests
{
    private static ParseResult Analyze(string source)
    {
        return ScriptAnalysis.Analyze(
            @"c:\ws\scripts\t.gsc", ScriptLanguage.Gsc, SourceText.From(source), NullInsertProvider.Instance, new NameTable());
    }

    /// <summary>Mirrors the handler: find the invocation, then the spans of what it was passed.</summary>
    private static ImmutableArray<MacroArgumentSpan> SpansAt(ParseResult result, string macroName)
    {
        foreach ( MacroInvocation invocation in result.Preprocessed.MacroInvocations )
        {
            if ( invocation.SourceFile is not null || !string.Equals(invocation.Name, macroName, StringComparison.Ordinal) )
            {
                continue;
            }

            int afterName = result.Text.GetOffset(invocation.Range.End);
            return MacroExpansionPreview.ArgumentSpansFollowing(result.Text.Text, afterName);
        }

        throw new InvalidOperationException($"no invocation of {macroName} found");
    }

    [Fact]
    public void TheSpanStartsAtTheArgumentsFirstCharacter()
    {
        // The reported shape, and the one the hint is judged by: `__a:` goes immediately before
        // `level`, not before the space or the parenthesis.
        const string source =
            "#define IS_TRUE(__a) (isdefined(__a) && __a)\n"
            + "function f()\n"
            + "{\n"
            + "    if ( IS_TRUE( level.ready ) )\n"
            + "    {\n"
            + "    }\n"
            + "}\n";

        ParseResult result = Analyze(source);
        MacroArgumentSpan span = Assert.Single(SpansAt(result, "IS_TRUE"));

        Assert.Equal("level.ready", result.Text.Text[span.Start..span.End]);

        Position position = result.Text.GetPosition(span.Start);
        Assert.Equal(3, position.Line);
        Assert.Equal(source.Split('\n')[3].IndexOf("level", StringComparison.Ordinal), position.Character);
    }

    [Fact]
    public void EverySpanAgreesWithTheTextTheHoverShows()
    {
        // Hover and hints read the same scan, so a hint can never name an argument the hover
        // reports differently.
        const string source =
            "#define PAIR(a, b) use( a, b )\n"
            + "function f()\n"
            + "{\n"
            + "    PAIR( first( x, y ), things[0, 1] );\n"
            + "}\n";

        ParseResult result = Analyze(source);

        MacroInvocation invocation = Assert.Single(result.Preprocessed.MacroInvocations);
        int afterName = result.Text.GetOffset(invocation.Range.End);

        ImmutableArray<string> texts = MacroExpansionPreview.ArgumentsFollowing(result.Text.Text, afterName);
        ImmutableArray<MacroArgumentSpan> spans = MacroExpansionPreview.ArgumentSpansFollowing(result.Text.Text, afterName);

        // Compared as arrays: ImmutableArray's own Equals is reference equality on the backing
        // store, so two matching arrays would fail the value comparison this is asking for.
        Assert.Equal(["first( x, y )", "things[0, 1]"], texts.ToArray());
        string[] fromSpans = [.. spans.Select(span => result.Text.Text[span.Start..span.End])];
        Assert.Equal(texts.ToArray(), fromSpans);
    }

    [Fact]
    public void AnArgumentOnItsOwnLineKeepsThatLine()
    {
        // A macro call broken across lines is normal in this code, and an offset-to-position
        // conversion that ignored newlines would stack both hints on the first line.
        const string source =
            "#define PAIR(a, b) use( a, b )\n"
            + "function f()\n"
            + "{\n"
            + "    PAIR(\n"
            + "        one,\n"
            + "        two );\n"
            + "}\n";

        ParseResult result = Analyze(source);
        ImmutableArray<MacroArgumentSpan> spans = SpansAt(result, "PAIR");

        Assert.Equal(2, spans.Length);
        Assert.Equal(new Position(4, 8), result.Text.GetPosition(spans[0].Start));
        Assert.Equal(new Position(5, 8), result.Text.GetPosition(spans[1].Start));
    }

    [Fact]
    public void AnObjectLikeMacroHasNothingToLabel()
    {
        const string source =
            "#define MAX_PLAYERS 18\n"
            + "function f()\n"
            + "{\n"
            + "    x = MAX_PLAYERS;\n"
            + "}\n";

        ParseResult result = Analyze(source);

        // The definition carries no parameters, which is what the handler gates on, and the text
        // after the name is not an argument list either.
        MacroInvocation invocation = Assert.Single(result.Preprocessed.MacroInvocations);
        Assert.Null(invocation.Definition.Parameters);
        Assert.Empty(SpansAt(result, "MAX_PLAYERS"));
    }

    [Fact]
    public void AHalfWrittenInvocationLabelsWhatIsThere()
    {
        // Every keystroke reaches this handler, so an unterminated argument list is the normal
        // state rather than an error case.
        ImmutableArray<MacroArgumentSpan> spans =
            MacroExpansionPreview.ArgumentSpansFollowing("PAIR( one, tw", afterName: 4);

        Assert.Equal(2, spans.Length);
        Assert.Equal("one", "PAIR( one, tw"[spans[0].Start..spans[0].End]);
        Assert.Equal("tw", "PAIR( one, tw"[spans[1].Start..spans[1].End]);
    }

    [Fact]
    public void AnEmptyArgumentListYieldsNoSpans()
    {
        Assert.Empty(MacroExpansionPreview.ArgumentSpansFollowing("NONE()", afterName: 4));
        Assert.Empty(MacroExpansionPreview.ArgumentSpansFollowing("NONE(  )", afterName: 4));
    }

    [Fact]
    public void AMacroFollowedByAnUnrelatedParenthesisIsNotAnArgumentList()
    {
        // `MAX_PLAYERS` on its own line, with a call on the next: the scan skips whitespace, so it
        // must not walk past the end of the invocation into whatever follows.
        Assert.Empty(MacroExpansionPreview.ArgumentSpansFollowing("MAX_PLAYERS;\n    f( x );", afterName: 11));
    }
}
