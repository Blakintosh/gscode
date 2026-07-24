using GSCode.Core;
using GSCode.Core.Symbols;

namespace GSCode.Workspace.Api;

/// <summary>Both languages' builtin libraries, selected by the asking file's language.</summary>
public sealed class BuiltinApiSet
{
    public BuiltinApi Gsc { get; }
    public BuiltinApi Csc { get; }

    public BuiltinApiSet(BuiltinApi gsc, BuiltinApi csc)
    {
        Gsc = gsc;
        Csc = csc;
    }

    /// <summary>Loads both libraries from the given Api directory, named by the profile.</summary>
    public static BuiltinApiSet Load(string apiDirectory, GameProfile? profile = null)
    {
        GameProfile game = profile ?? GameProfile.Active;
        return new BuiltinApiSet(
            ApiLoader.Load(apiDirectory, ScriptLanguage.Gsc, game),
            ApiLoader.Load(apiDirectory, ScriptLanguage.Csc, game));
    }

    /// <summary>The library for a language (GSH consults the GSC library, its usual host).</summary>
    public BuiltinApi For(ScriptLanguage language)
    {
        return language == ScriptLanguage.Csc ? Csc : Gsc;
    }
}
