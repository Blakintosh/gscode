using System.Collections.Immutable;
using GSCode.Core;
using GSCode.Core.Paths;
using GSCode.Core.Symbols;
using GSCode.Parser.Extraction;
using GSCode.Server.Configuration;
using GSCode.Server.Handlers;
using GSCode.Workspace.Api;
using GSCode.Workspace.Database;
using GSCode.Workspace.Documents;
using GSCode.Workspace.Indexing;
using GSCode.Workspace.Resolution;
using OmniSharp.Extensions.LanguageServer.Protocol;
using OmniSharp.Extensions.LanguageServer.Protocol.Document;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using Xunit;
using Xunit.Abstractions;
using LspPosition = OmniSharp.Extensions.LanguageServer.Protocol.Models.Position;
using SymbolKind = GSCode.Core.Symbols.SymbolKind;

namespace GSCode.Server.Tests.Corpus;

/// <summary>
/// A path call names its file outright: <c>animscripts\cover_prone::main()</c> means cover_prone's
/// <c>main</c>, even in combat.gsc, which declares a <c>main</c> of its own. Every path call in the
/// cod4 scripts is resolved here the way hover resolves it, and the declaration found has to be the
/// one the written file declares ITSELF — a far call never sees what that file includes. A call into
/// a file that does not declare the name resolves to nothing, and is allowed
/// only in a script nothing loads (<see cref="UnloadedStockScripts"/>).
///
/// Under <c>#include</c> every same-named function shares one key, so nothing in the key itself
/// says which file a call means; only the written path does. The corpus is what makes this worth
/// checking per call rather than in a fixture: the shipped animscripts path-call each other's
/// <c>main</c> from files that declare their own.
/// </summary>
[Trait("Category", "Corpus")]
[Collection(GameProfileCollection.Name)]
public class PathCallResolutionCorpusTests
{
    private readonly ITestOutputHelper _output;

    public PathCallResolutionCorpusTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public async Task EveryPathCallResolvesToTheFileItNames()
    {
        GameCorpus? cod4 = GameCorpusFixture.For(GameProfile.Cod4);
        if ( cod4 is null )
        {
            _output.WriteLine("SKIPPED: %GSCODE_CORPUS_COD4% not found.");
            return;
        }

        GameProfile previous = GameProfile.Active;
        try
        {
            GameProfile.Select(cod4.Profile.ShortName);
            PathResolver resolver = GameCorpusFixture.Resolver(cod4);
            ScriptDatabase database = new();
            WorkspaceIndexer indexer = new(database, () => resolver, new PhysicalFileSystem(), new NameTable());
            await indexer.IndexAsync(IndexingMode.Full, NullIndexProgressListener.Instance, CancellationToken.None);

            int checkedCalls = 0;
            int unindexedTargets = 0;
            int wrongWithoutPath = 0;
            List<string> wrong = [];
            foreach ( ScriptRecord record in database.Gsc.AllRecords )
            {
                foreach ( PathCallReference pathCall in record.PathCallTargets )
                {
                    ReferenceEntry? call = CallAt(record, pathCall);
                    if ( call is not ReferenceEntry entry )
                    {
                        continue;
                    }

                    string written = RelativePathIndex.Normalize(pathCall.Path);
                    if ( database.Gsc.FilesAt(written).IsEmpty )
                    {
                        unindexedTargets++;
                        continue;
                    }

                    checkedCalls++;
                    bool declaredThere = Declares(database.Gsc, written, entry.Key.Name);
                    ImmutableArray<ResolvedFunction> resolved = MethodResolution.ResolveCall(
                        database.Gsc, record.ContextId, record.Path, entry.Key, entry.Kind, pathCallTarget: pathCall.Path);
                    bool right = declaredThere
                        ? !resolved.IsEmpty && RelativePathIndex.Normalize(resolved[0].Record.RelativePath) == written
                        : resolved.IsEmpty && UnloadedStockScripts.Contains(cod4.Profile, record.RelativePath);
                    if ( !right )
                    {
                        string landed = resolved.IsEmpty ? "nothing" : resolved[0].Record.RelativePath;
                        wrong.Add($"{record.RelativePath}: {pathCall.Path}::{entry.Key.Name} -> {landed}");
                    }

                    // What the resolution gave before the written path reached it, for the record.
                    ImmutableArray<ResolvedFunction> unpinned = MethodResolution.ResolveCall(
                        database.Gsc, record.ContextId, record.Path, entry.Key, entry.Kind);
                    if ( unpinned.IsEmpty || RelativePathIndex.Normalize(unpinned[0].Record.RelativePath) != written )
                    {
                        wrongWithoutPath++;
                    }
                }
            }

            _output.WriteLine(
                $"cod4: {checkedCalls:N0} path calls checked, {unindexedTargets:N0} naming a file the index lacks; "
                + $"{wrongWithoutPath:N0} would resolve elsewhere without the written path");
            foreach ( string line in wrong.Take(20) )
            {
                _output.WriteLine("  WRONG " + line);
            }

            Assert.True(checkedCalls > 0, "no path calls were checked");
            Assert.Empty(wrong);
        }
        finally
        {
            GameProfile.Select(previous.ShortName);
        }
    }

    /// <summary>
    /// The same question asked of go-to-definition, through the handler, at every cod4 path call into a
    /// file that declares the function: the answer has to be that file. Hover's resolver passing is not
    /// enough — definition reaches its declarations through the reference query, which once read a
    /// path call into a file nothing includes as a bare call reaching nothing, and answered null.
    /// </summary>
    [Fact]
    public async Task EveryPathCallsDefinitionIsInTheFileItNames()
    {
        GameCorpus? cod4 = GameCorpusFixture.For(GameProfile.Cod4);
        if ( cod4 is null )
        {
            _output.WriteLine("SKIPPED: %GSCODE_CORPUS_COD4% not found.");
            return;
        }

        GameProfile previous = GameProfile.Active;
        try
        {
            GameProfile.Select(cod4.Profile.ShortName);
            PathResolver resolver = GameCorpusFixture.Resolver(cod4);
            ScriptDatabase database = new();
            NameTable names = new();
            WorkspaceIndexer indexer = new(database, () => resolver, new PhysicalFileSystem(), names);
            await indexer.IndexAsync(IndexingMode.Partial, NullIndexProgressListener.Instance, CancellationToken.None);

            PhysicalFileSystem files = new();
            ResolverHolder holder = new(files) { Current = resolver };
            InsertCache inserts = new();
            DocumentStore documents = new(
                path => new ResolverInsertProvider(holder.Current, holder.Current.GetContext(path), files, inserts), names, inserts);
            NavigationSupport support = new(
                documents, database, holder, BuiltinApiSet.Load(Path.Combine(AppContext.BaseDirectory, "Api"), cod4.Profile));
            DefinitionHandler definition = new(support, TextDocumentSelector.ForLanguage("gsc"));

            int checkedCalls = 0;
            List<string> wrong = [];
            foreach ( ScriptRecord record in database.Gsc.AllRecords )
            {
                List<PathCallReference> calls = [];
                foreach ( PathCallReference pathCall in record.PathCallTargets )
                {
                    if ( CallAt(record, pathCall) is ReferenceEntry entry
                        && Declares(database.Gsc, RelativePathIndex.Normalize(pathCall.Path), entry.Key.Name) )
                    {
                        calls.Add(pathCall);
                    }
                }

                if ( calls.Count == 0 )
                {
                    continue;
                }

                OpenDocument document = documents.Open(record.Path, File.ReadAllText(record.Path), version: 1);
                documents.AnalyzeIfStale(document);
                TextDocumentIdentifier id = new() { Uri = DocumentUri.FromFileSystemPath(record.Path) };
                foreach ( PathCallReference pathCall in calls )
                {
                    checkedCalls++;
                    LocationOrLocationLinks? found = await definition.Handle(
                        new DefinitionParams { TextDocument = id, Position = new LspPosition(pathCall.NameRange.Start.Line, pathCall.NameRange.Start.Character) },
                        CancellationToken.None);
                    string written = RelativePathIndex.Normalize(pathCall.Path);
                    bool right = found is not null && found.Any(link => link.Location is not null
                        && database.Gsc.TryGet(PathUtil.NormalizeAbsolute(link.Location.Uri.GetFileSystemPath()), out ScriptRecord landed)
                        && RelativePathIndex.Normalize(landed.RelativePath) == written);
                    if ( !right )
                    {
                        wrong.Add($"{record.RelativePath}:{pathCall.NameRange.Start.Line + 1} {pathCall.Path}");
                    }
                }

                documents.Close(record.Path);
            }

            _output.WriteLine($"cod4: {checkedCalls:N0} path calls asked for their definition");
            foreach ( string line in wrong.Take(20) )
            {
                _output.WriteLine("  WRONG " + line);
            }

            Assert.True(checkedCalls > 0, "no path calls were checked");
            Assert.Empty(wrong);
        }
        finally
        {
            GameProfile.Select(previous.ShortName);
        }
    }

    /// <summary>Whether a file at the written path declares the function itself.</summary>
    private static bool Declares(LanguageStore store, string relative, string keyName)
    {
        foreach ( string path in store.FilesAt(relative) )
        {
            if ( !store.TryGet(path, out ScriptRecord record) )
            {
                continue;
            }

            foreach ( FunctionSymbol function in record.Functions )
            {
                if ( function.KeyName == keyName )
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static ReferenceEntry? CallAt(ScriptRecord record, PathCallReference pathCall)
    {
        foreach ( ReferenceEntry entry in record.References )
        {
            if ( entry.Range == pathCall.NameRange && entry.Key.Kind == SymbolKind.Function
                && entry.Kind is ReferenceKind.Call or ReferenceKind.AddressOf )
            {
                return entry;
            }
        }

        return null;
    }
}
