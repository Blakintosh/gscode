using GSCode.Parser;
using GSCode.Server.Configuration;
using GSCode.Server.Handlers;
using GSCode.Workspace.Api;
using GSCode.Workspace.Documents;
using GSCode.Workspace.Typing;
using Xunit;

namespace GSCode.Server.Tests.Handlers;

/// <summary>
/// One flow-typing pass per document version, not per request.
///
/// The client sends one <c>inlayHint</c> request per visible range, so scrolling fires one per
/// frame, and the handler used to build a fresh <c>FlowTyper</c> and re-walk the whole file every
/// time — throwing away <c>FlowTyper.InferValues</c>'s own per-instance memoisation by discarding
/// the instance that held it on every call.
/// </summary>
public class InlayHintTypeCacheTests
{
    private const string RelativePath = @"scripts\main.gsc";
    private const string Source = "function main()\n{\n    x = 1;\n}\n";

    /// <summary>A handler over one open file, with no engine library: the cache is what is under test.</summary>
    private static InlayHintHandler HandlerOver(HandlerWorkspace workspace)
    {
        return new InlayHintHandler(
            workspace.Navigation,
            new BuiltinApiSet(BuiltinApi.Empty, BuiltinApi.Empty),
            ObjectFields.Empty,
            new ServerSettings { InlayParameterNames = true },
            HandlerWorkspace.Selector);
    }

    private static NavigationTarget Resolve(HandlerWorkspace workspace)
    {
        return workspace.Navigation.ResolveFresh(HandlerWorkspace.Identify(RelativePath).Uri, CancellationToken.None)!;
    }

    [Fact]
    public async Task TwoRequestsForAnUnchangedDocumentShareOneInference()
    {
        using HandlerWorkspace workspace = await HandlerWorkspace.BuildAsync([]);
        workspace.Open(RelativePath, Source);
        InlayHintHandler handler = HandlerOver(workspace);

        NavigationTarget first = Resolve(workspace);
        NavigationTarget second = Resolve(workspace);

        // Sanity on the premise the cache relies on: an unchanged document hands back the SAME
        // ParseResult instance, which is what AnalyzeIfStale's own staleness check guarantees.
        Assert.Same(first.Result, second.Result);

        ScriptTypes firstTypes = handler.InferTypes(first);
        ScriptTypes secondTypes = handler.InferTypes(second);

        Assert.Same(firstTypes, secondTypes);
    }

    [Fact]
    public async Task AnEditThatMovesTheVersionGetsAFreshInference()
    {
        using HandlerWorkspace workspace = await HandlerWorkspace.BuildAsync([]);
        OpenDocument document = workspace.Open(RelativePath, Source);
        InlayHintHandler handler = HandlerOver(workspace);

        NavigationTarget before = Resolve(workspace);
        ScriptTypes beforeTypes = handler.InferTypes(before);

        workspace.Documents.ApplyChange(document, range: null, Source + "\nfunction other()\n{\n}\n", version: 2);

        NavigationTarget after = Resolve(workspace);
        ScriptTypes afterTypes = handler.InferTypes(after);

        Assert.NotSame(before.Result, after.Result);
        Assert.NotSame(beforeTypes, afterTypes);
    }

    [Fact]
    public async Task TheHintsReadTheWalkTheServersLintPassDid()
    {
        // The point of sharing: after an edit the linter types the new parse, and the hint request
        // that follows resolves to that SAME parse and finds its answer already there. Both halves
        // are needed: a lint that did not fill the cache, or a request that resolved a different
        // parse of the same text, would each mean typing the file twice per edit.
        using HandlerWorkspace workspace = await HandlerWorkspace.BuildAsync([]);
        OpenDocument document = workspace.Open(RelativePath, Source);
        DocumentLinter linter = new(
            workspace.Database, workspace.ResolverHolder, workspace.Builtins, workspace.ObjectFields);

        ParseResult linted = workspace.Documents.AnalyzeIfStale(document);
        linter.Analyze(document, linted);

        NavigationTarget target = Resolve(workspace);

        Assert.Same(linted, target.Result);
        Assert.NotNull(FlowTyper.SharedFor(target.Result));
    }

    [Fact]
    public async Task HoverReadsTheSameWalkAsTheHints()
    {
        // Hover kept a table of its own and walked the file again for its assignments. Both now read
        // the one shared answer, so the hover's array is the very array the hints' answer carries.
        using HandlerWorkspace workspace = await HandlerWorkspace.BuildAsync([]);
        workspace.Open(RelativePath, Source);
        InlayHintHandler hints = HandlerOver(workspace);
        HoverHandler hover = new(
            workspace.Navigation,
            new BuiltinApiSet(BuiltinApi.Empty, BuiltinApi.Empty),
            ObjectFields.Empty,
            HandlerWorkspace.Selector);

        NavigationTarget target = Resolve(workspace);

        Assert.True(hints.InferTypes(target).Assignments == hover.AssignmentsOf(target));
    }
}
