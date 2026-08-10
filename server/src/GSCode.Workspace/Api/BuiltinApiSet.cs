using System.Collections.Frozen;
using GSCode.Core;
using GSCode.Core.Symbols;

namespace GSCode.Workspace.Api;

/// <summary>Both languages' builtin libraries, selected by the asking file's language.</summary>
public sealed class BuiltinApiSet
{
    private readonly FrozenSet<string> _engineNamesGsc;
    private readonly FrozenSet<string> _engineNamesCsc;

    public BuiltinApi Gsc { get; }
    public BuiltinApi Csc { get; }

    public BuiltinApiSet(BuiltinApi gsc, BuiltinApi csc, FrozenSet<string>? engineNamesGsc = null, FrozenSet<string>? engineNamesCsc = null)
    {
        Gsc = gsc;
        Csc = csc;
        _engineNamesGsc = engineNamesGsc ?? NamesOf(gsc);
        _engineNamesCsc = engineNamesCsc ?? NamesOf(csc);
    }

    /// <summary>Loads both libraries from the given Api directory, named by the profile.</summary>
    public static BuiltinApiSet Load(string apiDirectory, GameProfile? profile = null)
    {
        GameProfile game = profile ?? GameProfile.Active;
        BuiltinApi gsc = ApiLoader.Load(apiDirectory, ScriptLanguage.Gsc, game);
        BuiltinApi csc = ApiLoader.Load(apiDirectory, ScriptLanguage.Csc, game);

        return new BuiltinApiSet(
            gsc,
            csc,
            EngineNames(apiDirectory, game, ScriptLanguage.Gsc, gsc),
            EngineNames(apiDirectory, game, ScriptLanguage.Csc, csc));
    }

    /// <summary>The library for a language (GSH consults the GSC library, its usual host).</summary>
    public BuiltinApi For(ScriptLanguage language)
    {
        return language == ScriptLanguage.Csc ? Csc : Gsc;
    }

    /// <summary>
    /// The names a rule may consult to ask ONLY "could this be an engine function?" — this game's
    /// own, or a close sibling's when it ships no library (see
    /// <see cref="GameProfile.EngineNameFallbackPrefix"/>).
    ///
    /// A SET rather than a <see cref="BuiltinApi"/>, so the restriction is in the type. Everything
    /// that renders a signature, a description or an argument count must keep reading the game's own
    /// library through <see cref="For"/> and show nothing when it is empty; presenting a sibling's
    /// parameter list as this game's would be a confident lie. Membership is the one question a
    /// neighbouring engine can answer for another, and handing back a full library would leave that
    /// rule resting on a comment nobody has to read.
    /// </summary>
    public FrozenSet<string> EngineNamesFor(ScriptLanguage language)
    {
        return language == ScriptLanguage.Csc ? _engineNamesCsc : _engineNamesGsc;
    }

    private static FrozenSet<string> EngineNames(
        string apiDirectory, GameProfile game, ScriptLanguage language, BuiltinApi own)
    {
        if ( own.Count > 0 || game.EngineNameFallbackFileName(language) is not string fileName )
        {
            return NamesOf(own);
        }

        return NamesOf(ApiLoader.LoadFile(Path.Combine(apiDirectory, fileName)));
    }

    private static FrozenSet<string> NamesOf(BuiltinApi api)
    {
        return api.All.Select(static function => function.Name).ToFrozenSet(StringComparer.OrdinalIgnoreCase);
    }
}
