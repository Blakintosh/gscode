using GSCode.Core;
using GSCode.Core.Symbols;
using GSCode.Parser.Preprocessing;
using GSCode.Server.Configuration;
using GSCode.Server.Handlers;
using GSCode.Workspace.Api;
using GSCode.Workspace.Database;
using GSCode.Workspace.Documents;
using GSCode.Workspace.Resolution;
using GSCode.Workspace.Typing;
using OmniSharp.Extensions.LanguageServer.Protocol;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using System.Collections.Immutable;
using Xunit;

namespace GSCode.Server.Tests.Handlers;

/// <summary>
/// Hovering a field walks the file's assignments once per document VERSION, not once per hover.
///
/// `FlowTyper.InferAssignments` carries no memoisation of its own — only `InferValues` does, and
/// only per instance — so building a fresh typer per request re-walked every function in the file
/// for every hover. Hovering is a mouse-move away, and a hover over a field is the common case on
/// GSC code, where `self.x` is how state is kept.
///
/// Asserted by array IDENTITY, the same proof `CodeActionLintReuseTests` uses: the walk builds a
/// fresh array every time it runs, so two reads returning the same array is proof it ran once.
/// </summary>
public class HoverInferenceReuseTests
{
    private const string Path1 = @"c:\bo3\share\raw\scripts\fields.gsc";

    private static string ApiDirectory => System.IO.Path.Combine(AppContext.BaseDirectory, "Api");

    // Two field writes and a local, so the walk has something to return either way.
    private const string Source =
        "function run()\n{\n    self.state = \"idle\";\n    self.count = 3;\n    a = 1;\n}\n";

    private static HoverHandler BuildHandler(string source, out NavigationTarget target)
    {
        DocumentStore documents = new(static _ => NullInsertProvider.Instance, new NameTable());
        OpenDocument document = documents.Open(Path1, source, version: 1);
        documents.AnalyzeIfStale(document);

        ScriptDatabase database = new();
        ResolverHolder holder = new(new PhysicalFileSystem());
        NavigationSupport support = new(documents, database, holder);

        HoverHandler handler = new(
            support,
            BuiltinApiSet.Load(ApiDirectory),
            ObjectFields.Load(ApiDirectory),
            TextDocumentSelector.ForLanguage("gsc"));

        target = support.Resolve(DocumentUri.FromFileSystemPath(Path1), CancellationToken.None)!;
        Assert.NotNull(target);
        return handler;
    }

    [Fact]
    public void TwoHoversOnOneVersion_WalkTheFileOnce()
    {
        HoverHandler handler = BuildHandler(Source, out NavigationTarget target);

        ImmutableArray<InferredAssignment> first = handler.AssignmentsOf(target);
        ImmutableArray<InferredAssignment> second = handler.AssignmentsOf(target);

        Assert.True(first == second, "the second hover re-walked the file instead of reusing the first walk");
    }

    [Fact]
    public void TheCachedWalk_IsWhatTheTyperWouldHaveReturned()
    {
        // The cache is not allowed to be a filtered or reordered view of the walk: the field hover
        // reads every entry and requires every write of a name to agree.
        HoverHandler handler = BuildHandler(Source, out NavigationTarget target);

        ImmutableArray<InferredAssignment> cached = handler.AssignmentsOf(target);
        ImmutableArray<InferredAssignment> direct =
            new FlowTyper(BuiltinApiSet.Load(ApiDirectory).For(target.Language), ObjectFields.Load(ApiDirectory))
                .InferAssignments(target.Result);

        Assert.Equal(direct.Length, cached.Length);
        Assert.Equal(
            direct.Select(static a => (a.Name, a.IsField, a.Type)),
            cached.Select(static a => (a.Name, a.IsField, a.Type)));
    }

    [Fact]
    public void ANewVersionOfTheDocument_IsWalkedAgain()
    {
        // Keyed by ParseResult reference: an edited document is a different parse, and must not be
        // answered from the old one.
        HoverHandler handler = BuildHandler(Source, out NavigationTarget target);
        ImmutableArray<InferredAssignment> first = handler.AssignmentsOf(target);

        HoverHandler other = BuildHandler(Source, out NavigationTarget reparsed);
        Assert.False(first == other.AssignmentsOf(reparsed));
    }
}
