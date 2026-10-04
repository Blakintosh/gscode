using GSCode.Server.Handlers;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using Xunit;

namespace GSCode.Server.Tests.Handlers;

/// <summary>
/// Hovering __FUNCTION__ / __FILE__ where they are WRITTEN. By the time extraction runs, the token
/// there is an ordinary String literal — the macro already expanded before parsing — and a string
/// literal's own hover is a deliberate no-op, so without a separate path these two showed nothing at
/// all: the reader looking at "__FUNCTION__" on screen had no way to see what it resolved to.
/// </summary>
public class BuiltinMacroHoverTests
{
    private static async Task<Hover?> HoverAtAsync(string source, int line, int character)
    {
        using HandlerWorkspace workspace = await HandlerWorkspace.BuildAsync(
            [new TestFile(@"scripts\main.gsc", source)]);
        workspace.Open(@"scripts\main.gsc");

        HoverHandler handler = new(
            workspace.Navigation, workspace.Builtins, workspace.ObjectFields, HandlerWorkspace.Selector);

        HoverParams request = new()
        {
            TextDocument = HandlerWorkspace.Identify(@"scripts\main.gsc"),
            Position = new OmniSharp.Extensions.LanguageServer.Protocol.Models.Position(line, character),
        };

        return await handler.Handle(request, CancellationToken.None);
    }

    private static string TextOf(Hover hover)
    {
        return hover.Contents.MarkupContent!.Value;
    }

    [Fact]
    public async Task FunctionBuiltin_HoversWithTheQualifiedName()
    {
        string source = "#namespace spawner;\nfunction spawn_think()\n{\n    x = __FUNCTION__;\n}\n";

        Hover? hover = await HoverAtAsync(source, 3, 9);

        Assert.NotNull(hover);
        Assert.Contains("spawner::spawn_think", TextOf(hover!), StringComparison.Ordinal);
    }

    [Fact]
    public async Task FileBuiltin_HoversWithTheResolvedPath()
    {
        string source = "function run()\n{\n    x = __FILE__;\n}\n";

        Hover? hover = await HoverAtAsync(source, 2, 9);

        Assert.NotNull(hover);
        Assert.Contains(TestPaths.Raw(@"scripts\main.gsc"), TextOf(hover!), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AnOrdinaryStringLiteralStillHasNoHover()
    {
        // The new path must not start claiming every string in the file.
        string source = "function run()\n{\n    x = \"hello\";\n}\n";

        Hover? hover = await HoverAtAsync(source, 2, 9);

        Assert.Null(hover);
    }
}
