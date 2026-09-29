using GSCode.Server.Handlers;
using GSCode.Workspace.Indexing;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using Xunit;

namespace GSCode.Server.Tests.Handlers;

/// <summary>
/// Document highlight is scoped to ONE file by definition, but it used to answer that question by
/// scanning this file's own <c>Extraction.References</c> for a raw key match rather than going
/// through the shared <c>FindAllReferences</c> query every other reference-shaped feature uses.
/// That missed the one thing the shared query does that a raw comparison cannot:
/// <c>MethodResolution.Canonicalize</c> widening a method key to its declaring class. This pins the
/// SCOPING half of the fix — a call in another file must never appear in a highlight list — using a
/// real two-file workspace, since references only exist once real parsing produces them.
/// </summary>
public sealed class DocumentHighlightSameFileTests
{
    private const string LibSource = "#namespace lib;\nfunction helper()\n{\n}\n";

    // "helper();" call sits on line 3; the name starts at column 4.
    private const string CallerSource =
        "#using scripts\\lib;\n#namespace caller;\nfunction run()\n{\n    helper();\n}\n";

    private const string OtherCallerSource =
        "#using scripts\\lib;\n#namespace other;\nfunction run2()\n{\n    helper();\n}\n";

    [Fact]
    public async Task AHighlightNeverIncludesACallFromAnotherFile()
    {
        // Real indexing over all three files, so both the caller's and the other file's calls
        // to helper() land in the database as real, parser-produced references.
        using HandlerWorkspace workspace = await HandlerWorkspace.BuildAsync(
            [
                new TestFile(@"scripts\lib.gsc", LibSource),
                new TestFile(@"scripts\caller.gsc", CallerSource),
                new TestFile(@"scripts\other_caller.gsc", OtherCallerSource),
            ],
            mode: IndexingMode.Partial);

        // Opened and analysed with the SAME text just indexed, so the cursor position below lands
        // on the same call the index already knows about.
        workspace.Open(@"scripts\caller.gsc");

        DocumentHighlightHandler handler = new(workspace.Navigation, HandlerWorkspace.Selector);

        DocumentHighlightParams request = new()
        {
            TextDocument = HandlerWorkspace.Identify(@"scripts\caller.gsc"),
            Position = new Position(4, 6), // inside "helper" on line 5 (0-based line 4)
        };

        DocumentHighlightContainer? result = await handler.Handle(request, CancellationToken.None);

        Assert.NotNull(result);
        DocumentHighlight highlight = Assert.Single(result!);
        Assert.Equal(4, highlight.Range.Start.Line);
    }
}
