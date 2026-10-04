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
}
