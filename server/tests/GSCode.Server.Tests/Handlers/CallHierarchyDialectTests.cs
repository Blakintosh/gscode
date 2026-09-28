using GSCode.Core;
using GSCode.Core.Paths;
using GSCode.Core.Symbols;
using GSCode.Core.Text;
using GSCode.Parser;
using GSCode.Parser.Preprocessing;
using GSCode.Server.Configuration;
using GSCode.Server.Handlers;
using GSCode.Server.Tests.Corpus;
using GSCode.Workspace.Database;
using GSCode.Workspace.Documents;
using GSCode.Workspace.Resolution;
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
    private static CallHierarchyHandler HandlerOver(GameProfile profile, params (string Path, string Relative, string Source)[] files)
    {
        NameTable names = new();
        ScriptDatabase database = new();

        foreach ( (string Path, string Relative, string Source) file in files )
        {
            ParseResult result = ScriptAnalysis.Analyze(
                file.Path, ScriptLanguage.Gsc, SourceText.From(file.Source), NullInsertProvider.Instance, names, profile);
            database.Commit(result, ResolutionContext.RawContext, isDirty: false, file.Relative);
        }

        DocumentStore documents = new(static _ => NullInsertProvider.Instance, new NameTable());
        NavigationSupport support = new(documents, database, new ResolverHolder(new PhysicalFileSystem()));
        return new CallHierarchyHandler(support, new TextDocumentSelector());
    }

    private static CallHierarchyItem ItemFor(string path, string? keyNamespace, string name)
    {
        return new CallHierarchyItem
        {
            Name = name,
            Kind = OmniSharp.Extensions.LanguageServer.Protocol.Models.SymbolKind.Function,
            Uri = DocumentUri.FromFileSystemPath(path),
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

    private static async Task<CallHierarchyItem> UnderAsync(GameProfile profile, Func<Task<CallHierarchyItem>> body)
    {
        GameProfile previous = GameProfile.Active;
        try
        {
            GameProfile.Select(profile.ShortName);
            return await body();
        }
        finally
        {
            GameProfile.Select(previous.ShortName);
        }
    }

    [Fact]
    public async Task AMergeDialectCallerExpandsToItsOwnCallers()
    {
        const string lib = @"C:\ws\maps\lib.gsc";
        const string caller = @"C:\ws\maps\caller.gsc";
        const string top = @"C:\ws\maps\top.gsc";

        CallHierarchyItem second = await UnderAsync(GameProfile.Cod4, async () =>
        {
            CallHierarchyHandler handler = HandlerOver(
                GameProfile.Cod4,
                (lib, @"maps\lib.gsc", "helper()\n{\n}\n"),
                (caller, @"maps\caller.gsc", "#include maps\\lib;\n\nrun()\n{\n    helper();\n}\n"),
                (top, @"maps\top.gsc", "#include maps\\caller;\n\nmain()\n{\n    run();\n}\n"));

            CallHierarchyItem run = await SingleCallerAsync(handler, ItemFor(lib, null, "helper"), "run");
            return await SingleCallerAsync(handler, run, "main");
        });

        Assert.Equal(DocumentUri.FromFileSystemPath(PathUtil.NormalizeAbsolute(top)), second.Uri);
    }

    [Fact]
    public async Task ANamespaceDialectCallerStillExpandsToItsOwnCallers()
    {
        const string lib = @"C:\ws\scripts\lib.gsc";
        const string caller = @"C:\ws\scripts\caller.gsc";
        const string top = @"C:\ws\scripts\top.gsc";

        CallHierarchyItem second = await UnderAsync(GameProfile.BlackOps3, async () =>
        {
            CallHierarchyHandler handler = HandlerOver(
                GameProfile.BlackOps3,
                (lib, @"scripts\lib.gsc", "#namespace lib;\n\nfunction helper()\n{\n}\n"),
                (caller, @"scripts\caller.gsc", "#using scripts\\lib;\n\n#namespace caller;\n\nfunction run()\n{\n    lib::helper();\n}\n"),
                (top, @"scripts\top.gsc", "#using scripts\\caller;\n\n#namespace top;\n\nfunction main()\n{\n    caller::run();\n}\n"));

            CallHierarchyItem run = await SingleCallerAsync(handler, ItemFor(lib, "lib", "helper"), "run");
            return await SingleCallerAsync(handler, run, "main");
        });

        Assert.Equal(DocumentUri.FromFileSystemPath(PathUtil.NormalizeAbsolute(top)), second.Uri);
    }
}
