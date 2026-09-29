using GSCode.Core;
using GSCode.Parser.Preprocessing;
using GSCode.Server.Configuration;
using GSCode.Server.Handlers;
using GSCode.Workspace.Api;
using GSCode.Workspace.Database;
using GSCode.Workspace.Documents;
using GSCode.Workspace.Typing;
using OmniSharp.Extensions.LanguageServer.Protocol;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using Xunit;

namespace GSCode.Server.Tests.Handlers;

/// <summary>
/// One flow-typing pass per document version, not per request.
///
/// The client sends one <c>inlayHint</c> request per visible range, so scrolling fires one per
/// frame, and the handler used to build a fresh <c>FlowTyper</c> and re-walk the whole file every
/// time — throwing away <c>FlowTyper.InferValues</c>'s own per-instance memoisation by discarding
/// the instance that held it on every call.
/// </summary>
public class InlayHintTypeCacheTests
{
    private static readonly string Path = TestPaths.Raw(@"scripts\main.gsc");
    private const string Source = "function main()\n{\n    x = 1;\n}\n";

    private static (InlayHintHandler Handler, NavigationSupport Support, DocumentStore Documents) Build()
    {
        DocumentStore documents = new(static _ => NullInsertProvider.Instance, new NameTable());
        documents.Open(Path, Source, 1);

        NavigationSupport support = new(documents, new ScriptDatabase(), new ResolverHolder(new FakeFileSystem()));
        InlayHintHandler handler = new(
            support,
            new BuiltinApiSet(BuiltinApi.Empty, BuiltinApi.Empty),
            ObjectFields.Empty,
            new ServerSettings { InlayParameterNames = true },
            TextDocumentSelector.ForLanguage("gsc"));

        return (handler, support, documents);
    }

    private static NavigationTarget Resolve(NavigationSupport support)
    {
        return support.ResolveFresh(DocumentUri.FromFileSystemPath(Path), CancellationToken.None)!;
    }

    [Fact]
    public void TwoRequestsForAnUnchangedDocumentShareOneInference()
    {
        (InlayHintHandler handler, NavigationSupport support, _) = Build();

        NavigationTarget first = Resolve(support);
        NavigationTarget second = Resolve(support);

        // Sanity on the premise the cache relies on: an unchanged document hands back the SAME
        // ParseResult instance, which is what AnalyzeIfStale's own staleness check guarantees.
        Assert.Same(first.Result, second.Result);

        ScriptTypes firstTypes = handler.InferTypes(first);
        ScriptTypes secondTypes = handler.InferTypes(second);

        Assert.Same(firstTypes, secondTypes);
    }

    [Fact]
    public void AnEditThatMovesTheVersionGetsAFreshInference()
    {
        (InlayHintHandler handler, NavigationSupport support, DocumentStore documents) = Build();

        NavigationTarget before = Resolve(support);
        ScriptTypes beforeTypes = handler.InferTypes(before);

        documents.TryGet(Path, out OpenDocument document);
        documents.ApplyChange(document, range: null, Source + "\nfunction other()\n{\n}\n", version: 2);

        NavigationTarget after = Resolve(support);
        ScriptTypes afterTypes = handler.InferTypes(after);

        Assert.NotSame(before.Result, after.Result);
        Assert.NotSame(beforeTypes, afterTypes);
    }
}
