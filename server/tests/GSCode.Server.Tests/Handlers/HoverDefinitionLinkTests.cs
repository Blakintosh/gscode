using GSCode.Server.Handlers;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using Xunit;

namespace GSCode.Server.Tests.Handlers;

/// <summary>
/// The go-to-definition link a hover carries under its signature, for the three kinds that have one
/// place to point at: script functions, classes and macros.
///
/// Driven through the handler over a real indexed workspace, because the link is built from the
/// RESOLVED declaration — which only exists once real indexing, preprocessing and parsing have
/// produced it — and its whole point is naming the other file. The macro case needs the real insert
/// provider rather than the null one: its answer is the <c>.gsh</c> an <c>#insert</c> pulled it
/// from, and without the provider reading that header IS_TRUE is never a macro at all.
///
/// A builtin is the control. The engine declares it and there is nothing to open, so its hover must
/// not grow a link that leads nowhere.
/// </summary>
public sealed class HoverDefinitionLinkTests
{
    // "function helper()" is line 2 and its name starts at column 10; "class Widget" is line 6,
    // column 7. Both 1-based, as the link spells them.
    private const string LibSource =
        "#namespace lib;\nfunction helper()\n{\n}\n\nclass Widget\n{\n}\n";

    // "#define IS_TRUE" puts the name on line 1 at column 9.
    private const string HeaderSource = "#define IS_TRUE( __a ) (isdefined( __a ) && __a)\n";

    private const string CallerSource =
        "#using scripts\\lib;\n"
        + "#insert scripts\\defs.gsh;\n"
        + "#namespace caller;\n"
        + "function run()\n"
        + "{\n"
        + "    lib::helper();\n"
        + "    a = new Widget();\n"
        + "    b = IS_TRUE( 1 );\n"
        + "    getentarray();\n"
        + "}\n";

    private static async Task<string?> HoverAtAsync(int line, int character)
    {
        using HandlerWorkspace workspace = await HandlerWorkspace.BuildAsync(
        [
            new TestFile(@"scripts\lib.gsc", LibSource),
            new TestFile(@"scripts\defs.gsh", HeaderSource),
            new TestFile(@"scripts\caller.gsc", CallerSource),
        ]);
        workspace.Open(@"scripts\caller.gsc");

        HoverHandler handler = new(
            workspace.Navigation, workspace.Builtins, workspace.ObjectFields, HandlerWorkspace.Selector);

        HoverParams request = new()
        {
            TextDocument = HandlerWorkspace.Identify(@"scripts\caller.gsc"),
            Position = new Position(line, character),
        };

        Hover? hover = await handler.Handle(request, CancellationToken.None);
        return hover?.Contents.MarkupContent?.Value;
    }

    [Fact]
    public async Task AScriptFunctionHoverLinksToItsDeclaration()
    {
        string? markdown = await HoverAtAsync(5, 11);

        Assert.NotNull(markdown);

        // Script-relative label with the declaration's line, and a caret fragment the editor reads
        // as line 2, column 10 — both 1-based, where the parser's range is 0-based.
        Assert.Contains(@"[`scripts\lib.gsc:2`](", markdown!, StringComparison.Ordinal);
        Assert.Contains("#L2,10)", markdown!, StringComparison.Ordinal);

        // Under the signature, above the documentation.
        Assert.True(
            markdown!.IndexOf("```", StringComparison.Ordinal)
            < markdown.IndexOf("[`scripts", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AClassHoverLinksToItsDeclaration()
    {
        string? markdown = await HoverAtAsync(6, 14);

        Assert.NotNull(markdown);
        Assert.Contains(@"[`scripts\lib.gsc:6`](", markdown!, StringComparison.Ordinal);
        Assert.Contains("#L6,7)", markdown!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AMacroHoverLinksToTheHeaderThatDefinesIt()
    {
        string? markdown = await HoverAtAsync(7, 10);

        Assert.NotNull(markdown);

        // The .gsh, not the file doing the #insert. That distinction is the whole point: the
        // definition's range is a true position in the header and nowhere else.
        Assert.Contains(@"[`scripts\defs.gsh:1`](", markdown!, StringComparison.Ordinal);
        Assert.Contains("#L1,9)", markdown!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ABuiltinHoverCarriesNoLink()
    {
        string? markdown = await HoverAtAsync(8, 8);

        Assert.NotNull(markdown);
        Assert.DoesNotContain("#L", markdown!, StringComparison.Ordinal);
    }
}
