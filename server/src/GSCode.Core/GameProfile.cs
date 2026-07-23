using System.Collections.Immutable;
using GSCode.Core.Symbols;

namespace GSCode.Core;

/// <summary>How a script pulls in another file's functions — the two CoD families differ.</summary>
public enum ImportStyle
{
    /// <summary>T7: <c>#using</c> imports a NAMESPACE; calls into it stay qualified (<c>ns::foo</c>).</summary>
    Namespace,

    /// <summary>IW: <c>#include</c> MERGES the file's functions into this scope; calls are unqualified.</summary>
    Include,
}

/// <summary>Which studio's engine lineage a game belongs to — the biggest predictor of its dialect.</summary>
public enum EngineFamily
{
    Unknown,
    InfinityWard,
    Treyarch,
    SledgehammerGames,
}

/// <summary>How a function pointer is written — this changed at BO3.</summary>
public enum FunctionPointerStyle
{
    /// <summary>
    /// Pre-BO3 / IW: a bare qualified name IS the pointer — <c>level.f = maps\mp\_utility::foo;</c>
    /// (no parentheses), and <c>::foo</c> for a function in this file. Parentheses would call it.
    /// </summary>
    PathQualified,

    /// <summary>
    /// BO3 / T7: an explicit <c>&amp;</c> makes the pointer — <c>level.f = &amp;foo;</c> or
    /// <c>&amp;namespace::foo;</c>. A bare <c>ns::foo</c> is a call, never a pointer.
    /// </summary>
    Ampersand,
}

/// <summary>
/// The portability seam: every piece of game-specific knowledge (extensions, global object names,
/// which language features exist, how imports work, where scripts live) is reached through this
/// profile, never via inline constants. A future GSC-dialect port supplies a new profile instead of
/// touching engine logic.
///
/// Reach the current profile through <see cref="Active"/>. Today that is always
/// <see cref="BlackOps3"/> — the one place the game is chosen — and a dialect port (roadmap D1)
/// makes the choice per workspace. Every call site already goes through it, so that change lands
/// here rather than being scattered.
/// </summary>
public sealed record GameProfile
{
    /// <summary>Short identifier used in logs and cache metadata, e.g. "t7".</summary>
    public required string Id { get; init; }

    /// <summary>The game's common abbreviation used to select it, e.g. "bo3", "cod4", "mw2".</summary>
    public required string ShortName { get; init; }

    /// <summary>Human-readable game name shown in diagnostics and docs.</summary>
    public required string DisplayName { get; init; }

    /// <summary>Release year, so the lineage keeps a sensible order.</summary>
    public int ReleaseYear { get; init; }

    /// <summary>The studio engine lineage this game belongs to.</summary>
    public EngineFamily Family { get; init; } = EngineFamily.Unknown;

    /// <summary>
    /// Whether this profile's capabilities have been confirmed against real scripts. Only BO3 is
    /// verified; every other entry is a SHELL with placeholder capabilities, present so the game is
    /// nameable and its facts can be filled in, not relied on yet.
    /// </summary>
    public bool Verified { get; init; }

    /// <summary>Extension for server-side scripts, including the dot (".gsc").</summary>
    public string ServerScriptExtension { get; init; } = ".gsc";

    /// <summary>Extension for client-side scripts, including the dot (".csc").</summary>
    public string ClientScriptExtension { get; init; } = ".csc";

    /// <summary>Extension for preprocessor-injectable header files, including the dot (".gsh").</summary>
    public string HeaderExtension { get; init; } = ".gsh";

    /// <summary>Built-in global object names (level, game, world, ...) the language exposes.</summary>
    public ImmutableArray<string> GlobalObjectNames { get; init; } = [];

    /// <summary>File names of the bundled data artifacts this profile loads from the Api folder.</summary>
    public ImmutableArray<string> BundledDataFileNames { get; init; } = [];

    // --- Capabilities: which language features and worlds exist in this dialect. Shells leave
    //     these at the conservative defaults below until confirmed. ---

    /// <summary>Whether the game has client-side scripts (<c>.csc</c>). CSC is a Treyarch feature.</summary>
    public bool HasClientScripts { get; init; }

    /// <summary>Whether the game has preprocessor headers (<c>.gsh</c> / <c>#insert</c>). BO3 onward.</summary>
    public bool HasHeaders { get; init; }

    /// <summary>Whether the language has classes (<c>class</c>, <c>new</c>, <c>-&gt;</c>). T7 only.</summary>
    public bool HasClasses { get; init; }

    /// <summary>Whether a function declaration begins with the <c>function</c> keyword. IW omits it.</summary>
    public bool HasFunctionKeyword { get; init; }

    /// <summary>Whether a file declares its namespace with <c>#namespace</c>. IW keys off the path.</summary>
    public bool HasNamespaceDirective { get; init; }

    /// <summary>How imports work — see <see cref="Core.ImportStyle"/>.</summary>
    public ImportStyle ImportStyle { get; init; } = ImportStyle.Include;

    /// <summary>How a function pointer is written — see <see cref="Core.FunctionPointerStyle"/>.</summary>
    public FunctionPointerStyle FunctionPointerStyle { get; init; } = FunctionPointerStyle.PathQualified;

    /// <summary>
    /// Whether array parameters are passed by reference. BO3 passes arrays by reference ONLY;
    /// earlier games copy them by value, so a callee mutating an array does not affect the caller's.
    /// </summary>
    public bool ArraysPassedByReference { get; init; }

    // --- Root discovery: where the game's scripts live. ---

    /// <summary>The environment variable naming the game's tools install, or null if it has none.</summary>
    public string? RootEnvironmentVariable { get; init; }

    /// <summary>The raw-scripts folder relative to the tools install (e.g. <c>share\raw</c>), or null.</summary>
    public string? RawSubfolder { get; init; }

    /// <summary>The mods folder relative to the tools install (e.g. <c>mods</c>), or null.</summary>
    public string? ModsSubfolder { get; init; }

    /// <summary>The extension for a language world, including the dot.</summary>
    public string ExtensionFor(ScriptLanguage language)
    {
        switch ( language )
        {
            case ScriptLanguage.Csc:
                return ClientScriptExtension;
            case ScriptLanguage.Gsh:
                return HeaderExtension;
            default:
                return ServerScriptExtension;
        }
    }

    /// <summary>The language world an extension belongs to; server-side is the default.</summary>
    public ScriptLanguage LanguageFromExtension(string extension)
    {
        if ( HasClientScripts && string.Equals(extension, ClientScriptExtension, StringComparison.OrdinalIgnoreCase) )
        {
            return ScriptLanguage.Csc;
        }

        if ( HasHeaders && string.Equals(extension, HeaderExtension, StringComparison.OrdinalIgnoreCase) )
        {
            return ScriptLanguage.Gsh;
        }

        return ScriptLanguage.Gsc;
    }

    /// <summary>The language world a file path belongs to, from its extension (defaults to server).</summary>
    public ScriptLanguage LanguageFromPath(string filePath)
    {
        return LanguageFromExtension(Path.GetExtension(filePath));
    }

    /// <summary>Every script extension this game uses, in world order (server, client, header).</summary>
    public ImmutableArray<string> ScriptExtensions
    {
        get
        {
            ImmutableArray<string>.Builder extensions = ImmutableArray.CreateBuilder<string>(3);
            extensions.Add(ServerScriptExtension);
            if ( HasClientScripts )
            {
                extensions.Add(ClientScriptExtension);
            }

            if ( HasHeaders )
            {
                extensions.Add(HeaderExtension);
            }

            return extensions.ToImmutable();
        }
    }

    /// <summary>A glob for each script extension (e.g. <c>*.gsc</c>), for file enumeration and watchers.</summary>
    public ImmutableArray<string> ScriptGlobs
    {
        get { return [.. ScriptExtensions.Select(static extension => "*" + extension)]; }
    }

    /// <summary>
    /// The Black Ops 3 (Treyarch T7) profile — the only game the rewrite targets today, and the
    /// only one whose capabilities are confirmed.
    /// </summary>
    public static GameProfile BlackOps3 { get; } = new()
    {
        Id = "t7",
        ShortName = "bo3",
        DisplayName = "Call of Duty: Black Ops III",
        ReleaseYear = 2015,
        Family = EngineFamily.Treyarch,
        Verified = true,
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
        HasClientScripts = true,
        HasHeaders = true,
        HasClasses = true,
        HasFunctionKeyword = true,
        HasNamespaceDirective = true,
        ImportStyle = ImportStyle.Namespace,
        FunctionPointerStyle = FunctionPointerStyle.Ampersand,
        ArraysPassedByReference = true,
        RootEnvironmentVariable = "TA_TOOLS_PATH",
        RawSubfolder = @"share\raw",
        ModsSubfolder = "mods",
    };

    /// <summary>
    /// An unconfirmed placeholder for a game we do not support yet: it can be named and ordered, but
    /// every capability sits at the conservative default until someone fills it in against real
    /// scripts and sets <see cref="Verified"/>. The family is the one fact worth recording up front,
    /// since it is the strongest hint about the dialect.
    /// </summary>
    private static GameProfile Shell(string shortName, string displayName, int year, EngineFamily family)
    {
        return new GameProfile
        {
            Id = shortName,
            ShortName = shortName,
            DisplayName = displayName,
            ReleaseYear = year,
            Family = family,
        };
    }

    /// <summary>
    /// Every mainline game from Call of Duty 4 to Black Ops 6, in release order. All but
    /// <see cref="BlackOps3"/> are shells (<see cref="Verified"/> false) — present so the lineage is
    /// nameable and each game's facts have a home, to be filled in as dialect support arrives.
    /// </summary>
    public static ImmutableArray<GameProfile> All { get; } =
    [
        Shell("cod4", "Call of Duty 4: Modern Warfare", 2007, EngineFamily.InfinityWard),
        Shell("waw", "Call of Duty: World at War", 2008, EngineFamily.Treyarch),
        Shell("mw2", "Call of Duty: Modern Warfare 2", 2009, EngineFamily.InfinityWard),
        Shell("bo1", "Call of Duty: Black Ops", 2010, EngineFamily.Treyarch),
        Shell("mw3", "Call of Duty: Modern Warfare 3", 2011, EngineFamily.InfinityWard),
        Shell("bo2", "Call of Duty: Black Ops II", 2012, EngineFamily.Treyarch),
        Shell("ghosts", "Call of Duty: Ghosts", 2013, EngineFamily.InfinityWard),
        Shell("aw", "Call of Duty: Advanced Warfare", 2014, EngineFamily.SledgehammerGames),
        BlackOps3,
        Shell("iw", "Call of Duty: Infinite Warfare", 2016, EngineFamily.InfinityWard),
        Shell("wwii", "Call of Duty: WWII", 2017, EngineFamily.SledgehammerGames),
        Shell("bo4", "Call of Duty: Black Ops 4", 2018, EngineFamily.Treyarch),
        Shell("mw19", "Call of Duty: Modern Warfare", 2019, EngineFamily.InfinityWard),
        Shell("bocw", "Call of Duty: Black Ops Cold War", 2020, EngineFamily.Treyarch),
        Shell("vg", "Call of Duty: Vanguard", 2021, EngineFamily.SledgehammerGames),
        Shell("mw22", "Call of Duty: Modern Warfare II", 2022, EngineFamily.InfinityWard),
        Shell("mw23", "Call of Duty: Modern Warfare III", 2023, EngineFamily.SledgehammerGames),
        Shell("bo6", "Call of Duty: Black Ops 6", 2024, EngineFamily.Treyarch),
    ];

    /// <summary>The profile whose short name or id matches (case-insensitive), or null.</summary>
    public static GameProfile? ByName(string name)
    {
        foreach ( GameProfile profile in All )
        {
            if ( string.Equals(profile.ShortName, name, StringComparison.OrdinalIgnoreCase)
                || string.Equals(profile.Id, name, StringComparison.OrdinalIgnoreCase) )
            {
                return profile;
            }
        }

        return null;
    }

    /// <summary>
    /// The profile in force. A single fixed profile today — BO3 — and the seam a dialect port turns
    /// into a per-workspace choice (roadmap D1). Kept as one accessor so that change is one edit,
    /// not a sweep of every call site.
    /// </summary>
    public static GameProfile Active => BlackOps3;
}
