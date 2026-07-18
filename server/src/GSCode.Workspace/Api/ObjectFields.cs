using System.Collections.Frozen;
using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace GSCode.Workspace.Api;

/// <summary>One engine object field: its type and whether it is read-only, on a given entity kind.</summary>
public sealed record ObjectField(string Name, string Type, bool ReadOnly, string EntityKind);

/// <summary>One radiant map-entity KVP key from keys.txt.</summary>
public sealed record RadiantKey(string Name, string Type, string Side, string Comment);

/// <summary>
/// The bundled engine object-field + radiant-key data, generated from the curated sources
/// by the field-data tool. Powers typed completion and field hover; a field name can exist
/// on several entity kinds (e.g. `origin` on many), so lookups return every match.
/// </summary>
public sealed class ObjectFields
{
    private readonly FrozenDictionary<string, ImmutableArray<ObjectField>> _fieldsByName;
    private readonly FrozenDictionary<string, RadiantKey> _radiantByName;

    /// <summary>Empty data (used when the artifacts are absent).</summary>
    public static ObjectFields Empty { get; } = new(
        FrozenDictionary<string, ImmutableArray<ObjectField>>.Empty,
        FrozenDictionary<string, RadiantKey>.Empty);

    private ObjectFields(
        FrozenDictionary<string, ImmutableArray<ObjectField>> fieldsByName,
        FrozenDictionary<string, RadiantKey> radiantByName)
    {
        _fieldsByName = fieldsByName;
        _radiantByName = radiantByName;
    }

    /// <summary>Every entity kind that declares a field with this (case-insensitive) name.</summary>
    public ImmutableArray<ObjectField> FindField(string name)
    {
        return _fieldsByName.TryGetValue(name, out ImmutableArray<ObjectField> matches) ? matches : [];
    }

    /// <summary>The radiant map key with this name, or null.</summary>
    public RadiantKey? FindRadiantKey(string name)
    {
        return _radiantByName.TryGetValue(name, out RadiantKey? key) ? key : null;
    }

    /// <summary>Loads the two artifacts from an Api directory (empty when absent/corrupt).</summary>
    public static ObjectFields Load(string apiDirectory)
    {
        Dictionary<string, List<ObjectField>> byName = new(StringComparer.OrdinalIgnoreCase);
        LoadObjectFields(Path.Combine(apiDirectory, "t7_object_fields.json"), byName);

        Dictionary<string, RadiantKey> radiant = new(StringComparer.OrdinalIgnoreCase);
        LoadRadiantKeys(Path.Combine(apiDirectory, "t7_radiant_keys.json"), radiant);

        FrozenDictionary<string, ImmutableArray<ObjectField>> fields = byName.ToFrozenDictionary(
            static pair => pair.Key,
            static pair => pair.Value.ToImmutableArray(),
            StringComparer.OrdinalIgnoreCase);

        return new ObjectFields(fields, radiant.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase));
    }

    private static void LoadObjectFields(string path, Dictionary<string, List<ObjectField>> byName)
    {
        if ( !File.Exists(path) )
        {
            return;
        }

        Dictionary<string, List<RawField>>? kinds;
        try
        {
            using FileStream stream = File.OpenRead(path);
            kinds = JsonSerializer.Deserialize(stream, ObjectFieldsJsonContext.Default.DictionaryStringListRawField);
        }
        catch ( JsonException )
        {
            return;
        }

        if ( kinds is null )
        {
            return;
        }

        foreach ( (string kind, List<RawField> entries) in kinds )
        {
            foreach ( RawField entry in entries )
            {
                if ( entry.Name is null || entry.Type is null )
                {
                    continue;
                }

                if ( !byName.TryGetValue(entry.Name, out List<ObjectField>? list) )
                {
                    list = [];
                    byName[entry.Name] = list;
                }

                list.Add(new ObjectField(entry.Name, entry.Type, entry.ReadOnly, kind));
            }
        }
    }

    private static void LoadRadiantKeys(string path, Dictionary<string, RadiantKey> radiant)
    {
        if ( !File.Exists(path) )
        {
            return;
        }

        List<RawRadiantKey>? keys;
        try
        {
            using FileStream stream = File.OpenRead(path);
            keys = JsonSerializer.Deserialize(stream, ObjectFieldsJsonContext.Default.ListRawRadiantKey);
        }
        catch ( JsonException )
        {
            return;
        }

        foreach ( RawRadiantKey key in keys ?? [] )
        {
            if ( key.Name is not null && key.Type is not null )
            {
                radiant[key.Name] = new RadiantKey(key.Name, key.Type, key.Side ?? "both", key.Comment ?? "");
            }
        }
    }
}

internal sealed record RawField(string? Name, string? Type, bool ReadOnly);
internal sealed record RawRadiantKey(string? Name, string? Type, string? Side, string? Comment);

[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(Dictionary<string, List<RawField>>))]
[JsonSerializable(typeof(List<RawRadiantKey>))]
internal sealed partial class ObjectFieldsJsonContext : JsonSerializerContext;
