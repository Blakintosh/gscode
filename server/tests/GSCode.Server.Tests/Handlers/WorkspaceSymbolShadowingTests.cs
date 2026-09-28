using GSCode.Core.Symbols;
using GSCode.Server.Handlers;
using GSCode.Workspace.Database;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using Xunit;
using TextRange = GSCode.Core.Text.TextRange;
using Position = GSCode.Core.Text.Position;

namespace GSCode.Server.Tests.Handlers;

/// <summary>
/// Workspace symbol search (Ctrl+T) walks every record with no overlay-shadowing at all — a raw
/// file and the mod overlay that replaces it both declare the same function, and both show up as
/// separate results, one of them for a file the engine never loads.
/// </summary>
public class WorkspaceSymbolShadowingTests
{
    private static readonly TextRange s_someRange = new(new Position(1, 1), new Position(1, 5));

    private static ScriptRecord FunctionRecord(string path, string contextId, string relativePath, string name)
    {
        return new ScriptRecord
        {
            Path = path,
            ContextId = contextId,
            ContentHash = 0,
            Language = ScriptLanguage.Gsc,
            RelativePath = relativePath,
            Functions =
            [
                new FunctionSymbol
                {
                    Name = name,
                    KeyName = name,
                    Namespace = "",
                    NameRange = s_someRange,
                    FullRange = s_someRange,
                },
            ],
        };
    }

    [Fact]
    public async Task AModOverlayShadowsTheRawFileItReplaces_SoOnlyOneWorkspaceSymbolIsFound()
    {
        ScriptDatabase database = new();
        database.Gsc.Upsert(FunctionRecord(
            @"C:\raw0\maps\_utility.gsc", "raw", @"maps\_utility", "util"));
        database.Gsc.Upsert(FunctionRecord(
            @"C:\mods0\zm_grief\maps\_utility.gsc", "mod:zm_grief", @"maps\_utility", "util"));

        WorkspaceSymbolHandler handler = new(database);
        WorkspaceSymbolParams request = new() { Query = "util" };

        Container<WorkspaceSymbol>? result = await handler.Handle(request, CancellationToken.None);

        // Only one "util" should be offered — the raw copy is dead code the engine never loads,
        // and showing it as a second result sends the user to a file that plays no part at all.
        Assert.Single(result!);
    }
}
