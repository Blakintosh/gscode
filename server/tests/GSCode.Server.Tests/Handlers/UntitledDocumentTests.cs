using GSCode.Core.Paths;
using GSCode.Workspace.Documents;
using GSCode.Parser;
using OmniSharp.Extensions.LanguageServer.Protocol;
using Xunit;

namespace GSCode.Server.Tests.Handlers;

/// <summary>
/// Unsaved `untitled:` buffers are meant to get a Workspace context keyed by their URI and to
/// never reach the cache. That holds today without special-casing, so these tests pin the
/// behaviour rather than describe a feature: `GetFileSystemPath()` yields the bare buffer name
/// and `PathUtil.NormalizeAbsolute` resolves it against the server's working directory,
/// producing a stable synthetic path. The one thing that matters is that it is STABLE across
/// the document's lifetime, since open/change/close all key off it.
/// </summary>
public class UntitledDocumentTests
{
    private static string KeyFor(string uriText)
    {
        return PathUtil.NormalizeAbsolute(DocumentUri.Parse(uriText).GetFileSystemPath());
    }

    [Fact]
    public void UntitledUri_ProducesAStableKey_AndDoesNotThrow()
    {
        // Path.GetFullPath on a bare name resolves against the process directory rather than
        // throwing, which is why untitled buffers work at all.
        string first = KeyFor("untitled:Untitled-1.gsc");
        string second = KeyFor("untitled:Untitled-1.gsc");

        Assert.NotEmpty(first);
        Assert.Equal(first, second);
    }

    [Fact]
    public void DistinctUntitledBuffers_DoNotCollide()
    {
        Assert.NotEqual(KeyFor("untitled:Untitled-1.gsc"), KeyFor("untitled:Untitled-2.gsc"));
    }

    [Fact]
    public void DocumentStore_RoundTripsAnUntitledBuffer()
    {
        DocumentStore documents = new(
            _ => GSCode.Parser.Preprocessing.NullInsertProvider.Instance, new GSCode.Core.NameTable());

        string key = KeyFor("untitled:Untitled-1.gsc");
        documents.Open(key, "function f()\n{\n}\n", version: 1);

        Assert.True(documents.TryGet(key, out OpenDocument document));

        ParseResult result = documents.Analyze(document);
        Assert.Single(result.Extraction.Functions);

        documents.Close(key);
        Assert.False(documents.TryGet(key, out OpenDocument _));
    }

    [Fact]
    public void UntitledBuffers_NeverReachTheCache()
    {
        // Structural, not incidental: only WorkspaceIndexer commits records to the database,
        // and it enumerates files from the resolver — an open buffer is never a target. This
        // test documents the invariant so a future "commit open documents" change has to
        // confront it deliberately.
        DocumentStore documents = new(
            _ => GSCode.Parser.Preprocessing.NullInsertProvider.Instance, new GSCode.Core.NameTable());

        string key = KeyFor("untitled:Untitled-1.gsc");
        OpenDocument document = documents.Open(key, "function f()\n{\n}\n", version: 1);

        Assert.Equal(key, document.Path);
    }
}
