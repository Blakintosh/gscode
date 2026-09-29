using GSCode.Core;
using GSCode.Core.Paths;
using GSCode.Server.Handlers;
using GSCode.Server.Tests.Corpus;
using Newtonsoft.Json.Linq;
using OmniSharp.Extensions.LanguageServer.Protocol;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using Xunit;

namespace GSCode.Server.Tests.Handlers;

/// <summary>
/// Expanding the call hierarchy a SECOND level, on both dialect families.
///
/// The first level is asked with the key the cursor's reference was indexed under. Every level
/// after that is asked with the key of a caller the handler built itself, and on a merge dialect a
/// function's declared namespace — its file stem — is not the namespace its calls are keyed under
/// (<see cref="GameProfile.KeyNamespace"/>). A caller item keyed on the stem matched no reference,
/// so expanding it reported no incoming calls at all.
/// </summary>
[Collection(GameProfileCollection.Name)]
public class CallHierarchyDialectTests
{
    private static CallHierarchyItem ItemFor(string relativePath, string? keyNamespace, string name)
    {
        return new CallHierarchyItem
        {
            Name = name,
            Kind = OmniSharp.Extensions.LanguageServer.Protocol.Models.SymbolKind.Function,
            Uri = DocumentUri.FromFileSystemPath(TestPaths.Raw(relativePath)),
            Data = JToken.FromObject(new { ns = keyNamespace ?? "", name }),
        };
    }

    private static async Task<CallHierarchyItem> SingleCallerAsync(CallHierarchyHandler handler, CallHierarchyItem item, string expectedName)
    {
        Container<CallHierarchyIncomingCall>? incoming = await handler.Handle(
            new CallHierarchyIncomingCallsParams { Item = item }, CancellationToken.None);

        Assert.NotNull(incoming);
        CallHierarchyIncomingCall call = Assert.Single(incoming);
        Assert.Equal(expectedName, call.From.Name);
        return call.From;
    }

    /// <summary>Indexes the three files under <paramref name="profile"/> and walks two levels up from helper.</summary>
    private static async Task<CallHierarchyItem> SecondCallerAsync(
        GameProfile profile, string? libNamespace, params TestFile[] files)
    {
        using HandlerWorkspace workspace = await HandlerWorkspace.BuildAsync(files, profile);
        CallHierarchyHandler handler = new(workspace.Navigation, HandlerWorkspace.Selector);

        CallHierarchyItem run = await SingleCallerAsync(handler, ItemFor(files[0].RelativePath, libNamespace, "helper"), "run");
        return await SingleCallerAsync(handler, run, "main");
    }

    [Fact]
    public async Task AMergeDialectCallerExpandsToItsOwnCallers()
    {
        CallHierarchyItem second = await SecondCallerAsync(
            GameProfile.Cod4,
            null,
            new TestFile(@"maps\lib.gsc", "helper()\n{\n}\n"),
            new TestFile(@"maps\caller.gsc", "#include maps\\lib;\n\nrun()\n{\n    helper();\n}\n"),
            new TestFile(@"maps\top.gsc", "#include maps\\caller;\n\nmain()\n{\n    run();\n}\n"));

        Assert.Equal(DocumentUri.FromFileSystemPath(PathUtil.NormalizeAbsolute(TestPaths.Raw(@"maps\top.gsc"))), second.Uri);
    }

    [Fact]
    public async Task ANamespaceDialectCallerStillExpandsToItsOwnCallers()
    {
        CallHierarchyItem second = await SecondCallerAsync(
            GameProfile.BlackOps3,
            "lib",
            new TestFile(@"scripts\lib.gsc", "#namespace lib;\n\nfunction helper()\n{\n}\n"),
            new TestFile(@"scripts\caller.gsc", "#using scripts\\lib;\n\n#namespace caller;\n\nfunction run()\n{\n    lib::helper();\n}\n"),
            new TestFile(@"scripts\top.gsc", "#using scripts\\caller;\n\n#namespace top;\n\nfunction main()\n{\n    caller::run();\n}\n"));

        Assert.Equal(DocumentUri.FromFileSystemPath(PathUtil.NormalizeAbsolute(TestPaths.Raw(@"scripts\top.gsc"))), second.Uri);
    }
}
