namespace GSCode.Core;

/// <summary>
/// The games the extension targets: Call of Duty 4 through Black Ops 3. Each is a static profile on
/// the <see cref="GameProfile"/> partial, kept here so the main file stays the record and registry.
/// Only <see cref="BlackOps3"/> is <see cref="GameProfile.Verified"/>; the rest carry the shared
/// pre-BO3 shape via <see cref="GameProfile.Targeted"/> plus the few facts that vary.
/// </summary>
public sealed partial record GameProfile
{
    /// <summary>
    /// Call of Duty 4: Modern Warfare (2007) — the original Infinity Ward GSC; no foreach. The one
    /// non-BO3 game with bundled data (engine functions, radiant keys and entity fields, names-only,
    /// from the mod-tools wordfile), so it sets a <see cref="GameProfile.DataFilePrefix"/>.
    /// </summary>
    public static GameProfile Cod4 { get; } =
        Targeted("cod4", "Call of Duty 4: Modern Warfare", 2007, EngineFamily.InfinityWard, hasForeach: false)
            with { DataFilePrefix = "cod4" };

    /// <summary>Call of Duty: World at War (2008) — Treyarch, first with client scripts; no foreach.</summary>
    public static GameProfile WorldAtWar { get; } =
        Targeted("waw", "Call of Duty: World at War", 2008, EngineFamily.Treyarch, hasClientScripts: true, hasForeach: false);

    /// <summary>Call of Duty: Modern Warfare 2 (2009) — has file-scope constants.</summary>
    public static GameProfile ModernWarfare2 { get; } =
        Targeted("mw2", "Call of Duty: Modern Warfare 2", 2009, EngineFamily.InfinityWard, hasFileScopeConstants: true);

    /// <summary>Call of Duty: Black Ops (2010) — Treyarch; first with hash strings.</summary>
    public static GameProfile BlackOps { get; } =
        Targeted("bo1", "Call of Duty: Black Ops", 2010, EngineFamily.Treyarch, hasClientScripts: true, hasHashStrings: true);

    /// <summary>Call of Duty: Modern Warfare 3 (2011).</summary>
    public static GameProfile ModernWarfare3 { get; } =
        Targeted("mw3", "Call of Duty: Modern Warfare 3", 2011, EngineFamily.InfinityWard);

    /// <summary>Call of Duty: Black Ops II (2012) — Treyarch.</summary>
    public static GameProfile BlackOps2 { get; } =
        Targeted("bo2", "Call of Duty: Black Ops II", 2012, EngineFamily.Treyarch, hasClientScripts: true, hasHashStrings: true);

    /// <summary>Call of Duty: Ghosts (2013).</summary>
    public static GameProfile Ghosts { get; } =
        Targeted("ghosts", "Call of Duty: Ghosts", 2013, EngineFamily.InfinityWard);

    /// <summary>Call of Duty: Advanced Warfare (2014) — Sledgehammer.</summary>
    public static GameProfile AdvancedWarfare { get; } =
        Targeted("aw", "Call of Duty: Advanced Warfare", 2014, EngineFamily.SledgehammerGames);

    /// <summary>
    /// Call of Duty: Black Ops III (2015) — Treyarch T7, the one game the rewrite targets today and
    /// the only profile whose capabilities are verified.
    /// </summary>
    public static GameProfile BlackOps3 { get; } = new()
    {
        Id = "t7",
        ShortName = "bo3",
        DisplayName = "Call of Duty: Black Ops III",
        ReleaseYear = 2015,
        Family = EngineFamily.Treyarch,
        Verified = true,
        Supported = true,
        ServerScriptExtension = ".gsc",
        ClientScriptExtension = ".csc",
        HeaderExtension = ".gsh",
        DataFilePrefix = "t7",
        HasClientScripts = true,
        HasHeaders = true,
        HasClasses = true,
        HasWorldObject = true,
        HasFunctionKeyword = true,
        HasNamespaceDirective = true,
        ImportStyle = ImportStyle.Namespace,
        FunctionPointerStyle = FunctionPointerStyle.Ampersand,
        ScriptDocStyle = ScriptDocStyle.AtSign,
        ArraysPassedByReference = true,
        HasHashStrings = true,
        HasPrecacheDirective = true,
        HasForeach = true,
        HasDoWhile = true,
        // BO3 dropped inline path calls: a function is reached by #using + ns::foo, never by path.
        HasInlinePathCalls = false,
        RootEnvironmentVariable = "TA_TOOLS_PATH",
        RawSubfolder = @"share\raw",
        ModsSubfolder = "mods",
    };
}
