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

/// <summary>Which markers a ScriptDoc documentation comment uses — BO3 differs from the rest.</summary>
public enum ScriptDocStyle
{
    /// <summary>
    /// Pre-BO3 (both Infinity Ward and Treyarch): a doc block sits inside an ordinary
    /// <c>/* … */</c> comment, fenced by <c>///ScriptDocBegin</c> and <c>///ScriptDocEnd</c> lines.
    /// </summary>
    TripleSlash,

    /// <summary>BO3 delimits a doc comment with <c>/@ … @/</c> (its <c>/# #/</c> is a dev block).</summary>
    AtSign,
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
public sealed partial record GameProfile
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
    /// Whether this dialect is proven against the game's OWN scripts, not just filled in from a
    /// worksheet. The bar is the corpus gate: every script in the game's script tree analyses without
    /// throwing, lex/parse errors stay under 1% (the residue being genuinely malformed files that no
    /// compiler would accept either), and the formatter round-trips a sample of them without changing
    /// a token or moving after a second pass.
    ///
    /// All five SUPPORTED games clear it. A CORE never does — it carries no game-specific capabilities
    /// to prove.
    /// </summary>
    public bool Verified { get; init; }

    /// <summary>
    /// Whether this game's capabilities are filled in and verified against its real scripts. True for
    /// the five supported games (CoD4, WaW, MW2, BO1, BO3); false for every CORE, whose capabilities
    /// are left at the base until a contributor fills them in — including the pre-BO3 cores (MW3, BO2,
    /// Ghosts, AW), which are not supported despite predating BO3.
    /// </summary>
    public bool Supported { get; init; }

    /// <summary>Extension for server-side scripts, including the dot (".gsc").</summary>
    public string ServerScriptExtension { get; init; } = ".gsc";

    /// <summary>Extension for client-side scripts, including the dot (".csc").</summary>
    public string ClientScriptExtension { get; init; } = ".csc";

    /// <summary>Extension for preprocessor-injectable header files, including the dot (".gsh").</summary>
    public string HeaderExtension { get; init; } = ".gsh";

    /// <summary>
    /// The filename prefix for this game's bundled data files — the builtin API, object fields,
    /// radiant keys and stock-script list — e.g. <c>"t7"</c> gives <c>t7_api_gsc.json</c>. Null when
    /// the game ships none (every profile but BO3 today), in which case a workspace on that game
    /// loads no builtin data rather than BO3's, and the loaders read their names from here rather
    /// than hardcoding one game's.
    /// </summary>
    public string? DataFilePrefix { get; init; }

    // --- Capabilities: which language features and worlds exist in this dialect. Cores leave
    //     these at the conservative base defaults below until a contributor confirms them. ---

    /// <summary>Whether the game has client-side scripts (<c>.csc</c>). CSC is a Treyarch feature.</summary>
    public bool HasClientScripts { get; init; }

    /// <summary>Whether the game has preprocessor headers (<c>.gsh</c> / <c>#insert</c>). BO3 onward.</summary>
    public bool HasHeaders { get; init; }

    /// <summary>
    /// The language keywords this dialect recognizes. A word is a keyword only if it is in this set,
    /// so a dialect that lacks one (e.g. <c>foreach</c> before MW2, <c>function</c>/<c>class</c> in
    /// the Infinity Ward games) leaves it an ordinary identifier. Built as <c>[..BaseKeywords, …]</c>:
    /// the base is the CoD4/WaW/BO1 set that every game shares, and each dialect adds its own on top.
    /// Directives are gated separately by their feature flags, not listed here.
    /// </summary>
    public ImmutableArray<string> Keywords { get; init; } = BaseKeywords;

    /// <summary>The keywords every CoD GSC dialect has — the true base MW2 and BO3 extend.</summary>
    public static readonly ImmutableArray<string> BaseKeywords =
    [
        "if", "else", "for", "while", "switch", "case", "default", "break", "continue", "return",
        "thread", "wait", "waittill", "waittillmatch", "waittillframeend", "notify", "endon",
        "isdefined", "assert", "assertmsg", "true", "false", "undefined",
    ];

    /// <summary>
    /// The class-system keywords, kept as one group so a dialect adds them together with a single
    /// <c>.. ClassKeywords</c> — they all arrive with BO3's class system and none exists without it.
    /// <see cref="HasClasses"/> keys off <c>class</c>, so including this group turns the whole class
    /// feature on. <c>autoexec</c>/<c>private</c> are NOT here — they are function modifiers, not
    /// part of the class system, so a dialect can have them without classes.
    /// </summary>
    public static readonly ImmutableArray<string> ClassKeywords =
        ["class", "var", "new", "constructor", "destructor"];

    private bool HasKeyword(string keyword)
    {
        return Keywords.Contains(keyword, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Whether the given word is a keyword in this dialect. Called only after the central table has
    /// already recognised the word as a possible keyword, so the scan is over the profile's own set
    /// (~two dozen entries) and allocation-free — no per-identifier cost on the lexer's hot path.
    /// </summary>
    public bool IsKeyword(ReadOnlySpan<char> word)
    {
        foreach ( string keyword in Keywords )
        {
            if ( word.Equals(keyword, StringComparison.OrdinalIgnoreCase) )
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Whether the language has classes (<c>class</c>, <c>new</c>, <c>-&gt;</c>). T7 only. Derived from the keyword set.</summary>
    public bool HasClasses => HasKeyword("class");

    /// <summary>
    /// Whether the engine exposes the <c>world</c> global object. Added in BO3 and present in the
    /// Treyarch games from then on (BO4, Cold War); the earlier games and the Infinity Ward line
    /// have <c>self</c>/<c>level</c>/<c>game</c>/<c>anim</c> but no <c>world</c>.
    /// </summary>
    public bool HasWorldObject { get; init; }

    /// <summary>Whether a function declaration begins with the <c>function</c> keyword. IW omits it. Derived from the keyword set.</summary>
    public bool HasFunctionKeyword => HasKeyword("function");

    /// <summary>Whether a file declares its namespace with <c>#namespace</c>. IW keys off the path.</summary>
    public bool HasNamespaceDirective { get; init; }

    /// <summary>How imports work — see <see cref="Core.ImportStyle"/>.</summary>
    public ImportStyle ImportStyle { get; init; } = ImportStyle.Include;

    /// <summary>How a function pointer is written — see <see cref="Core.FunctionPointerStyle"/>.</summary>
    public FunctionPointerStyle FunctionPointerStyle { get; init; } = FunctionPointerStyle.PathQualified;

    /// <summary>Which markers a ScriptDoc comment uses — see <see cref="Core.ScriptDocStyle"/>.</summary>
    public ScriptDocStyle ScriptDocStyle { get; init; } = ScriptDocStyle.TripleSlash;

    /// <summary>
    /// Whether array parameters are passed by reference. BO3 passes arrays by reference ONLY;
    /// earlier games copy them by value, so a callee mutating an array does not affect the caller's.
    /// </summary>
    public bool ArraysPassedByReference { get; init; }

    /// <summary>
    /// Whether a constant can be declared at file scope by assigning a variable outside any function
    /// (<c>CONST_FOO = 4;</c>). MW2 has these; BO3 uses <c>#define</c> instead and rejects a bare
    /// top-level assignment. Parser support for this form is future work (roadmap D2).
    /// </summary>
    public bool HasFileScopeConstants { get; init; }

    /// <summary>
    /// Whether a function can be reached inline by its file PATH — <c>maps\mp\_utility::foo()</c> to
    /// call it, <c>maps\mp\_utility::foo</c> (no parens) as a pointer — without importing the file.
    /// Every pre-BO3 game does this; BO3 replaced it with a
    /// <c>#using</c> import and namespace-qualified <c>ns::foo</c> calls, and has none.
    /// </summary>
    public bool HasInlinePathCalls { get; init; }

    /// <summary>
    /// Whether the language has canonicalized hash strings — <c>#"some_string"</c>, hashed at
    /// compile time. A Treyarch feature: present in BO1, BO2 and BO3; the Infinity Ward games have
    /// none.
    /// </summary>
    public bool HasHashStrings { get; init; }

    /// <summary>
    /// Whether the <c>foreach ( item in collection )</c> loop exists. Introduced in MW2 (2009) on the
    /// Infinity Ward line; the Treyarch line has none until BO3. Derived from the keyword set.
    /// </summary>
    public bool HasForeach => HasKeyword("foreach");

    /// <summary>Whether the <c>do { … } while ( … )</c> loop exists. BO3 only. Derived from the keyword set.</summary>
    public bool HasDoWhile => HasKeyword("do");

    /// <summary>
    /// Whether assets are precached with the <c>#precache( "type", "asset" )</c> directive. BO3 only;
    /// every earlier game precaches with ordinary function calls (<c>PrecacheModel( … )</c>,
    /// <c>PrecacheItem( … )</c>), which is why the directive is absent from them.
    /// </summary>
    public bool HasPrecacheDirective { get; init; }

    // --- Root discovery: where the game's scripts live. ---

    /// <summary>The environment variable naming the game's tools install, or null if it has none.</summary>
    public string? RootEnvironmentVariable { get; init; }

    /// <summary>The raw-scripts folder relative to the tools install (e.g. <c>share\raw</c>), or null.</summary>
    public string? RawSubfolder { get; init; }

    /// <summary>The mods folder relative to the tools install (e.g. <c>mods</c>), or null.</summary>
    public string? ModsSubfolder { get; init; }

    /// <summary>
    /// The builtin-API filename for a language world (e.g. <c>t7_api_gsc.json</c>), or null when
    /// this game ships no data. The world suffix is the language's own extension, so a dialect with
    /// different extensions names its files consistently.
    /// </summary>
    public string? ApiFileName(ScriptLanguage language)
    {
        return DataFilePrefix is null ? null : $"{DataFilePrefix}_api_{ExtensionFor(language).TrimStart('.')}.json";
    }

    /// <summary>The object-fields data filename (<c>t7_object_fields.json</c>), or null when none.</summary>
    public string? ObjectFieldsFileName => DataFilePrefix is null ? null : $"{DataFilePrefix}_object_fields.json";

    /// <summary>The Radiant-keys data filename (<c>t7_radiant_keys.json</c>), or null when none.</summary>
    public string? RadiantKeysFileName => DataFilePrefix is null ? null : $"{DataFilePrefix}_radiant_keys.json";

    /// <summary>The stock-script list filename (<c>t7_stock_scripts.txt</c>), or null when none.</summary>
    public string? StockScriptsFileName => DataFilePrefix is null ? null : $"{DataFilePrefix}_stock_scripts.txt";

    /// <summary>
    /// Every bundled data file this game ships, for the cache build identity. Computed from the
    /// naming above so it cannot drift from what the loaders actually read; empty when the game
    /// ships none. The client API is listed only when the game has client scripts.
    /// </summary>
    public ImmutableArray<string> BundledDataFileNames
    {
        get
        {
            if ( DataFilePrefix is null )
            {
                return [];
            }

            ImmutableArray<string>.Builder names = ImmutableArray.CreateBuilder<string>();
            names.Add(ApiFileName(ScriptLanguage.Gsc)!);
            if ( HasClientScripts )
            {
                names.Add(ApiFileName(ScriptLanguage.Csc)!);
            }

            names.Add(ObjectFieldsFileName!);
            names.Add(RadiantKeysFileName!);
            names.Add(StockScriptsFileName!);
            return names.ToImmutable();
        }
    }

    /// <summary>
    /// The engine global objects the language exposes (<c>self</c>, <c>level</c>, …), offered in
    /// statement-scope completion. <c>self</c>/<c>level</c>/<c>game</c>/<c>anim</c> are universal
    /// across the CoD lineage; <c>world</c> arrives with BO3 (see <see cref="HasWorldObject"/>) and
    /// <c>classes</c> with its class system. Owned here so completion offers exactly what the
    /// dialect has, rather than one hardcoded set for every game.
    /// </summary>
    public ImmutableArray<string> GlobalObjectNames
    {
        get
        {
            ImmutableArray<string>.Builder names = ImmutableArray.CreateBuilder<string>();
            names.Add("self");
            names.Add("level");
            names.Add("game");
            if ( HasWorldObject )
            {
                names.Add("world");
            }

            names.Add("anim");
            if ( HasClasses )
            {
                names.Add("classes");
            }

            return names.ToImmutable();
        }
    }

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

    private static readonly Lazy<ImmutableArray<GameProfile>> s_lineage = new(BuildLineage);

    /// <summary>
    /// Every mainline game from Call of Duty 4 to Black Ops 6, in release order. Five are SUPPORTED
    /// with capabilities verified against real scripts (CoD4, WaW, MW2, BO1, BO3 — only BO3 is also
    /// <see cref="Verified"/>); the rest are CORES (see <see cref="Core"/>) — nameable identities over
    /// the shared base dialect, left for a contributor to fill in. All live in
    /// <c>Profiles/SupportedProfiles.cs</c>.
    /// </summary>
    public static ImmutableArray<GameProfile> All => s_lineage.Value;

    private static ImmutableArray<GameProfile> BuildLineage()
    {
        List<GameProfile> profiles =
        [
            Cod4,
            WorldAtWar,
            ModernWarfare2,
            BlackOps,
            ModernWarfare3,
            BlackOps2,
            Ghosts,
            AdvancedWarfare,
            BlackOps3,
            InfiniteWarfare,
            WorldWar2,
            BlackOps4,
            ModernWarfare2019,
            BlackOpsColdWar,
            Vanguard,
            ModernWarfare2_2022,
            ModernWarfare3_2023,
            BlackOps6,
        ];

        profiles.Sort(static (left, right) => left.ReleaseYear.CompareTo(right.ReleaseYear));
        return [.. profiles];
    }

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

    private static GameProfile? s_active;

    /// <summary>
    /// The profile in force. BO3 by default, changed by <see cref="Select"/> from the
    /// <c>gscode.game</c> setting. A workspace is one game, so a single current profile is correct;
    /// this is the one place the game is chosen, and every call site already reads it.
    ///
    /// Null-backed rather than initialised to <see cref="BlackOps3"/>, because that profile lives in
    /// another partial file and static field init order across files is unspecified — the fallback
    /// keeps <c>Active</c> valid before any field initialiser has run.
    /// </summary>
    public static GameProfile Active => s_active ?? BlackOps3;

    /// <summary>
    /// Selects the active profile by short name or id (see <see cref="ByName"/>). An unknown name
    /// falls back to BO3 rather than throwing, so a stray setting cannot break the server.
    /// </summary>
    public static void Select(string name)
    {
        s_active = ByName(name) ?? BlackOps3;
    }
}
