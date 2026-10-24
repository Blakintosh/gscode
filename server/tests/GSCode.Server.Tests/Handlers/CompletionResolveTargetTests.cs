using GSCode.Core;
using GSCode.Server.Configuration;
using GSCode.Server.Handlers;
using GSCode.Workspace.Completion;
using OmniSharp.Extensions.LanguageServer.Protocol;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using Xunit;

namespace GSCode.Server.Tests.Handlers;

/// <summary>
/// A completion row's documentation describes the declaration the name would reach from the file
/// being edited, as hover does once the name is written. The row carries only a name and the
/// namespace its declaration was found under, and on an include dialect that namespace is the file
/// STEM: every <c>*\_utility.gsc</c> answers to <c>_utility</c>, so looking it up by namespace showed
/// whichever copy the index listed first. On BO3 an mp and a zm copy both declare
/// <c>#namespace util</c>, and an mp script <c>#using</c> the mp copy means that one.
///
/// Several unreached copies, so an index order that happens to list the reached one first cannot
/// pass a fact by accident.
/// </summary>
public sealed class CompletionResolveTargetTests
{
    private static async Task<string> DocumentationAsync(
        IEnumerable<TestFile> files, GameProfile? profile, string openRelativePath, string name, string ns, string importPath = "")
    {
        using HandlerWorkspace workspace = await HandlerWorkspace.BuildAsync(files, profile);
        workspace.Open(openRelativePath);

        CompletionHandler handler = new(
            workspace.Navigation, workspace.Completion, new ServerSettings(), HandlerWorkspace.Selector, workspace.Builtins);

        DocumentUri uri = DocumentUri.FromFileSystemPath(TestPaths.Raw(openRelativePath));
        CompletionItem row = new()
        {
            Label = name,
            Data = CompletionHandler.ResolveData(
                new CompletionEntry(name, CompletionKind.Function, ns, name + "($0)", Namespace: ns, ImportPath: importPath), uri),
        };

        CompletionItem resolved = await handler.Handle(row, CancellationToken.None);
        return resolved.Documentation?.MarkupContent?.Value ?? resolved.Documentation?.String ?? "";
    }

    private static readonly TestFile[] s_cod4 =
    [
        new TestFile(@"maps\_utility.gsc", "#include common_scripts\\utility;\n\nfoo( a_param, b_param, c_param )\n{\n}\n"),
        new TestFile(@"maps\mp\_utility.gsc", "foo( mp_param )\n{\n}\n"),
        new TestFile(@"maps\_zombie\_utility.gsc", "foo( zombie_param )\n{\n}\n"),
        new TestFile(@"common_scripts\utility.gsc", "array_thread( ents_param, func_param )\n{\n}\n"),
        new TestFile(@"maps\other\utility.gsc", "array_thread( other_param )\n{\n}\n"),
        new TestFile(@"maps\caller.gsc", "#include maps\\_utility;\n\nmain()\n{\n}\n"),
        new TestFile(@"maps\mp\caller.gsc", "#include maps\\mp\\_utility;\n\nmain()\n{\n}\n"),
    ];

    [Fact]
    public async Task OnAnIncludeDialect_ARowForANameTheFileCannotReach_HasNoScriptDocs()
    {
        // maps\mp\caller includes maps\mp\_utility only, which declares foo; maps\_utility's
        // a_param copy is not reachable from it, and the engine would not look there.
        string documentation = await DocumentationAsync(s_cod4, GameProfile.Cod4, @"maps\mp\caller.gsc", "array_thread", "utility");

        Assert.DoesNotContain("ents_param", documentation);
        Assert.DoesNotContain("other_param", documentation);
    }

    [Fact]
    public async Task AnAutoImportRow_DescribesTheFileItWouldInclude()
    {
        string documentation = await DocumentationAsync(
            s_cod4, GameProfile.Cod4, @"maps\mp\caller.gsc", "array_thread", "utility", importPath: @"common_scripts\utility");

        Assert.Contains("ents_param", documentation);
        Assert.DoesNotContain("other_param", documentation);
    }

    [Fact]
    public async Task OnAnIncludeDialect_ARowDescribesTheIncludedFilesFunction()
    {
        string documentation = await DocumentationAsync(s_cod4, GameProfile.Cod4, @"maps\caller.gsc", "foo", "_utility");

        Assert.Contains("a_param", documentation);
        Assert.DoesNotContain("mp_param", documentation);
        Assert.DoesNotContain("zombie_param", documentation);
    }

    [Fact]
    public async Task OnAnIncludeDialect_ARowForAFunctionOnlyANestedIncludeDeclares_HasNoScriptDocs()
    {
        // maps\caller includes maps\_utility, which includes common_scripts\utility: an include copies
        // only the included file's own functions, so array_thread is out of reach from here.
        string documentation = await DocumentationAsync(s_cod4, GameProfile.Cod4, @"maps\caller.gsc", "array_thread", "utility");

        Assert.DoesNotContain("ents_param", documentation);
        Assert.DoesNotContain("other_param", documentation);
    }

    [Fact]
    public async Task OnANamespaceDialect_ARowDescribesTheCopyTheFileUses()
    {
        TestFile[] files =
        [
            new TestFile(@"scripts\zm\gametypes\_util.gsc", "#namespace util;\nfunction f( zm_param, zm_second )\n{\n}\n"),
            new TestFile(@"scripts\cp\gametypes\_util.gsc", "#namespace util;\nfunction f( cp_param, cp_second, cp_third )\n{\n}\n"),
            new TestFile(@"scripts\mp\gametypes\_util.gsc", "#namespace util;\nfunction f( mp_param )\n{\n}\n"),
            new TestFile(@"scripts\mp\caller.gsc", "#using scripts\\mp\\gametypes\\_util;\n\n#namespace caller;\n\nfunction main()\n{\n}\n"),
        ];

        string documentation = await DocumentationAsync(files, null, @"scripts\mp\caller.gsc", "f", "util");

        Assert.Contains("mp_param", documentation);
        Assert.DoesNotContain("zm_param", documentation);
        Assert.DoesNotContain("cp_param", documentation);
    }
}
