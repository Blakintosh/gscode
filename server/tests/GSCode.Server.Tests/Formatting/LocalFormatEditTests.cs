using System.Collections.Immutable;
using System.Text;
using GSCode.Core.Text;
using GSCode.Parser;
using GSCode.Server.Configuration;
using GSCode.Server.Formatting;
using GSCode.Server.Handlers;
using GSCode.Server.Tests.Handlers;
using GSCode.Workspace.Api;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using Xunit;

namespace GSCode.Server.Tests.Formatting;

/// <summary>
/// Formatting edits stay where their changes are, however large the file and however much of it
/// changes. On a 7,000-line file the line diff used to give up past 3,000 changed lines and return
/// one edit from the first change to the last; on-type formatting kept any edit overlapping the
/// cursor's group, so typing a ';' rewrote the whole file and sent the caret to its end.
/// </summary>
public class LocalFormatEditTests
{
    private static readonly FormatOptions s_tabs = FormatOptions.Default with { UseTabs = true };

    /// <summary>
    /// A file with <paramref name="functions"/> functions, each unindented, back to back, and with a
    /// statement split across a case label, so the formatter changes lines all the way through,
    /// adds lines, and splits one.
    /// </summary>
    private static string LargeUnformattedFile(int functions)
    {
        StringBuilder source = new();
        for ( int index = 0; index < functions; index++ )
        {
            source.Append("function f").Append(index).Append("( v )\n{\n");
            source.Append("a = 1;\nb = 2;\n");
            source.Append("switch ( v )\n{\ncase 0: c();\nbreak;\n}\n");
            source.Append("}\n");
        }

        return source.ToString();
    }

    private static string Apply(SourceText text, ImmutableArray<GscFormatter.FormatEdit> edits)
    {
        string result = text.Text;
        for ( int index = edits.Length - 1; index >= 0; index-- )
        {
            int start = text.GetOffset(edits[index].Range.Start);
            int end = text.GetOffset(edits[index].Range.End);
            result = string.Concat(result.AsSpan(0, start), edits[index].NewText, result.AsSpan(end));
        }

        return result;
    }

    [Fact]
    public void ALargeFileStillGetsOneEditPerChangedRegionAndReproducesTheFormat()
    {
        // 1,000 functions: about 11,000 lines, nearly every one of them reindented.
        ParseResult result = TestParse.Analyze(LargeUnformattedFile(1000));

        ImmutableArray<GscFormatter.FormatEdit> edits = GscFormatter.FormatMinimalEdits(result, s_tabs);

        Assert.Equal(GscFormatter.Format(result, s_tabs), Apply(result.Text, edits));
        Assert.All(edits, edit => Assert.True(
            edit.Range.End.Line - edit.Range.Start.Line <= 2,
            $"an edit spans lines {edit.Range.Start.Line}-{edit.Range.End.Line}"));
    }

    [Fact]
    public void EditsNeverOverlap()
    {
        ParseResult result = TestParse.Analyze(LargeUnformattedFile(50));

        ImmutableArray<GscFormatter.FormatEdit> edits = GscFormatter.FormatMinimalEdits(result, s_tabs);

        for ( int index = 1; index < edits.Length; index++ )
        {
            GSCode.Core.Text.Position previousEnd = edits[index - 1].Range.End;
            GSCode.Core.Text.Position start = edits[index].Range.Start;
            Assert.True(
                previousEnd.Line < start.Line || (previousEnd.Line == start.Line && previousEnd.Character <= start.Character),
                $"edit {index} starts before edit {index - 1} ends");
        }
    }

    [Fact]
    public void AnEditReachingOutsideTheScopeIsNotWithinIt()
    {
        GscFormatter.FormatEdit wide = new(TextRange.FromCoordinates(3, 0, 900, 0), "x");
        GscFormatter.FormatEdit line = new(TextRange.FromCoordinates(5, 0, 6, 0), "x");
        GscFormatter.FormatEdit insertionAfter = new(TextRange.FromCoordinates(6, 0, 6, 0), "x");

        Assert.False(FormattingSupport.WithinLines(wide, 5, 5));
        Assert.True(FormattingSupport.WithinLines(line, 5, 5));
        Assert.False(FormattingSupport.WithinLines(insertionAfter, 5, 5));
    }

    private static async Task<TextEdit[]> OnTypeAsync(string source, int line)
    {
        using HandlerWorkspace workspace = await HandlerWorkspace.BuildAsync([]);
        workspace.Open(@"scripts\big.gsc", source);
        FormattingSupport support = new(
            workspace.Documents, workspace.ResolverHolder, StockScripts.Empty, new ServerSettings(),
            workspace.Navigation, workspace.Builtins);
        DocumentOnTypeFormattingHandler handler = new(support, HandlerWorkspace.Selector);

        TextEditContainer? edits = await handler.Handle(
            new DocumentOnTypeFormattingParams
            {
                TextDocument = HandlerWorkspace.Identify(@"scripts\big.gsc"),
                Position = new OmniSharp.Extensions.LanguageServer.Protocol.Models.Position(line, 6),
                Character = ";",
                Options = new FormattingOptions { TabSize = 4, InsertSpaces = false },
            },
            CancellationToken.None);

        return edits is null ? [] : [.. edits];
    }

    [Fact]
    public async Task OnTypeFormattingOnALargeFileOnlyTouchesTheLinesAroundTheCursor()
    {
        // Line 6002 is `a = 1;` in the middle of the file: its group is it and `b = 2;` below.
        string source = LargeUnformattedFile(1000);
        int cursorLine = 6002;
        Assert.Equal("a = 1;", source.Split('\n')[cursorLine]);

        TextEdit[] edits = await OnTypeAsync(source, cursorLine);

        Assert.NotEmpty(edits);
        Assert.All(edits, edit =>
        {
            Assert.InRange(edit.Range.Start.Line, cursorLine, cursorLine + 1);
            Assert.InRange(edit.Range.End.Line, cursorLine, cursorLine + 2);
        });
    }
}
