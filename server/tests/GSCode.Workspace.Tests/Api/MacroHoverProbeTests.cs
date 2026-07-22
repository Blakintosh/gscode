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
/// The macro hover as the handler actually assembles it, from a real invocation in a real file.
///
/// The unit tests around <see cref="MacroExpansionPreview.Render"/> pass arguments in directly and
/// so cannot see where they come FROM — which is exactly what was wrong: a MacroInvocation's range
/// covers the NAME alone (`IS_TRUE`, not `IS_TRUE( v )`), so slicing that range yielded text with
/// no argument list in it and every substitution silently did nothing.
/// </summary>
public class MacroHoverProbeTests
{
    private const string Source =
        "#define IS_TRUE(__a) (isdefined(__a) && __a)\n"
        + "\n"
        + "function f( v )\n"
        + "{\n"
        + "    if ( IS_TRUE( v ) )\n"
        + "    {\n"
        + "    }\n"
        + "}\n";

    private static ParseResult Analyze(string source)
    {
        return ScriptAnalysis.Analyze(
            @"c:\ws\scripts\t.gsc", ScriptLanguage.Gsc, SourceText.From(source), NullInsertProvider.Instance, new NameTable());
    }

    /// <summary>Mirrors the handler: find the invocation, read its arguments, render the body.</summary>
    private static string RenderAtInvocation(string source, string macroName)
    {
        ParseResult result = Analyze(source);

        Assert.True(result.Preprocessed.Macros.TryGet(macroName, out MacroDefinition definition));

        foreach ( MacroInvocation invocation in result.Preprocessed.MacroInvocations )
        {
            if ( invocation.SourceFile is not null || !string.Equals(invocation.Name, macroName, StringComparison.Ordinal) )
            {
                continue;
            }

            int afterName = result.Text.GetOffset(invocation.Range.End);
            ImmutableArray<string> arguments = MacroExpansionPreview.ArgumentsFollowing(result.Text.Text, afterName);

            return MacroExpansionPreview.Render(definition.Body, definition.Parameters ?? [], arguments);
        }

        throw new InvalidOperationException($"no invocation of {macroName} found");
    }

    [Fact]
    public void TheInvocationsRangeCoversTheNameOnly()
    {
        // The fact the fix turns on, pinned so nobody "simplifies" the argument lookup back to
        // slicing the range.
        ParseResult result = Analyze(Source);
        MacroInvocation invocation = Assert.Single(result.Preprocessed.MacroInvocations);

        int start = result.Text.GetOffset(invocation.Range.Start);
        int end = result.Text.GetOffset(invocation.Range.End);

        Assert.Equal("IS_TRUE", result.Text.Text[start..end]);
    }

    [Fact]
    public void HoveringAnInvocationSubstitutesItsArguments()
    {
        string preview = RenderAtInvocation(Source, "IS_TRUE");

        Assert.Contains("v", preview);
        Assert.DoesNotContain("__a", preview);
    }

    [Fact]
    public void SeveralArgumentsAllSubstitute()
    {
        string source =
            "#define PAIR(a, b) use( a, b )\n"
            + "function f()\n"
            + "{\n"
            + "    PAIR( first, second );\n"
            + "}\n";

        string preview = RenderAtInvocation(source, "PAIR");

        Assert.Contains("first", preview);
        Assert.Contains("second", preview);
    }

    [Fact]
    public void AnObjectLikeMacroIsUnaffected()
    {
        // No parentheses follow the name, so there is nothing to read and nothing to substitute.
        string source =
            "#define MAX_PLAYERS 18\n"
            + "function f()\n"
            + "{\n"
            + "    x = MAX_PLAYERS;\n"
            + "}\n";

        Assert.Equal("18", RenderAtInvocation(source, "MAX_PLAYERS"));
    }
}
