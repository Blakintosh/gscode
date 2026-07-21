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
        // The reported IS_TRUE shape.
        string preview = MacroExpansionPreview.Render(
            BodyOf("#define IS_TRUE(__a) (isdefined(__a) && __a)\n", "IS_TRUE"));

        Assert.Contains("isdefined", preview);
        Assert.Contains("__a", preview);
    }

    [Fact]
    public void ObjectLikeMacro_RendersItsValue()
    {
        string preview = MacroExpansionPreview.Render(BodyOf("#define MAX_PLAYERS 18\n", "MAX_PLAYERS"));

        Assert.Equal("18", preview);
    }

    [Fact]
    public void MultiLineMacro_CollapsesItsContinuations()
    {
        // The reported NEW_STATE shape: a multi-statement body joined by backslashes.
        string source = "#define NEW_STATE(__state) flagsys::clear( \"ready\" ); \\\n"
            + "    _str_state = __state; \\\n"
            + "    self notify( __state );\n";

        string preview = MacroExpansionPreview.Render(BodyOf(source, "NEW_STATE"));

        // One line, no backslashes, and the statements still separated.
        Assert.DoesNotContain("\\", preview);
        Assert.DoesNotContain("\n", preview);
        Assert.Contains("flagsys::clear", preview);
        Assert.Contains("notify", preview);
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
