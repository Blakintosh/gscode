using GSCode.Core;
using GSCode.Server.Handlers;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using Xunit;

namespace GSCode.Server.Tests.Handlers;

/// <summary>
/// On an include dialect the engine finds a function only through the paths a script provides:
/// the file a path call names, else the script's own declarations, else what its <c>#include</c>
/// chain merges in. It never looks the name up across the install. So a script that includes
/// <c>maps\mp\_utility</c> and not <c>maps\_utility</c> cannot reach anything only the second
/// declares, and navigation must not land there either.
/// </summary>
public sealed class IncludeScopedNavigationTests
{
    private const string CallerPath = @"maps\mp\caller.gsc";

    private const string CallerSource =
        "#include maps\\mp\\_utility;\n"               // 0
        + "\n"                                         // 1
        + "main()\n"                                   // 2
        + "{\n"                                        // 3
        + "    sp_only();\n"                           // 4  name at 4
        + "    shared_name();\n"                       // 5  name at 4
        + "    maps\\_utility::array_thread();\n"      // 6  name at 19
        + "    spawn( \"script_origin\" );\n"          // 7  name at 4
        + "}\n";

    private static readonly TestFile[] s_files =
    [
        new TestFile(@"maps\_utility.gsc",
            "#include common_scripts\\utility;\n\nsp_only( sp_b )\n{\n}\n\nshared_name( sp_a )\n{\n}\n\nspawn( sp_spawn )\n{\n}\n"),
        new TestFile(@"maps\mp\_utility.gsc", "shared_name( mp_a )\n{\n}\n"),
        new TestFile(@"common_scripts\utility.gsc", "array_thread( ents_param )\n{\n}\n"),
        new TestFile(@"maps\other\utility.gsc", "array_thread( other_param )\n{\n}\n"),
        new TestFile(CallerPath, CallerSource),
    ];

    private static async Task<List<string>> DefinitionFilesAsync(int line, int character)
    {
        using HandlerWorkspace workspace = await HandlerWorkspace.BuildAsync(s_files, GameProfile.Cod4);
        workspace.Open(CallerPath);

        LocationOrLocationLinks? result = await new DefinitionHandler(workspace.Navigation, HandlerWorkspace.Selector).Handle(
            new DefinitionParams { TextDocument = HandlerWorkspace.Identify(CallerPath), Position = new Position(line, character) },
            CancellationToken.None);

        if ( result is null )
        {
            return [];
        }

        return [.. result.Select(static link => link.Location!.Uri.GetFileSystemPath().ToLowerInvariant())];
    }

    [Fact]
    public async Task ANameOnlyAnUnincludedFileDeclares_HasNoDefinition()
    {
        Assert.Empty(await DefinitionFilesAsync(4, 4));
    }

    [Fact]
    public async Task ANameBothFilesDeclare_IsTheIncludedFiles()
    {
        string only = Assert.Single(await DefinitionFilesAsync(5, 4));

        Assert.EndsWith(@"maps\mp\_utility.gsc", only);
    }

    [Fact]
    public async Task APathCall_ToAFunctionTheNamedFileOnlyIncludes_HasNoDefinition()
    {
        // maps\_utility includes common_scripts\utility, but a far call sees the named file's own
        // functions, never what that file includes.
        Assert.Empty(await DefinitionFilesAsync(6, 19));
    }

    [Fact]
    public async Task AnEngineFunction_IsNotHiddenByAScriptFunctionTheFileCannotReach()
    {
        // maps\_utility declares its own spawn(), but this script does not include it.
        using HandlerWorkspace workspace = await HandlerWorkspace.BuildAsync(s_files, GameProfile.Cod4);
        workspace.Open(CallerPath);

        BuiltinAtResponse response = await new BuiltinAtHandler(workspace.Navigation, workspace.Builtins).Handle(
            new BuiltinAtParams
            {
                Uri = OmniSharp.Extensions.LanguageServer.Protocol.DocumentUri.FromFileSystemPath(TestPaths.Raw(CallerPath)).ToString(),
                Line = 7,
                Character = 4,
            },
            CancellationToken.None);

        Assert.Equal("spawn", response.Name, ignoreCase: true);
    }
}
