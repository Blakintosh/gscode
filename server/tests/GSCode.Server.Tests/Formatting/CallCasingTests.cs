using GSCode.Server.Formatting;
using GSCode.Server.Handlers;
using GSCode.Server.Tests.Handlers;
using Xunit;

namespace GSCode.Server.Tests.Formatting;

/// <summary>
/// The spellings names are fixed to, answered from an indexed workspace and the real builtin
/// library. A bare call resolves to a builtin before a script function, so it takes the builtin's
/// spelling; a threaded call, a reference and a qualified call mean the script function. Namespaces
/// take their #namespace directive's spelling and classes their declaration's.
/// </summary>
public sealed class CallCasingTests
{
    private const string LibSource =
        "#namespace Lib_Mixed;\nfunction Helper_Thing()\n{\n}\nclass Widget\n{\n}\n";

    // The shape stock's exploder_shared.gsc has: a script `earthquake` beside bare calls to the
    // engine's, the script one reached only qualified.
    private const string CallerSource =
        "#using scripts\\lib;\n#namespace caller;\n"
        + "function earthquake()\n{\n}\n"
        + "function run()\n{\n    x = abs( 1 );\n    Earthquake( 1, 2, 3, 4 );\n    caller::earthquake();\n}\n";

    /// <summary>The workspace and the lookup over it, disposed together.</summary>
    private sealed class Fixture : IDisposable
    {
        public required HandlerWorkspace Workspace { get; init; }

        public required CallCasing Casing { get; init; }

        public void Dispose()
        {
            Workspace.Dispose();
        }
    }

    private static async Task<Fixture> BuildAsync()
    {
        HandlerWorkspace workspace = await HandlerWorkspace.BuildAsync(
        [
            new TestFile(@"scripts\lib.gsc", LibSource),
            new TestFile(@"scripts\caller.gsc", CallerSource),
        ]);
        workspace.Open(@"scripts\caller.gsc");
        NavigationTarget? target = workspace.Navigation.ResolveFresh(
            HandlerWorkspace.Identify(@"scripts\caller.gsc").Uri, CancellationToken.None);

        Assert.NotNull(target);
        return new Fixture { Workspace = workspace, Casing = new CallCasing(target, workspace.Builtins) };
    }

    [Fact]
    public async Task AQualifiedCallTakesTheDeclaredSpellingFromAnotherFile()
    {
        using Fixture fixture = await BuildAsync();
        CallCasing casing = fixture.Casing;

        Assert.Equal("Helper_Thing", casing.Function("lib_mixed", "HELPER_THING", preferScript: false));
    }

    [Fact]
    public async Task ABareBuiltinCallTakesTheDocumentedSpelling()
    {
        using Fixture fixture = await BuildAsync();
        CallCasing casing = fixture.Casing;

        Assert.Equal("Abs", casing.Function(null, "abs", preferScript: false));
        Assert.Equal("Abs", casing.Function("sys", "ABS", preferScript: false));
    }

    [Fact]
    public async Task ABareCallResolvesToTheBuiltinBeforeAScriptFunction()
    {
        using Fixture fixture = await BuildAsync();
        CallCasing casing = fixture.Casing;

        Assert.Equal("Earthquake", casing.Function(null, "earthquake", preferScript: false));
    }

    [Fact]
    public async Task AThreadedCallOrReferenceOrQualifiedCallMeansTheScriptFunction()
    {
        using Fixture fixture = await BuildAsync();
        CallCasing casing = fixture.Casing;

        Assert.Equal("earthquake", casing.Function(null, "EARTHQUAKE", preferScript: true));
        Assert.Equal("earthquake", casing.Function("caller", "Earthquake", preferScript: false));
    }

    [Fact]
    public async Task AQualifiedNameIsNeverABuiltin()
    {
        using Fixture fixture = await BuildAsync();
        CallCasing casing = fixture.Casing;

        Assert.Null(casing.Function("lib_mixed", "abs", preferScript: false));
    }

    [Fact]
    public async Task ANamespaceTakesItsDirectivesSpelling()
    {
        using Fixture fixture = await BuildAsync();
        CallCasing casing = fixture.Casing;

        Assert.Equal("Lib_Mixed", casing.Qualifier("LIB_MIXED"));
        Assert.Equal("caller", casing.Qualifier("Caller"));
    }

    [Fact]
    public async Task AClassTakesItsDeclarationsSpelling()
    {
        using Fixture fixture = await BuildAsync();
        CallCasing casing = fixture.Casing;

        Assert.Equal("Widget", casing.Class("WIDGET"));
    }
}
