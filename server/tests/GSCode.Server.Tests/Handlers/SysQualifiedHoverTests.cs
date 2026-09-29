using GSCode.Server.Handlers;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using Xunit;

namespace GSCode.Server.Tests.Handlers;

/// <summary>
/// A hover on <c>sys::name()</c> describes the ENGINE function, whatever script namespaces also
/// declare that name.
///
/// Extraction keys <c>sys::spawnSpectator()</c> as the namespace-less builtin key, and on a namespace
/// dialect nothing else produces that key — an unqualified call is keyed under the file's own
/// namespace. Hover resolved it through <c>MethodResolution.ResolveCall</c>, whose fallback reads a
/// null namespace as "any namespace", so the explicit builtin form picked up
/// <c>zm::spawnSpectator</c> from <c>scripts\zm\_zm.gsc</c> in a file that neither is in namespace
/// <c>zm</c> nor imports it.
///
/// The qualified <c>zm::</c> call is the control: the fix must not stop a namespace-qualified call
/// from reaching the script function it names.
/// </summary>
public sealed class SysQualifiedHoverTests
{
    // A stand-in for the shipped _zm.gsc, which declares its own spawnSpectator() taking nothing
    // alongside BO3's engine SpawnSpectator( origin, angles ).
    private const string ZmSource = "#namespace zm;\nfunction spawnSpectator()\n{\n}\n";

    // No #using: the reported file reached _zm.gsc through nothing at all. "    self thread " is 16
    // characters, so the names start at column 21 (after "sys::") and column 20 (after "zm::").
    private const string CallerSource =
        "#namespace gscode;\n"
        + "function respawn()\n"
        + "{\n"
        + "    self thread sys::spawnSpectator();\n"
        + "    self thread zm::spawnSpectator();\n"
        + "}\n";

    private static async Task<string?> HoverAtAsync(int line, int character)
    {
        using HandlerWorkspace workspace = await HandlerWorkspace.BuildAsync(
        [
            new TestFile(@"scripts\zm\_zm.gsc", ZmSource),
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
    public async Task ASysQualifiedCallHoversAsTheBuiltin()
    {
        string? markdown = await HoverAtAsync(3, 23);

        Assert.NotNull(markdown);

        // Neither the script declaration's link nor its namespace: a builtin hover carries no link
        // (HoverDefinitionLinkTests.ABuiltinHoverCarriesNoLink), and zm:: is not what sys:: names.
        Assert.DoesNotContain("_zm.gsc", markdown!, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("zm::", markdown!, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("#L", markdown!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ANamespaceQualifiedCallStillHoversAsTheScriptFunction()
    {
        string? markdown = await HoverAtAsync(4, 22);

        Assert.NotNull(markdown);
        Assert.Contains(@"[`scripts\zm\_zm.gsc:2`](", markdown!, StringComparison.Ordinal);
    }
}
