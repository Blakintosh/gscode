using GSCode.Server.Handlers;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using Xunit;

namespace GSCode.Server.Tests.Handlers;

/// <summary>
/// Find-all-references on an engine BUILTIN finds every call site, not only the ones that happen to
/// share the asking file's namespace.
///
/// A builtin has no declaration, so <c>SymbolExtractor.RecordCalleeReference</c> has nothing to key
/// its call sites to and keys each one by the scope it was WRITTEN in — the file's namespace, the
/// enclosing class, or nothing at all for <c>sys::</c>. On a namespace dialect that makes every
/// file's calls a different key, so the answer used to be whatever the asking file itself contained.
/// The extractor's own comment calls the builtin case "a query-time concern"; these pin that concern
/// being handled.
///
/// Driven through the handler over a real indexed workspace, because the keys only exist once real
/// parsing has produced them, and with the real builtin library, because the library is half of what
/// decides that a name is the engine's.
/// </summary>
public sealed class BuiltinReferenceTests
{
    // getentarray() is an engine function no script here declares. It is called from three different
    // namespaces, from inside a class, and through the explicit sys:: qualifier.
    private const string LibSource =
        "#namespace lib;\nfunction helper()\n{\n    a = getentarray();\n}\n";

    private const string CallerSource =
        "#using scripts\\lib;\n#namespace caller;\nfunction run()\n{\n    b = getentarray();\n    lib::helper();\n}\n";

    private const string OtherSource =
        "#using scripts\\lib;\n#namespace other;\n"
        + "class Widget\n{\n    function work()\n    {\n        c = getentarray();\n    }\n}\n"
        + "function run2()\n{\n    d = sys::getentarray();\n    lib::helper();\n}\n";

    // A script function that SHARES an engine name. Inside this namespace the call means this
    // function, and its references must stay its own.
    private const string ShadowSource =
        "#namespace shadow;\nfunction getentarray()\n{\n}\nfunction use()\n{\n    e = getentarray();\n}\n";

    private static async Task<List<string>> ReferencesAtAsync(string fileRelativePath, int line, int character)
    {
        using HandlerWorkspace workspace = await HandlerWorkspace.BuildAsync(
        [
            new TestFile(@"scripts\lib.gsc", LibSource),
            new TestFile(@"scripts\caller.gsc", CallerSource),
            new TestFile(@"scripts\other.gsc", OtherSource),
            new TestFile(@"scripts\shadow.gsc", ShadowSource),
        ]);
        workspace.Open(fileRelativePath);

        ReferencesHandler handler = new(workspace.Navigation, HandlerWorkspace.Selector);

        ReferenceParams request = new()
        {
            TextDocument = HandlerWorkspace.Identify(fileRelativePath),
            Position = new Position(line, character),
            Context = new ReferenceContext { IncludeDeclaration = true },
        };

        LocationContainer? result = await handler.Handle(request, CancellationToken.None);

        List<string> found = [];
        foreach ( Location location in result ?? new LocationContainer() )
        {
            found.Add(Path.GetFileName(location.Uri.GetFileSystemPath()) + ":" + location.Range.Start.Line);
        }

        // Ordinal, so the expectations below read "other.gsc:11" before "other.gsc:6". Sorted at
        // all because the union walks the key SET, whose order is not part of the answer.
        found.Sort(StringComparer.Ordinal);
        return found;
    }

    [Fact]
    public async Task ABuiltinCall_FindsEveryNamespacesCallSites()
    {
        List<string> found = await ReferencesAtAsync(@"scripts\caller.gsc", 4, 10);

        // The call in lib's namespace, this file's own, the one inside other.gsc's CLASS, and
        // other.gsc's sys:: form — four scopes, four keys, one engine function. shadow.gsc declares
        // its own and is a different symbol; the test below pins that.
        Assert.Equal(
            new[] { "caller.gsc:4", "lib.gsc:3", "other.gsc:11", "other.gsc:6" },
            found);
    }

    [Fact]
    public async Task ABuiltinCall_IsAnsweredTheSameFromAnyOfItsCallSites()
    {
        // The answer must not depend on which namespace asked, which is exactly what was wrong:
        // asked from lib.gsc this returned lib.gsc alone.
        List<string> fromCaller = await ReferencesAtAsync(@"scripts\caller.gsc", 4, 10);
        List<string> fromLib = await ReferencesAtAsync(@"scripts\lib.gsc", 3, 10);

        Assert.Equal(fromCaller, fromLib);
    }

    [Fact]
    public async Task AScriptFunctionSharingAnEngineName_KeepsItsOwnReferences()
    {
        // shadow.gsc declares getentarray(), so inside that namespace the call means the script
        // function. Widening it to every builtin call site would merge two different symbols — which
        // is why the library alone does not decide this, and "nothing declares it" is the other half.
        List<string> found = await ReferencesAtAsync(@"scripts\shadow.gsc", 6, 10);

        Assert.Equal(new[] { "shadow.gsc:1", "shadow.gsc:6" }, found);
    }

    [Fact]
    public async Task AScriptFunction_IsUnaffected()
    {
        // The control: an ordinary cross-file function was always found everywhere, and still is.
        List<string> found = await ReferencesAtAsync(@"scripts\caller.gsc", 5, 10);

        Assert.Equal(new[] { "caller.gsc:5", "lib.gsc:1", "other.gsc:12" }, found);
    }
}
