using GSCode.Server.Handlers;
using GSCode.Workspace.Database;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using Xunit;

namespace GSCode.Server.Tests.Handlers;

/// <summary>
/// Workspace symbol search (Ctrl+T) applies overlay shadowing: when a mod overlay replaces a raw
/// file, the two declare the same function, and only the overlay's is offered. Without shadowing
/// both appeared, one of them for a file the engine never loads.
/// </summary>
public class WorkspaceSymbolShadowingTests
{
    private static ScriptRecord FunctionRecord(string path, string contextId, string relativePath, string name)
    {
        return TestRecords.At(path, contextId, relativePath) with { Functions = [TestRecords.Function(name)] };
    }

    [Fact]
    public async Task AModOverlayShadowsTheRawFileItReplaces_SoOnlyOneWorkspaceSymbolIsFound()
    {
        ScriptDatabase database = new();
        database.Gsc.Upsert(FunctionRecord(
            TestPaths.Raw(@"maps\_utility.gsc"), "raw", @"maps\_utility", "util"));
        database.Gsc.Upsert(FunctionRecord(
            TestPaths.ModsRoot + @"\zm_grief\maps\_utility.gsc", "mod:zm_grief", @"maps\_utility", "util"));

        WorkspaceSymbolHandler handler = new(database);
        WorkspaceSymbolParams request = new() { Query = "util" };

        Container<WorkspaceSymbol>? result = await handler.Handle(request, CancellationToken.None);

        // Only one "util" should be offered — the raw copy is dead code the engine never loads,
        // and showing it as a second result sends the user to a file that plays no part at all.
        Assert.Single(result!);
    }

    [Fact]
    public async Task AnOverlayDeclaringOtherNames_LeavesTheRawFilesNameOffered()
    {
        // Shadowing here is per NAME: the overlay hides the raw copy of a name it also declares,
        // and the raw file's other names stay in the list, as they did before the search stopped
        // collecting every match first.
        ScriptDatabase database = new();
        database.Gsc.Upsert(FunctionRecord(
            TestPaths.Raw(@"maps\_utility.gsc"), "raw", @"maps\_utility", "only_in_raw"));
        database.Gsc.Upsert(FunctionRecord(
            TestPaths.ModsRoot + @"\zm_grief\maps\_utility.gsc", "mod:zm_grief", @"maps\_utility", "only_in_mod"));

        Container<WorkspaceSymbol>? result = await new WorkspaceSymbolHandler(database).Handle(
            new WorkspaceSymbolParams { Query = "only" }, CancellationToken.None);

        Assert.Equal(["only_in_mod", "only_in_raw"], result!.Select(symbol => symbol.Name).Order());
    }

    [Fact]
    public async Task AClassAnOverlayAlsoDeclares_IsOfferedOnce()
    {
        ScriptDatabase database = new();
        database.Gsc.Upsert(TestRecords.At(TestPaths.Raw(@"scripts\scene.gsc"), "raw", @"scripts\scene") with
        {
            Classes = [TestRecords.Class("cScene")],
        });
        database.Gsc.Upsert(TestRecords.At(TestPaths.ModsRoot + @"\m\scripts\scene.gsc", "mod:m", @"scripts\scene") with
        {
            Classes = [TestRecords.Class("cScene")],
        });

        Container<WorkspaceSymbol>? result = await new WorkspaceSymbolHandler(database).Handle(
            new WorkspaceSymbolParams { Query = "cscene" }, CancellationToken.None);

        WorkspaceSymbol only = Assert.Single(result!);
        Assert.Contains("mods", only.Location.Location!.Uri.GetFileSystemPath(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ManyMatches_StopAtTheCap_FunctionsBeforeClasses()
    {
        ScriptDatabase database = new();
        for ( int file = 0; file < 30; file++ )
        {
            database.Gsc.Upsert(TestRecords.At(TestPaths.Raw($@"scripts\f{file}.gsc"), "raw", $@"scripts\f{file}") with
            {
                Functions = [.. Enumerable.Range(0, 10).Select(index => TestRecords.Function($"match_{file}_{index}"))],
                Classes = [TestRecords.Class($"match_class_{file}")],
            });
        }

        Container<WorkspaceSymbol>? result = await new WorkspaceSymbolHandler(database).Handle(
            new WorkspaceSymbolParams { Query = "match" }, CancellationToken.None);

        // 300 functions and 30 classes match; the list is capped at 256 and functions come first.
        Assert.Equal(256, result!.Count());
        Assert.All(result!, symbol => Assert.Equal(SymbolKind.Function, symbol.Kind));
    }
}
