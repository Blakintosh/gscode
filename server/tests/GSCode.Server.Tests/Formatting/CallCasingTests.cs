using GSCode.Server.Formatting;
using GSCode.Server.Handlers;
using GSCode.Server.Tests.Handlers;
using OmniSharp.Extensions.LanguageServer.Protocol;
using Xunit;

namespace GSCode.Server.Tests.Formatting;

/// <summary>
/// The spelling a call is fixed to, answered from an indexed workspace and the real builtin
/// library: a script function's declared spelling wherever it is declared, a builtin's documented
/// one, and nothing when the two share a name, because the spelling is what picks between them.
/// </summary>
public sealed class CallCasingTests
{
    private const string LibSource =
        "#namespace lib;\nfunction Helper_Thing()\n{\n}\n";

    private const string CallerSource =
        "#using scripts\\lib;\n#namespace caller;\n"
        + "function earthquake()\n{\n}\n"
        + "function run()\n{\n    lib::HELPER_THING();\n    x = abs( 1 );\n    Earthquake( 1, 2, 3, 4 );\n    earthquake();\n}\n";

    private static async Task<CallCasing> CasingForCallerAsync(HandlerWorkspace workspace)
    {
        workspace.Open(@"scripts\caller.gsc");
        NavigationTarget? target = workspace.Navigation.ResolveFresh(
            HandlerWorkspace.Identify(@"scripts\caller.gsc").Uri, CancellationToken.None);

        Assert.NotNull(target);
        return await Task.FromResult(new CallCasing(target, workspace.Builtins));
    }

    private static Task<HandlerWorkspace> BuildAsync()
    {
        return HandlerWorkspace.BuildAsync(
        [
            new TestFile(@"scripts\lib.gsc", LibSource),
            new TestFile(@"scripts\caller.gsc", CallerSource),
        ]);
    }

    [Fact]
    public async Task AScriptFunctionTakesItsDeclaredSpellingFromAnotherFile()
    {
        using HandlerWorkspace workspace = await BuildAsync();
        CallCasing casing = await CasingForCallerAsync(workspace);

        Assert.Equal("Helper_Thing", casing.SpellingFor("lib", "HELPER_THING"));
    }

    [Fact]
    public async Task ABuiltinTakesItsDocumentedSpelling()
    {
        using HandlerWorkspace workspace = await BuildAsync();
        CallCasing casing = await CasingForCallerAsync(workspace);

        Assert.Equal("Abs", casing.SpellingFor(null, "abs"));
        Assert.Equal("Abs", casing.SpellingFor("sys", "ABS"));
    }

    [Fact]
    public async Task ANameThatIsBothAScriptFunctionAndABuiltinKeepsItsSpelling()
    {
        // Stock does exactly this: `function earthquake()` beside calls to the engine's
        // `Earthquake( … )`. The spelling decides which one runs, so neither may change.
        using HandlerWorkspace workspace = await BuildAsync();
        CallCasing casing = await CasingForCallerAsync(workspace);

        Assert.Null(casing.SpellingFor(null, "Earthquake"));
        Assert.Null(casing.SpellingFor(null, "earthquake"));
    }

    [Fact]
    public async Task AQualifiedNameIsNeverABuiltin()
    {
        using HandlerWorkspace workspace = await BuildAsync();
        CallCasing casing = await CasingForCallerAsync(workspace);

        Assert.Null(casing.SpellingFor("lib", "abs"));
    }
}
