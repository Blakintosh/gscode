using GSCode.Core;
using GSCode.Core.Diagnostics;
using GSCode.Parser;
using GSCode.Parser.Preprocessing;
using GSCode.Server.Configuration;
using GSCode.Server.Handlers;
using GSCode.Workspace.Api;
using GSCode.Workspace.Database;
using GSCode.Workspace.Documents;
using System.Collections.Immutable;
using Xunit;

namespace GSCode.Server.Tests.Handlers;

/// <summary>
/// One code-action request runs the lint pass at most once.
///
/// Two things inside <c>CodeActionHandler.Handle</c> want that pass for different slices of the
/// same answer — the line-scoped fallback and the whole-document unused-import list — and each
/// used to ask <c>DocumentLinter</c> itself. An invoked "Quick Fix..." with nothing in
/// <c>Context.Diagnostics</c> therefore ran twenty-six cross-file rules and a whole-file flow pass
/// twice over one unchanged document.
///
/// Asserted by IDENTITY rather than by counting calls: <c>DocumentLinter.Analyze</c> builds a fresh
/// array every time it runs, so two reads returning the same array is proof the pass ran once. It
/// needs no mock, and it cannot pass for the wrong reason the way a call counter around a sealed
/// type would have needed one.
/// </summary>
public class CodeActionLintReuseTests
{
    private static readonly string AskingPath = TestPaths.Raw(@"scripts\main.gsc");

    private static string ApiDirectory => Path.Combine(AppContext.BaseDirectory, "Api");

    private static CodeActionHandler.RequestLints BuildLints(string source, out int diagnosticCount)
    {
        ScriptDatabase database = new();
        DocumentStore documents = new(static _ => NullInsertProvider.Instance, new NameTable());
        OpenDocument document = documents.Open(AskingPath, source, 1);
        documents.AnalyzeIfStale(document);

        ParseResult result = document.LatestResult!;
        DocumentLinter linter = new(
            database, new ResolverHolder(new FakeFileSystem()),
            BuiltinApiSet.Load(ApiDirectory), ObjectFields.Load(ApiDirectory));

        CodeActionHandler.RequestLints lints = new(linter, document, result);
        diagnosticCount = lints.All(CancellationToken.None).Length;
        return lints;
    }

    /// <summary>A document the cross-file pass has something to say about.</summary>
    private const string Source = "#using util;\n\nfunction run()\n{\n    a = 1;\n}\n";

    [Fact]
    public void TwoReadsOfOneRequest_RunThePassOnce()
    {
        CodeActionHandler.RequestLints lints = BuildLints(Source, out int _);

        ImmutableArray<Diagnostic> first = lints.All(CancellationToken.None);
        ImmutableArray<Diagnostic> second = lints.All(CancellationToken.None);

        Assert.True(first == second, "the second read re-ran the lint pass instead of reusing the first");
    }

    [Fact]
    public void TheMemoisedSet_IsTheWholeDocumentsDiagnostics()
    {
        // The memo is not allowed to be a filtered or truncated view: both consumers slice it
        // themselves, one by line and one by diagnostic code.
        CodeActionHandler.RequestLints lints = BuildLints(Source, out int count);

        Assert.Equal(count, lints.All(CancellationToken.None).Length);
        // UnusedLocal comes from NodeLintPass, not from the parse — so its presence is what proves
        // the memo holds the CROSS-FILE pass's output and not merely result.AllDiagnostics.
        Assert.Contains(
            lints.All(CancellationToken.None),
            diagnostic => diagnostic.Code == GscDiagnosticCode.UnusedLocal);
    }

    [Fact]
    public void SeparateRequests_DoNotShareAMemo()
    {
        // Per request and dropped with it: a memo that outlived the request could answer for a
        // buffer that has since been edited.
        CodeActionHandler.RequestLints one = BuildLints(Source, out int _);
        CodeActionHandler.RequestLints other = BuildLints(Source, out int _);

        Assert.False(one.All(CancellationToken.None) == other.All(CancellationToken.None));
    }
}
