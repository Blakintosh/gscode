using System.Collections.Frozen;
using GSCode.Core.Symbols;

namespace GSCode.Parser.Syntax;

/// <summary>Which script world an asset type belongs to.</summary>
public enum PrecacheSide
{
    /// <summary>Usable from either world.</summary>
    Both,

    /// <summary>
    /// Client-side only: the <c>client_*</c> family, which precaches assets the client owns.
    /// Writing one in a <c>.gsc</c> is a mistake the engine will not honour.
    /// </summary>
    Client,
}

/// <summary>Argument rules for one #precache asset type (count includes the values after the type).</summary>
/// <param name="Name">The asset type string as written in scripts.</param>
/// <param name="MinValues">Minimum values after the type (usually 1: the asset name).</param>
/// <param name="MaxValues">Maximum values after the type — string-family types accept extras.</param>
/// <param name="Side">Which world may use it; <see cref="PrecacheSide.Both"/> unless stated.</param>
public sealed record PrecacheAssetType(
    string Name, int MinValues, int MaxValues, PrecacheSide Side = PrecacheSide.Both);

/// <summary>
/// The declarative #precache asset-type table from the language reference. Extraction
/// (P4) validates directives against it: unknown type or wrong value count → diagnostic.
/// </summary>
public static class PrecacheAssetTypes
{
    private static readonly FrozenDictionary<string, PrecacheAssetType> s_types = BuildTable();

    private static FrozenDictionary<string, PrecacheAssetType> BuildTable()
    {
        // The string-family types accept additional arguments beyond the asset name;
        // everything else takes exactly one value.
        //
        // Every type the language PDF documents is here. "xmodel" is the one addition beyond it:
        // undocumented, but used 38 times across the shipped scripts, so rejecting it would flag
        // stock code. Extend the same way — from real usage, not guesswork.
        string[] singleValueTypes =
        [
            "vehicle", "model", "playercharacter", "aitype", "character", "xmodelalias",
            "weapon", "zbarrier", "rumble", "shellshock", "xcam", "destructible",
            "streamerhint", "headicon", "statusicon", "locationselector", "menu",
            "material", "objective", "fx", "lui_menu", "lui_menu_data",
            "xmodel",
        ];

        string[] stringFamilyTypes = ["string", "debugstring", "eventstring", "triggerstring"];

        // The client_* family precaches assets the client owns, and appears only in .csc. Offering
        // or accepting one in a .gsc is wrong in both directions: it pads the completion list with
        // types that cannot work there, and it lets a real mistake through silently.
        string[] clientSingleValueTypes = ["client_fx", "client_tagfxset", "client_model"];
        string[] clientStringFamilyTypes = ["client_string"];

        Dictionary<string, PrecacheAssetType> table = new(StringComparer.OrdinalIgnoreCase);

        foreach ( string typeName in singleValueTypes )
        {
            table[typeName] = new PrecacheAssetType(typeName, MinValues: 1, MaxValues: 1);
        }

        foreach ( string typeName in stringFamilyTypes )
        {
            table[typeName] = new PrecacheAssetType(typeName, MinValues: 1, MaxValues: 3);
        }

        foreach ( string typeName in clientSingleValueTypes )
        {
            table[typeName] = new PrecacheAssetType(typeName, 1, 1, PrecacheSide.Client);
        }

        foreach ( string typeName in clientStringFamilyTypes )
        {
            table[typeName] = new PrecacheAssetType(typeName, 1, 3, PrecacheSide.Client);
        }

        return table.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Looks up an asset type by its (unquoted) name, whatever world it belongs to.</summary>
    public static bool TryGet(string typeName, out PrecacheAssetType assetType)
    {
        return s_types.TryGetValue(typeName, out assetType!);
    }

    /// <summary>
    /// Whether a type may be used from a file of this language.
    ///
    /// A header is allowed everything: a .gsh is inserted into whichever world includes it, so the
    /// language it will end up in is not knowable from the header itself, and reporting there would
    /// blame a file that is correct for the file that inserts it.
    /// </summary>
    public static bool IsAvailableIn(PrecacheAssetType assetType, ScriptLanguage language)
    {
        return assetType.Side == PrecacheSide.Both
            || language == ScriptLanguage.Csc
            || language == ScriptLanguage.Gsh;
    }

    /// <summary>All known type names, whatever world they belong to.</summary>
    public static IEnumerable<string> AllNames
    {
        get { return s_types.Keys; }
    }

    /// <summary>The type names a file of this language may actually use (completion source).</summary>
    public static IEnumerable<string> NamesFor(ScriptLanguage language)
    {
        foreach ( PrecacheAssetType assetType in s_types.Values )
        {
            if ( IsAvailableIn(assetType, language) )
            {
                yield return assetType.Name;
            }
        }
    }
}
