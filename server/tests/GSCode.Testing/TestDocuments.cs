using GSCode.Core;
using GSCode.Parser.Preprocessing;
using GSCode.Workspace.Documents;

namespace GSCode.Testing;

/// <summary>
/// Open documents with no workspace behind them, for the tests whose subject is the document
/// store itself — its analysis gate, single-flight reruns, versions, untitled buffers — or that
/// need somewhere to hold an open file and nothing more. No <c>#insert</c> resolves here; a test
/// that needs one wants <c>TestWorkspace</c> or <c>HandlerWorkspace</c>.
/// </summary>
public static class TestDocuments
{
    public static DocumentStore Standalone()
    {
        return new DocumentStore(static _ => NullInsertProvider.Instance, new NameTable());
    }
}
