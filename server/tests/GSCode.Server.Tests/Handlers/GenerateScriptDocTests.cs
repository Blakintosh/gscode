using GSCode.Core;
using GSCode.Core.Docs;
using GSCode.Core.Symbols;
using GSCode.Server.Handlers;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using Xunit;
using LspRange = OmniSharp.Extensions.LanguageServer.Protocol.Models.Range;

namespace GSCode.Server.Tests.Handlers;

/// <summary>
/// "Generate ScriptDoc block": the gscode/generateScriptDoc request the right-click menu sends, and
/// the block it writes.
///
/// The round trip is what these pin. A generated block is only worth anything if the extractor
/// reads it back as documentation, and the two dialects disagree about what makes that true — BO3
/// has its own <c>/@ … @/</c> delimiter, while everything earlier is an ordinary block comment that
/// counts as documentation only because of a <c>///ScriptDocBegin</c> fence inside it. A block
/// missing that fence looks right and parses to nothing, which is exactly how the `doc` snippet had
/// been wrong.
/// </summary>
public class GenerateScriptDocTests
{
    private const string Path = @"scripts\main.gsc";

    /// <summary>The request as the client sends it, through the handler and the document store.</summary>
    private static async Task<GenerateScriptDocResponse> RequestAtAsync(string source, int line, int character)
    {
        using HandlerWorkspace workspace = await HandlerWorkspace.BuildAsync([new TestFile(Path, source)]);
        workspace.Open(Path);

        GenerateScriptDocHandler handler = new(workspace.Documents);
        return await handler.Handle(
            new GenerateScriptDocParams
            {
                Uri = HandlerWorkspace.Identify(Path).Uri.ToString(),
                Line = line,
                Character = character,
            },
            CancellationToken.None);
    }

    [Fact]
    public async Task AnUndocumentedFunctionGetsABlock()
    {
        string source = "#namespace game;\nfunction give( weapon, count = 1 )\n{\n}\n";

        GenerateScriptDocResponse response = await RequestAtAsync(source, 1, 12);

        Assert.Equal("generated", response.Status);
        Assert.Equal("give", response.Function);
        Assert.Equal(1, response.Line);
        Assert.Contains("\"Name: give( <weapon>, [count] )\"", response.Text, StringComparison.Ordinal);

        // A parameter with a default is one the caller may leave out, which is what OptionalArg means.
        Assert.Contains("\"MandatoryArg: <weapon> : <description>\"", response.Text, StringComparison.Ordinal);
        Assert.Contains("\"OptionalArg: [count] : <description>\"", response.Text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(1, 0)]
    [InlineData(1, 4)]
    [InlineData(1, 9)]
    [InlineData(1, 14)]
    [InlineData(1, 28)]
    [InlineData(2, 0)]
    [InlineData(3, 1)]
    [InlineData(4, 0)]
    public async Task AnywhereInTheFunctionFindsIt(int line, int character)
    {
        // The keyword, the name, a parameter, the body and both braces: the reported bug was a block
        // offered only with the cursor on `function`.
        string source = "#namespace game;\nfunction give( weapon, count = 1 )\n{\n\tx = 1;\n}\n";

        GenerateScriptDocResponse response = await RequestAtAsync(source, line, character);

        Assert.Equal("generated", response.Status);
        Assert.Equal(1, response.Line);
    }

    [Fact]
    public async Task OutsideAnyFunctionThereIsNothingToDocument()
    {
        string source = "#namespace game;\n\nfunction give()\n{\n}\n";

        Assert.Equal("none", (await RequestAtAsync(source, 0, 3)).Status);
    }

    [Fact]
    public async Task AFunctionThatAlreadyHasADocBlockIsReportedAsSuch()
    {
        string source =
            "#namespace game;\n/@\n\"Name: give()\"\n\"Summary: Gives.\"\n@/\nfunction give()\n{\n}\n";

        GenerateScriptDocResponse response = await RequestAtAsync(source, 5, 12);

        Assert.Equal("documented", response.Status);
        Assert.Equal("give", response.Function);
        Assert.Equal("", response.Text);
    }

    [Fact]
    public async Task AMethodsBlockIsIndentedToItsDeclaration()
    {
        string source = "#namespace game;\nclass cScene\n{\n    function play()\n    {\n    }\n}\n";

        GenerateScriptDocResponse response = await RequestAtAsync(source, 3, 14);

        Assert.Equal("generated", response.Status);
        Assert.Equal(3, response.Line);

        // Flush left, a block above a method would be the only thing in the file at column zero.
        foreach ( string blockLine in response.Text.TrimEnd('\n').Split('\n') )
        {
            Assert.StartsWith("    ", blockLine, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task ItIsNotACodeAction()
    {
        // The point of the request: as a refactor it put a lightbulb on every undocumented function.
        string source = "#namespace game;\nfunction give()\n{\n}\n";

        using HandlerWorkspace workspace = await HandlerWorkspace.BuildAsync([new TestFile(Path, source)]);
        workspace.Open(Path);
        DocumentLinter linter = new(
            workspace.Database, workspace.ResolverHolder, workspace.Builtins, workspace.ObjectFields);
        CodeActionHandler handler = new(workspace.Documents, workspace.Navigation, linter, HandlerWorkspace.Selector);

        CommandOrCodeActionContainer? actions = await handler.Handle(
            new CodeActionParams
            {
                TextDocument = HandlerWorkspace.Identify(Path),
                Range = new LspRange(1, 10, 1, 10),
                Context = new CodeActionContext { Diagnostics = new Container<Diagnostic>() },
            },
            CancellationToken.None);

        Assert.DoesNotContain(
            actions ?? [],
            entry => entry.CodeAction?.Title.Contains("ScriptDoc", StringComparison.Ordinal) == true);
    }

    [Theory]
    [InlineData("bo3")]
    [InlineData("cod4")]
    public void TheGeneratedBlockParsesBackAsDocumentation(string shortName)
    {
        using ProfileScope scope = ProfileScope.Use(GameProfile.ByName(shortName));

        string block = ScriptDocTemplate.Render(
            "give",
            [new ParameterSymbol("weapon", ByRef: false, DefaultValueText: "")],
            hasVarargs: false,
            GameProfile.Active.ScriptDocStyle);

        // The pre-BO3 games have no doc delimiter of their own, so the fence is what makes this
        // documentation rather than a comment above a function.
        if ( GameProfile.Active.ScriptDocStyle == ScriptDocStyle.TripleSlash )
        {
            Assert.True(ScriptDocComment.HasTripleSlashFence(block));
        }

        ScriptDocComment parsed = ScriptDocComment.Parse(block);

        Assert.False(parsed.IsNone);
        Assert.Equal("give( <weapon> )", parsed.Name);
        Assert.Equal("weapon", Assert.Single(parsed.Arguments).Name);
    }
}
