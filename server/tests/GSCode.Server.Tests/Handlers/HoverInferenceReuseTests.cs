using GSCode.Server.Handlers;
using GSCode.Workspace.Typing;
using System.Collections.Immutable;
using Xunit;

namespace GSCode.Server.Tests.Handlers;

/// <summary>
/// Hovering a field walks the file's assignments once per document VERSION, not once per hover.
///
/// `FlowTyper.InferAssignments` carries no memoisation of its own — only `InferValues` does, and
/// only per instance — so building a fresh typer per request re-walked every function in the file
/// for every hover. Hovering is a mouse-move away, and a hover over a field is the common case on
/// GSC code, where `self.x` is how state is kept.
///
/// Asserted by array IDENTITY, the same proof `CodeActionLintReuseTests` uses: the walk builds a
/// fresh array every time it runs, so two reads returning the same array is proof it ran once.
/// </summary>
public class HoverInferenceReuseTests
{
    // Two field writes and a local, so the walk has something to return either way.
    private const string Source =
        "function run()\n{\n    self.state = \"idle\";\n    self.count = 3;\n    a = 1;\n}\n";

    /// <summary>
    /// A hover handler over <see cref="Source"/>, open in an otherwise empty workspace, and the
    /// resolved target it answers for.
    /// </summary>
    private static HoverHandler HandlerOver(HandlerWorkspace workspace, out NavigationTarget target)
    {
        workspace.Open(@"scripts\fields.gsc", Source);

        HoverHandler handler = new(
            workspace.Navigation, workspace.Builtins, workspace.ObjectFields, HandlerWorkspace.Selector);

        target = workspace.Navigation.Resolve(HandlerWorkspace.Identify(@"scripts\fields.gsc").Uri, CancellationToken.None)!;
        Assert.NotNull(target);
        return handler;
    }

    [Fact]
    public async Task TwoHoversOnOneVersion_WalkTheFileOnce()
    {
        using HandlerWorkspace workspace = await HandlerWorkspace.BuildAsync([]);
        HoverHandler handler = HandlerOver(workspace, out NavigationTarget target);

        ImmutableArray<InferredAssignment> first = handler.AssignmentsOf(target);
        ImmutableArray<InferredAssignment> second = handler.AssignmentsOf(target);

        Assert.True(first == second, "the second hover re-walked the file instead of reusing the first walk");
    }

    [Fact]
    public async Task TheCachedWalk_IsWhatTheTyperWouldHaveReturned()
    {
        // The cache is not allowed to be a filtered or reordered view of the walk: the field hover
        // reads every entry and requires every write of a name to agree.
        using HandlerWorkspace workspace = await HandlerWorkspace.BuildAsync([]);
        HoverHandler handler = HandlerOver(workspace, out NavigationTarget target);

        ImmutableArray<InferredAssignment> cached = handler.AssignmentsOf(target);
        ImmutableArray<InferredAssignment> direct =
            new FlowTyper(workspace.Builtins.For(target.Language), workspace.ObjectFields)
                .InferAssignments(target.Result);

        Assert.Equal(direct.Length, cached.Length);
        Assert.Equal(
            direct.Select(static a => (a.Name, a.IsField, a.Type)),
            cached.Select(static a => (a.Name, a.IsField, a.Type)));
    }

    [Fact]
    public async Task ANewVersionOfTheDocument_IsWalkedAgain()
    {
        // Keyed by ParseResult reference: an edited document is a different parse, and must not be
        // answered from the old one.
        using HandlerWorkspace workspace = await HandlerWorkspace.BuildAsync([]);
        HoverHandler handler = HandlerOver(workspace, out NavigationTarget target);
        ImmutableArray<InferredAssignment> first = handler.AssignmentsOf(target);

        using HandlerWorkspace otherWorkspace = await HandlerWorkspace.BuildAsync([]);
        HoverHandler other = HandlerOver(otherWorkspace, out NavigationTarget reparsed);
        Assert.False(first == other.AssignmentsOf(reparsed));
    }
}
