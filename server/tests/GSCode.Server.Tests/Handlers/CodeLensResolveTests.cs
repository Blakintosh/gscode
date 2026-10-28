using GSCode.Server.Configuration;
using GSCode.Server.Handlers;
using Newtonsoft.Json;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using Xunit;

namespace GSCode.Server.Tests.Handlers;

/// <summary>
/// CodeLens lists its lenses without counts and counts each on resolve, so the editor pays only for
/// the lenses it shows. The lens goes to the client and comes back, so what resolving needs has to
/// survive that trip as data, and a lens resolved from it must read what the old one-step list read.
/// </summary>
public sealed class CodeLensResolveTests
{
    // "helper" is line 1; caller.gsc calls it once, and class Thing's method m twice.
    private const string LibSource =
        "#namespace lib;\nfunction autoexec helper()\n{\n}\nclass Thing\n{\n    function m()\n    {\n    }\n}\n";

    private const string CallerSource =
        "#using scripts\\lib;\n#namespace caller;\nfunction run()\n{\n    lib::helper();\n    t = new Thing();\n    [[ t ]]->m();\n    [[ t ]]->m();\n}\n";

    private static async Task<List<CodeLens>> ListAsync(CodeLensHandler handler)
    {
        CodeLensContainer? lenses = await handler.Handle(
            new CodeLensParams { TextDocument = HandlerWorkspace.Identify(@"scripts\lib.gsc") }, CancellationToken.None);

        Assert.NotNull(lenses);
        return [.. lenses];
    }

    [Fact]
    public async Task TheListCarriesNoCount_AndALensResolvesAfterTheTripToTheClient()
    {
        using HandlerWorkspace workspace = await HandlerWorkspace.BuildAsync(
        [
            new TestFile(@"scripts\lib.gsc", LibSource),
            new TestFile(@"scripts\caller.gsc", CallerSource),
        ]);
        workspace.Open(@"scripts\lib.gsc");

        CodeLensHandler handler = new(
            workspace.Navigation, new ServerSettings { CodeLensEnabled = true }, HandlerWorkspace.Selector, new LensCountCache());

        List<CodeLens> listed = await ListAsync(handler);
        Assert.NotEmpty(listed);
        Assert.All(listed, static lens => Assert.Null(lens.Command));

        List<string> titles = [];
        foreach ( CodeLens lens in listed )
        {
            CodeLens returned = JsonConvert.DeserializeObject<CodeLens>(JsonConvert.SerializeObject(lens))!;
            CodeLens resolved = await handler.Handle(returned, CancellationToken.None);

            Assert.NotNull(resolved.Command);
            Assert.Equal("gscode.showReferences", resolved.Command.Name);
            Assert.Equal(lens.Range.Start.Line, resolved.Command.Arguments![1]!.ToObject<int>());
            titles.Add($"{resolved.Range.Start.Line}:{resolved.Command.Title}");
        }

        CodeLensContainer? all = await handler.HandleResolvedAsync(
            new CodeLensParams { TextDocument = HandlerWorkspace.Identify(@"scripts\lib.gsc") }, CancellationToken.None);
        Assert.NotNull(all);
        Assert.Equal(titles, all.Select(static lens => $"{lens.Range.Start.Line}:{lens.Command!.Title}"));

        Assert.Contains("1:1 reference · autoexec", titles);
        Assert.Contains("6:2 references", titles);
    }
}
