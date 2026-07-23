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

    // --- Capabilities: which language features and worlds exist in this dialect. ---

    /// <summary>Whether the game has client-side scripts (<c>.csc</c>). CSC is a Treyarch feature.</summary>
    public required bool HasClientScripts { get; init; }

    /// <summary>Whether the game has preprocessor headers (<c>.gsh</c> / <c>#insert</c>). BO3 onward.</summary>
    public required bool HasHeaders { get; init; }

    /// <summary>Whether the language has classes (<c>class</c>, <c>new</c>, <c>-&gt;</c>). T7 only.</summary>
    public required bool HasClasses { get; init; }

    /// <summary>Whether a function declaration begins with the <c>function</c> keyword. IW omits it.</summary>
    public required bool HasFunctionKeyword { get; init; }

    /// <summary>Whether a file declares its namespace with <c>#namespace</c>. IW keys off the path.</summary>
    public required bool HasNamespaceDirective { get; init; }

    /// <summary>How imports work — see <see cref="Core.ImportStyle"/>.</summary>
    public required ImportStyle ImportStyle { get; init; }

    // --- Root discovery: where the game's scripts live. ---

    /// <summary>The environment variable naming the game's tools install, or null if it has none.</summary>
    public required string? RootEnvironmentVariable { get; init; }

    /// <summary>The raw-scripts folder relative to the tools install (e.g. <c>share\raw</c>), or null.</summary>
    public required string? RawSubfolder { get; init; }

    /// <summary>The mods folder relative to the tools install (e.g. <c>mods</c>), or null.</summary>
    public required string? ModsSubfolder { get; init; }

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
        HasClientScripts = true,
        HasHeaders = true,
        HasClasses = true,
        HasFunctionKeyword = true,
        HasNamespaceDirective = true,
        ImportStyle = ImportStyle.Namespace,
        RootEnvironmentVariable = "TA_TOOLS_PATH",
        RawSubfolder = @"share\raw",
        ModsSubfolder = "mods",
    };

    /// <summary>
    /// The profile in force. A single fixed profile today — BO3 — and the seam a dialect port turns
    /// into a per-workspace choice (roadmap D1). Kept as one accessor so that change is one edit,
    /// not a sweep of every call site.
    /// </summary>
    public static GameProfile Active => BlackOps3;
}
