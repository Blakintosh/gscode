using GSCode.Server.Configuration;
using GSCode.Server.Handlers;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using Xunit;

namespace GSCode.Server.Tests.Handlers;

/// <summary>
/// Literal and field completion send the workspace's vocabulary cut to what has been typed, so the
/// list they answer with is only true for that text and must say so: marked incomplete, the editor
/// asks again as the text changes instead of filtering a page a different question would have filled
/// differently. Driven through the handler, since the flag is the handler's to set.
/// </summary>
public sealed class NarrowedCompletionTests
{
    private const string EventsSource =
        "#namespace ev;\nfunction fire()\n{\n    self notify( \"player_spawned\" );\n    level.round_number = 1;\n}\n";

    /// <summary>The completion list at the end of <paramref name="typed"/>, the caller's only statement.</summary>
    private static async Task<CompletionList> CompleteAfterAsync(string typed)
    {
        string source = "#namespace caller;\nfunction run()\n{\n    " + typed + "\n}\n";
        using HandlerWorkspace workspace = await HandlerWorkspace.BuildAsync(
        [
            new TestFile(@"scripts\events.gsc", EventsSource),
            new TestFile(@"scripts\caller.gsc", source),
        ]);
        workspace.Open(@"scripts\caller.gsc");

        CompletionHandler handler = new(
            workspace.Navigation,
            workspace.Completion,
            new ServerSettings(),
            HandlerWorkspace.Selector,
            workspace.Builtins);

        CompletionParams request = new()
        {
            TextDocument = HandlerWorkspace.Identify(@"scripts\caller.gsc"),
            Position = new Position(3, 4 + typed.Length),
        };

        return await handler.Handle(request, CancellationToken.None);
    }

    [Fact]
    public async Task ALiteralListIsIncomplete()
    {
        CompletionList list = await CompleteAfterAsync("x = \"player");

        Assert.Contains(list.Items, item => item.Label == "player_spawned");
        Assert.True(list.IsIncomplete);
    }

    [Fact]
    public async Task AFieldListIsIncomplete()
    {
        CompletionList list = await CompleteAfterAsync("x = level.rou");

        Assert.Contains(list.Items, item => item.Label == "round_number");
        Assert.True(list.IsIncomplete);
    }

    [Fact]
    public async Task ALiteralListThatMatchedNothingIsStillIncomplete()
    {
        // No row is left to carry the flag. Answered complete, the editor would cache the empty page
        // for the rest of the string, and a backspace that widens the text again would find nothing.
        // Two characters, so the literal being typed is too short to be offered back as a name.
        CompletionList list = await CompleteAfterAsync("x = \"zq");

        Assert.Empty(list.Items);
        Assert.True(list.IsIncomplete);
    }
}
