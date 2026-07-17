using System.Collections.Immutable;

namespace GSCode.Core;

/// <summary>
/// The portability seam: every piece of game-specific knowledge (extensions, global object
/// names, bundled data-file names) is reached through this profile, never via inline constants.
/// A future GSC-dialect port supplies a new profile instead of touching engine logic.
/// </summary>
public sealed record GameProfile
{
    /// <summary>Short identifier used in logs and cache metadata, e.g. "t7".</summary>
    public required string Id { get; init; }

    /// <summary>Human-readable game name shown in diagnostics and docs.</summary>
    public required string DisplayName { get; init; }

    /// <summary>Extension for server-side scripts, including the dot (".gsc").</summary>
    public required string ServerScriptExtension { get; init; }

    /// <summary>Extension for client-side scripts, including the dot (".csc").</summary>
    public required string ClientScriptExtension { get; init; }

    /// <summary>Extension for preprocessor-injectable header files, including the dot (".gsh").</summary>
    public required string HeaderExtension { get; init; }

    /// <summary>Built-in global object names (level, game, world, ...) the language exposes.</summary>
    public required ImmutableArray<string> GlobalObjectNames { get; init; }

    /// <summary>File names of the bundled data artifacts this profile loads from the Api folder.</summary>
    public required ImmutableArray<string> BundledDataFileNames { get; init; }

    /// <summary>The Black Ops 3 (Treyarch T7) profile — the only game the rewrite targets initially.</summary>
    public static GameProfile BlackOps3 { get; } = new()
    {
        Id = "t7",
        DisplayName = "Call of Duty: Black Ops III",
        ServerScriptExtension = ".gsc",
        ClientScriptExtension = ".csc",
        HeaderExtension = ".gsh",
        GlobalObjectNames = ["self", "level", "game", "world", "anim", "classes"],
        BundledDataFileNames =
        [
            "t7_api_gsc.json",
            "t7_api_csc.json",
            "t7_stock_scripts.txt",
        ],
    };
}
