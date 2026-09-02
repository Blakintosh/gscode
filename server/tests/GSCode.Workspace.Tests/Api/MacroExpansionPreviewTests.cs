using System.Collections.Immutable;
using GSCode.Core;
using GSCode.Core.Symbols;
using GSCode.Core.Text;
using GSCode.Parser;
using GSCode.Parser.Preprocessing;
using GSCode.Workspace.Api;
using GSCode.Workspace.Database;
using Xunit;

namespace GSCode.Workspace.Tests.Api;

/// <summary>
/// A macro's hover should say what it expands to — the thing a caller of `IS_TRUE` or
/// `NEW_STATE` actually wants. The preview is rebuilt from the token stream, so line
/// continuations collapse and the reader sees one readable line.
/// </summary>
public class MacroExpansionPreviewTests
{
    private static ImmutableArray<PToken> BodyOf(string source, string macroName)
    {
        ParseResult result = ScriptAnalysis.Analyze(
            @"c:\ws\scripts\t.gsc", ScriptLanguage.Gsc, SourceText.From(source), NullInsertProvider.Instance, new NameTable());

        Assert.True(result.Preprocessed.Macros.TryGet(macroName, out MacroDefinition definition));
        return definition.Body;
    }

    [Fact]
    public void FunctionLikeMacro_RendersItsBody()
    {
        // The reported IS_TRUE shape. With no call site there is nothing to substitute, so the
        // parameter names stand — which is what hovering the DEFINITION should show.
        string preview = MacroExpansionPreview.Render(
            BodyOf("#define IS_TRUE(__a) (isdefined(__a) && __a)\n", "IS_TRUE"));

        Assert.Contains("isdefined", preview);
        Assert.Contains("__a", preview);
    }

    // --- Substituting the call site's arguments ---

    private static ImmutableArray<string> ParametersOf(string source, string macroName)
    {
        ParseResult result = ScriptAnalysis.Analyze(
            @"c:\ws\scripts\t.gsc", ScriptLanguage.Gsc, SourceText.From(source), NullInsertProvider.Instance, new NameTable());

        Assert.True(result.Preprocessed.Macros.TryGet(macroName, out MacroDefinition definition));
        return definition.Parameters ?? [];
    }

    [Fact]
    public void ArgumentsReplaceTheParameterNames()
    {
        // The reported want: hovering `IS_TRUE( foo )` should read what it expands to, rather
        // than the macro's own parameter names read back at you.
        const string source = "#define IS_TRUE(__a) (isdefined(__a) && __a)\n";

        string preview = MacroExpansionPreview.Render(
            BodyOf(source, "IS_TRUE"), ParametersOf(source, "IS_TRUE"), ["foo"]);

        Assert.Contains("foo", preview);
        Assert.DoesNotContain("__a", preview);
    }

    [Fact]
    public void SubstitutionIsPerTokenNotTextual()
    {
        // A parameter named `a` replaced textually would also rewrite the `a` inside `value`.
        const string source = "#define USE(a) helper( a, value )\n";

        string preview = MacroExpansionPreview.Render(BodyOf(source, "USE"), ParametersOf(source, "USE"), ["x"]);

        Assert.Contains("value", preview);
        Assert.Contains("x", preview);
    }

    [Fact]
    public void UnsuppliedParametersKeepTheirNames()
    {
        // A half-written invocation should show what is actually known.
        const string source = "#define PAIR(a, b) use( a, b )\n";

        string preview = MacroExpansionPreview.Render(BodyOf(source, "PAIR"), ParametersOf(source, "PAIR"), ["first"]);

        Assert.Contains("first", preview);
        Assert.Contains("b", preview);
    }

    [Theory]
    [InlineData("IS_TRUE( foo )", new[] { "foo" })]
    [InlineData("PAIR( a, b )", new[] { "a", "b" })]
    [InlineData("OUTER( inner( a, b ), c )", new[] { "inner( a, b )", "c" })]
    [InlineData("INDEXED( things[0, 1], c )", new[] { "things[0, 1]", "c" })]
    public void ArgumentsAreSplitOnTopLevelCommas(string invocation, string[] expected)
    {
        // Nesting matters: a comma inside a nested call belongs to that call, not to this one.
        Assert.Equal(expected, MacroExpansionPreview.ParseArguments(invocation));
    }

    [Fact]
    public void AnObjectLikeMacroHasNoArgumentList()
    {
        Assert.Empty(MacroExpansionPreview.ParseArguments("MAX_PLAYERS"));
    }

    [Fact]
    public void ObjectLikeMacro_RendersItsValue()
    {
        string preview = MacroExpansionPreview.Render(BodyOf("#define MAX_PLAYERS 18\n", "MAX_PLAYERS"));

        Assert.Equal("18", preview);
    }

    [Fact]
    public void MultiLineMacro_KeepsItsLinesAndDropsItsBackslashes()
    {
        // The reported NEW_STATE shape: a multi-statement body joined by backslashes. The body
        // opens on the #define's own line, so the base column is out at that opening statement and
        // the continuations underneath it clamp to the left margin rather than going negative.
        string source = "#define NEW_STATE(__state) flagsys::clear( \"ready\" ); \\\n"
            + "    _str_state = __state; \\\n"
            + "    self notify( __state );\n";

        string preview = MacroExpansionPreview.Render(BodyOf(source, "NEW_STATE"));

        Assert.DoesNotContain("\\", preview);
        Assert.Equal(
            // `notify` lexes as a keyword rather than an identifier, so the spacing heuristic does
            // not hug its parenthesis. That is the heuristic's own pre-existing answer; what this
            // test is about is the three lines it is spread over.
            "flagsys::clear(\"ready\");\n_str_state = __state;\nself notify (__state);",
            preview);
    }

    /// <summary>
    /// The reported REGISTER_SYSTEM shape, and the reason indentation is relative: the whole body
    /// sits one level in from a `#define` at column 0, so rendering the author's ABSOLUTE columns
    /// would push every line of every macro to the right inside the code fence.
    /// </summary>
    [Fact]
    public void MultiLineMacro_IndentsRelativeToItsFirstLine()
    {
        string source = "#define REGISTER_SYSTEM(__sys,__func,__reqs) \\\n"
            + "    function autoexec __init__system__() { \\\n"
            + "        system::register(__sys,__func,undefined,__reqs); \\\n"
            + "    }\n";

        string preview = MacroExpansionPreview.Render(BodyOf(source, "REGISTER_SYSTEM"));

        Assert.Equal(
            "function autoexec __init__system__() {\n"
            + "    system::register(__sys, __func, undefined, __reqs);\n"
            + "}",
            preview);
    }

    /// <summary>
    /// The same header written with TABS. A tab is one character in a token's range, so rendering
    /// the difference between two columns gave a tab-indented body a one-space step — the structure
    /// present and unreadable. Levels are ranked instead, so how the file was indented does not
    /// reach the preview.
    /// </summary>
    [Fact]
    public void MultiLineMacro_RendersTabsAndSpacesTheSame()
    {
        string spaces = "#define REGISTER_SYSTEM(__sys,__func,__reqs) \\\n"
            + "    function autoexec __init__system__() { \\\n"
            + "        system::register(__sys,__func,undefined,__reqs); \\\n"
            + "    }\n";
        string tabs = "#define REGISTER_SYSTEM(__sys,__func,__reqs) \\\n"
            + "\tfunction autoexec __init__system__() { \\\n"
            + "\t\tsystem::register(__sys,__func,undefined,__reqs); \\\n"
            + "\t}\n";

        Assert.Equal(
            MacroExpansionPreview.Render(BodyOf(spaces, "REGISTER_SYSTEM")),
            MacroExpansionPreview.Render(BodyOf(tabs, "REGISTER_SYSTEM")));
    }

    [Fact]
    public void Spacing_KeepsCallsAndSeparatorsReadable()
    {
        string preview = MacroExpansionPreview.Render(BodyOf("#define CALL_IT helper( a, b );\n", "CALL_IT"));

        // Not `helper ( a , b ) ;`
        Assert.Contains("helper(", preview);
        Assert.DoesNotContain(" ;", preview);
        Assert.DoesNotContain(" ,", preview);
    }

    [Fact]
    public void LongBody_IsTruncated()
    {
        string body = string.Join(" ", Enumerable.Repeat("some_long_identifier_name", 40));
        string preview = MacroExpansionPreview.Render(BodyOf("#define BIG " + body + "\n", "BIG"));

        Assert.True(preview.Length <= MacroExpansionPreview.MaxLength + 4, "preview should be truncated");
        Assert.EndsWith("…", preview, StringComparison.Ordinal);
    }

    [Fact]
    public void EmptyBody_RendersNothing()
    {
        // A bare `#define FLAG` guard has nothing to preview.
        Assert.Equal("", MacroExpansionPreview.Render(BodyOf("#define FEATURE_FLAG\n", "FEATURE_FLAG")));
    }

    [Fact]
    public void RenderMacro_ShowsTheExpansionInsideTheDefineBlock()
    {
        MacroRecord macro = new("IS_TRUE", true, ["__a"], TextRange.Empty, "");

        string markdown = MarkdownDocRenderer.RenderMacro(macro, "(isdefined(__a) && __a)");

        Assert.Contains("#define IS_TRUE(__a)", markdown);
        Assert.Contains("(isdefined(__a) && __a)", markdown);
    }

    [Fact]
    public void RenderMacro_WithoutAnExpansion_IsUnchanged()
    {
        // The default keeps every existing caller rendering exactly as before.
        MacroRecord macro = new("FEATURE_FLAG", false, [], TextRange.Empty, "");

        Assert.Equal("```gsc\n#define FEATURE_FLAG\n```", MarkdownDocRenderer.RenderMacro(macro));
    }
}
