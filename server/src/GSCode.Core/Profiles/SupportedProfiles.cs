namespace GSCode.Core;

/// <summary>
/// The mainline Call of Duty lineage as <see cref="GameProfile"/>s. Five are SUPPORTED, with
/// capabilities verified against each game's real scripts — CoD4, WaW, MW2, BO1, BO3 — though only
/// BO3 is fully <see cref="GameProfile.Verified"/> (implemented end to end). Every other game up to
/// BO6 is a CORE: a nameable identity over the shared base dialect, its specifics deliberately left
/// unset for a contributor to fill in and promote. Each game's keyword set is <c>[..BaseKeywords, …]</c>
/// — the base every game shares, plus that dialect's own additions.
/// </summary>
public sealed partial record GameProfile
{
    // --- Supported: capabilities verified against each game's mod-tools scripts. ---

    /// <summary>
    /// Call of Duty 4: Modern Warfare (2007) — Infinity Ward. The base dialect itself: #include
    /// merge, path/:: calls, ///ScriptDoc, no foreach/classes. The one non-BO3 game with bundled
    /// data (functions, radiant keys, fields from the mod tools), so it sets a data prefix.
    /// </summary>
    public static GameProfile Cod4 { get; } = new()
    {
        Id = "cod4",
        ShortName = "cod4",
        DisplayName = "Call of Duty 4: Modern Warfare",
        ReleaseYear = 2007,
        Family = EngineFamily.InfinityWard,
        Supported = true,
        Verified = true,
        HasInlinePathCalls = true,
        DataFilePrefix = "cod4",
        HasCompleteBuiltinLibrary = true,
        Keywords = [.. BaseKeywords, "prof_begin", "prof_end"],
    };

    /// <summary>Call of Duty: World at War (2008) — Treyarch. The base plus client scripts (.csc).</summary>
    public static GameProfile WorldAtWar { get; } = new()
    {
        Id = "waw",
        ShortName = "waw",
        DisplayName = "Call of Duty: World at War",
        ReleaseYear = 2008,
        Family = EngineFamily.Treyarch,
        Supported = true,
        Verified = true,
        HasInlinePathCalls = true,
        HasClientScripts = true,
        DataFilePrefix = "waw",
        Keywords = [.. BaseKeywords, "prof_begin", "prof_end"],
    };

    /// <summary>
    /// Call of Duty: Modern Warfare 2 (2009) — Infinity Ward. The IW line's keyword additions over
    /// the base: <c>foreach</c> (+ its <c>in</c>), <c>childthread</c> and <c>call</c>. Also file-scope
    /// constants (<c>CONST = 4;</c>).
    /// </summary>
    public static GameProfile ModernWarfare2 { get; } = new()
    {
        Id = "mw2",
        ShortName = "mw2",
        DisplayName = "Call of Duty: Modern Warfare 2",
        ReleaseYear = 2009,
        Family = EngineFamily.InfinityWard,
        Supported = true,
        Verified = true,
        HasInlinePathCalls = true,
        HasFileScopeConstants = true,
        Keywords = [.. BaseKeywords, "foreach", "in", "childthread", "call", "prof_begin", "prof_end"],
    };

    /// <summary>
    /// Call of Duty: Black Ops (2010) — Treyarch. Base plus client scripts and hash strings
    /// (<c>#"…"</c>); the Treyarch line does NOT get <c>foreach</c> until BO3, so its keywords are
    /// the base.
    /// </summary>
    public static GameProfile BlackOps { get; } = new()
    {
        Id = "bo1",
        ShortName = "bo1",
        DisplayName = "Call of Duty: Black Ops",
        ReleaseYear = 2010,
        Family = EngineFamily.Treyarch,
        Supported = true,
        Verified = true,
        HasInlinePathCalls = true,
        HasClientScripts = true,
        HasHashStrings = true,
        DataFilePrefix = "bo1",
        Keywords = [.. BaseKeywords, "prof_begin", "prof_end"],
    };

    /// <summary>
    /// Call of Duty: Black Ops III (2015) — Treyarch T7. The verified target and the wholesale
    /// rewrite: <c>#using</c> namespaces (no <c>#include</c>), <c>&amp;</c> pointers, <c>/@ @/</c>
    /// ScriptDoc, headers, classes, <c>function</c>, arrays by reference, and the full T7 keyword set.
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
        DataFilePrefix = "t7",
        HasCompleteBuiltinLibrary = true,
        HasClientScripts = true,
        HasHeaders = true,
        HasWorldObject = true,
        HasNamespaceDirective = true,
        ImportStyle = ImportStyle.Namespace,
        FunctionPointerStyle = FunctionPointerStyle.Ampersand,
        ScriptDocStyle = ScriptDocStyle.AtSign,
        ArraysPassedByReference = true,
        HasHashStrings = true,
        HasPrecacheDirective = true,
        // BO3 dropped inline path calls: a function is reached by #using + ns::foo, never by path.
        HasInlinePathCalls = false,
        Keywords =
        [
            .. BaseKeywords,
            // The IW-line loop BO3 also carries (childthread/call are not used in T7).
            "foreach", "in",
            // BO3's own additions: the class system (grouped), function decls + modifiers, and its
            // extra intrinsics.
            .. ClassKeywords,
            "do", "function", "autoexec", "private", "const",
            "waitrealtime", "vectorscale", "profilestart", "profilestop",
        ],
    };

    // --- Cores: identity over the shared base dialect, capabilities unset (Supported/Verified false).
    //     A contributor with a game's mod tools fills one in and promotes it out of Core(). ---

    public static GameProfile ModernWarfare3 { get; } = Core("mw3", "Call of Duty: Modern Warfare 3", 2011, EngineFamily.InfinityWard);
    public static GameProfile BlackOps2 { get; } = Core("bo2", "Call of Duty: Black Ops II", 2012, EngineFamily.Treyarch);
    public static GameProfile Ghosts { get; } = Core("ghosts", "Call of Duty: Ghosts", 2013, EngineFamily.InfinityWard);
    public static GameProfile AdvancedWarfare { get; } = Core("aw", "Call of Duty: Advanced Warfare", 2014, EngineFamily.SledgehammerGames);
    public static GameProfile InfiniteWarfare { get; } = Core("iw", "Call of Duty: Infinite Warfare", 2016, EngineFamily.InfinityWard);
    public static GameProfile WorldWar2 { get; } = Core("wwii", "Call of Duty: WWII", 2017, EngineFamily.SledgehammerGames);
    public static GameProfile BlackOps4 { get; } = Core("bo4", "Call of Duty: Black Ops 4", 2018, EngineFamily.Treyarch);
    public static GameProfile ModernWarfare2019 { get; } = Core("mw19", "Call of Duty: Modern Warfare (2019)", 2019, EngineFamily.InfinityWard);
    public static GameProfile BlackOpsColdWar { get; } = Core("bocw", "Call of Duty: Black Ops Cold War", 2020, EngineFamily.Treyarch);
    public static GameProfile Vanguard { get; } = Core("vg", "Call of Duty: Vanguard", 2021, EngineFamily.SledgehammerGames);
    public static GameProfile ModernWarfare2_2022 { get; } = Core("mw22", "Call of Duty: Modern Warfare II (2022)", 2022, EngineFamily.InfinityWard);
    public static GameProfile ModernWarfare3_2023 { get; } = Core("mw23", "Call of Duty: Modern Warfare III (2023)", 2023, EngineFamily.SledgehammerGames);
    public static GameProfile BlackOps6 { get; } = Core("bo6", "Call of Duty: Black Ops 6", 2024, EngineFamily.Treyarch);

    /// <summary>
    /// A core profile: a nameable identity over the shared base dialect, with nothing game-specific
    /// input. Everything defaults to the base IW-style shape (Keywords = BaseKeywords, #include merge,
    /// path calls), Supported and Verified both false, until someone fills it in from that game's
    /// tools.
    /// </summary>
    private static GameProfile Core(string shortName, string displayName, int year, EngineFamily family)
    {
        return new GameProfile
        {
            Id = shortName,
            ShortName = shortName,
            DisplayName = displayName,
            ReleaseYear = year,
            Family = family,
            HasInlinePathCalls = true,
        };
    }
}
