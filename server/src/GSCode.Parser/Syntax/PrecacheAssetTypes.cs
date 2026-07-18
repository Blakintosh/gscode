using System.Collections.Frozen;

namespace GSCode.Parser.Syntax;

/// <summary>Argument rules for one #precache asset type (count includes the values after the type).</summary>
/// <param name="Name">The asset type string as written in scripts.</param>
/// <param name="MinValues">Minimum values after the type (usually 1: the asset name).</param>
/// <param name="MaxValues">Maximum values after the type — string-family types accept extras.</param>
public sealed record PrecacheAssetType(string Name, int MinValues, int MaxValues);

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
        string[] singleValueTypes =
        [
            "vehicle", "model", "playercharacter", "aitype", "character", "xmodelalias",
            "weapon", "zbarrier", "rumble", "shellshock", "xcam", "destructible",
            "streamerhint", "headicon", "statusicon", "locationselector", "menu",
            "material", "objective", "fx", "lui_menu", "lui_menu_data", "client_fx",
            "client_tagfxset",
        ];

        string[] stringFamilyTypes = ["string", "debugstring", "eventstring", "triggerstring"];

        Dictionary<string, PrecacheAssetType> table = new(StringComparer.OrdinalIgnoreCase);

        foreach ( string typeName in singleValueTypes )
        {
            table[typeName] = new PrecacheAssetType(typeName, MinValues: 1, MaxValues: 1);
        }

        foreach ( string typeName in stringFamilyTypes )
        {
            table[typeName] = new PrecacheAssetType(typeName, MinValues: 1, MaxValues: 3);
        }

        return table.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Looks up an asset type by its (unquoted) name.</summary>
    public static bool TryGet(string typeName, out PrecacheAssetType assetType)
    {
        return s_types.TryGetValue(typeName, out assetType!);
    }

    /// <summary>All known type names (completion source).</summary>
    public static IEnumerable<string> AllNames
    {
        get { return s_types.Keys; }
    }
}
