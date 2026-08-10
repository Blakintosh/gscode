using GSCode.Core.Symbols;
using GSCode.Core.Text;
using GSCode.Server.Handlers;
using GSCode.Workspace.Documents;
using Xunit;

namespace GSCode.Server.Tests.Handlers;

/// <summary>
/// Which open documents get re-linted when an edit changes something they can see.
///
/// The cross-file lints read their neighbours, and nothing republished them: removing a
/// <c>#namespace</c> left every caller squiggle-free until each was reopened. Code lenses had the
/// same problem and solved it by asking the client to re-request; diagnostics are server-pushed,
/// so the server has to republish them itself.
/// </summary>
public class DependentDiagnosticsTests
{
    private static OpenDocument Document(string path, int version = 1, int analyzedVersion = 1)
    {
        return new OpenDocument
        {
            Path = path,
            Language = ScriptLanguage.Gsc,
            Text = SourceText.From("function main()\n{\n}\n"),
            Version = version,
            AnalyzedVersion = analyzedVersion,
        };
    }

    [Fact]
    public void ANeighbourIsRefreshed()
    {
        // The whole point: another open file's diagnostics were computed against the edited one.
        Assert.True(DependentDiagnosticsRefresher.ShouldRefresh(
            Document(@"c:\ws\caller.gsc"), originPath: @"c:\ws\util.gsc"));
    }

    [Fact]
    public void TheEditedDocumentIsNot()
    {
        // Its own handler is publishing it; doing it here as well would only race that.
        Assert.False(DependentDiagnosticsRefresher.ShouldRefresh(
            Document(@"c:\ws\util.gsc"), originPath: @"c:\ws\util.gsc"));
    }

    [Fact]
    public void ADocumentMidEditIsNot()
    {
        // Text newer than anything committed, and a debounced analysis of its own already queued.
        // Publishing here would describe text the user has already replaced.
        OpenDocument typing = Document(@"c:\ws\caller.gsc", version: 7, analyzedVersion: 4);

        Assert.True(typing.IsStale);
        Assert.False(DependentDiagnosticsRefresher.ShouldRefresh(typing, originPath: @"c:\ws\util.gsc"));
    }
}
