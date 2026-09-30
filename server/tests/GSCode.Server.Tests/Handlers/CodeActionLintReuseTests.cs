using GSCode.Core.Diagnostics;
using GSCode.Server.Handlers;
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
    /// <summary>
    /// One request's lints over <see cref="Source"/>, open in an otherwise empty workspace — the
    /// memo is what is under test, not what the pass finds.
    /// </summary>
    private static CodeActionHandler.RequestLints LintsOver(HandlerWorkspace workspace)
    {
        OpenDocument document = workspace.Open(@"scripts\main.gsc", Source);
        DocumentLinter linter = new(
            workspace.Database, workspace.ResolverHolder, workspace.Builtins, workspace.ObjectFields);

        return new CodeActionHandler.RequestLints(linter, document, document.LatestResult!);
    }

    /// <summary>A document the cross-file pass has something to say about.</summary>
    private const string Source = "#using util;\n\nfunction run()\n{\n    a = 1;\n}\n";

    [Fact]
    public async Task TwoReadsOfOneRequest_RunThePassOnce()
    {
        using HandlerWorkspace workspace = await HandlerWorkspace.BuildAsync([]);
        CodeActionHandler.RequestLints lints = LintsOver(workspace);

        ImmutableArray<Diagnostic> first = lints.All(CancellationToken.None);
        ImmutableArray<Diagnostic> second = lints.All(CancellationToken.None);

        Assert.True(first == second, "the second read re-ran the lint pass instead of reusing the first");
    }

    [Fact]
    public async Task TheMemoisedSet_IsTheWholeDocumentsDiagnostics()
    {
        // The memo is not allowed to be a filtered or truncated view: both consumers slice it
        // themselves, one by line and one by diagnostic code.
        using HandlerWorkspace workspace = await HandlerWorkspace.BuildAsync([]);
        CodeActionHandler.RequestLints lints = LintsOver(workspace);
        int count = lints.All(CancellationToken.None).Length;

        Assert.Equal(count, lints.All(CancellationToken.None).Length);
        // UnusedLocal comes from NodeLintPass, not from the parse — so its presence is what proves
        // the memo holds the CROSS-FILE pass's output and not merely result.AllDiagnostics.
        Assert.Contains(
            lints.All(CancellationToken.None),
            diagnostic => diagnostic.Code == GscDiagnosticCode.UnusedLocal);
    }

    [Fact]
    public async Task SeparateRequests_DoNotShareAMemo()
    {
        // Per request and dropped with it: a memo that outlived the request could answer for a
        // buffer that has since been edited.
        using HandlerWorkspace workspace = await HandlerWorkspace.BuildAsync([]);
        using HandlerWorkspace otherWorkspace = await HandlerWorkspace.BuildAsync([]);
        CodeActionHandler.RequestLints one = LintsOver(workspace);
        CodeActionHandler.RequestLints other = LintsOver(otherWorkspace);

        Assert.False(one.All(CancellationToken.None) == other.All(CancellationToken.None));
    }
}
