using System.Collections.Immutable;
using System.Text;
using GSCode.Core;
using GSCode.Core.Diagnostics;
using GSCode.Core.Symbols;
using GSCode.Core.Text;
using GSCode.Parser;
using GSCode.Parser.Preprocessing;
using Xunit;

namespace GSCode.Parser.Tests.Syntax;

/// <summary>
/// An analysis nobody is waiting for any more stops instead of finishing.
///
/// The 250 ms debounce bounds when an analysis STARTS; nothing bounded how long one runs, and the
/// cancellation token reached <c>Task.Delay</c> and nothing else. A superseded pass therefore ran
/// to completion, holding its own token arrays and tree the whole way, and then published. On a
/// file slow enough to outrun the debounce that is several dead analyses in flight at once.
/// </summary>
public class ParseCancellationTests
{
    private static ParseResult Analyze(string source, CancellationToken cancellationToken)
    {
        return ScriptAnalysis.Analyze(
            @"C:\bo3\share\raw\scripts\main.gsc",
            ScriptLanguage.Gsc,
            SourceText.From(source),
            NullInsertProvider.Instance,
            new NameTable(),
            profile: null,
            headerCache: null,
            cancellationToken);
    }

    /// <summary>A file long enough that cancelling mid-parse has somewhere to land.</summary>
    private static string LongFile(int statements)
    {
        StringBuilder source = new();
        source.AppendLine("function main()");
        source.AppendLine("{");

        for ( int index = 0; index < statements; index++ )
        {
            source.Append("    value").Append(index).AppendLine(" = 1 + 2 * 3;");
        }

        source.AppendLine("}");
        return source.ToString();
    }

    [Fact]
    public void AnAlreadyCancelledAnalysisThrows()
    {
        Assert.Throws<OperationCanceledException>(
            () => Analyze("function main()\n{\n}\n", new CancellationToken(canceled: true)));
    }

    [Fact]
    public async Task CancellationIsObservedInsideALongFile()
    {
        // Cancelled once the parse is already running, which is the case that matters: the check
        // points have to be inside the loops, not only between the stages.
        using CancellationTokenSource cancellation = new();
        string source = LongFile(50_000);

        Task analysis = Task.Run(() => Analyze(source, cancellation.Token));
        cancellation.CancelAfter(TimeSpan.FromMilliseconds(1));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => analysis);
    }

    [Fact]
    public void AnUncancelledParseIsByteIdentical()
    {
        // The guard that the check points changed nothing: same source, same diagnostics.
        string source = "function main()\n{\n    if ( 1 )\n    {\n        return;\n    }\n    x = ;\n}\n";

        ImmutableArray<Diagnostic> first = Analyze(source, CancellationToken.None).AllDiagnostics;
        ImmutableArray<Diagnostic> second = Analyze(source, default).AllDiagnostics;

        Assert.Equal(first.Length, second.Length);
        for ( int index = 0; index < first.Length; index++ )
        {
            Assert.Equal(first[index].Code, second[index].Code);
            Assert.Equal(first[index].Range, second[index].Range);
            Assert.Equal(first[index].Message, second[index].Message);
        }
    }
}
