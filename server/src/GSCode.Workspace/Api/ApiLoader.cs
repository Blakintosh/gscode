using System.Collections.Frozen;
using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using GSCode.Core;
using GSCode.Core.Symbols;

namespace GSCode.Workspace.Api;

// DTOs mirroring the api_*.json shape, deserialized via source generation.
internal sealed record ApiFile(List<ApiEntry>? Api);
internal sealed record ApiEntry(
    string? Name, string? Description, List<ApiOverload>? Overloads, string? Example, bool? DevOnly, string? Confidence);
internal sealed record ApiOverload(ApiCalledOn? CalledOn, List<ApiParameter>? Parameters, ApiReturn? Returns);
internal sealed record ApiCalledOn(string? Name);
internal sealed record ApiParameter(string? Name, string? Description, bool Mandatory, ApiType? Type);
internal sealed record ApiReturn(ApiType? Type, bool Void);
internal sealed record ApiType(string? DataType, bool IsArray);

[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(ApiFile))]
internal sealed partial class ApiJsonContext : JsonSerializerContext;

/// <summary>Loads the bundled builtin API JSON into a <see cref="BuiltinApi"/>.</summary>
public static class ApiLoader
{
    /// <summary>
    /// Loads the library for a language from the given Api directory, using the profile's data-file
    /// naming. Empty when the profile ships no data (non-BO3 today) or the file is absent.
    /// </summary>
    public static BuiltinApi Load(string apiDirectory, ScriptLanguage language, GameProfile? profile = null)
    {
        string? fileName = (profile ?? GameProfile.Active).ApiFileName(language);
        if ( fileName is null )
        {
            return BuiltinApi.Empty;
        }

        return LoadFile(Path.Combine(apiDirectory, fileName));
    }

    /// <summary>
    /// Loads one API file by full path. Split out of <see cref="Load"/> so a caller that has already
    /// decided WHICH file it wants — the engine-name fallback, which reads a sibling game's — is not
    /// forced to go back through profile-based naming to ask for it.
    /// </summary>
    public static BuiltinApi LoadFile(string path)
    {
        if ( !File.Exists(path) )
        {
            return BuiltinApi.Empty;
        }

        ApiFile? file;
        try
        {
            using FileStream stream = File.OpenRead(path);
            file = JsonSerializer.Deserialize(stream, ApiJsonContext.Default.ApiFile);
        }
        catch ( JsonException )
        {
            return BuiltinApi.Empty;
        }

        if ( file?.Api is null )
        {
            return BuiltinApi.Empty;
        }

        Dictionary<string, BuiltinFunction> functions = new(StringComparer.OrdinalIgnoreCase);
        foreach ( ApiEntry entry in file.Api )
        {
            if ( entry.Name is null )
            {
                continue;
            }

            functions[entry.Name] = Convert(entry);
        }

        return new BuiltinApi(functions.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase));
    }

    private static BuiltinFunction Convert(ApiEntry entry)
    {
        ImmutableArray<BuiltinOverload>.Builder overloads = ImmutableArray.CreateBuilder<BuiltinOverload>();

        foreach ( ApiOverload overload in entry.Overloads ?? [] )
        {
            ImmutableArray<BuiltinParameter>.Builder parameters = ImmutableArray.CreateBuilder<BuiltinParameter>();
            foreach ( ApiParameter parameter in overload.Parameters ?? [] )
            {
                parameters.Add(new BuiltinParameter(
                    parameter.Name ?? "",
                    parameter.Description ?? "",
                    parameter.Mandatory,
                    FormatType(parameter.Type),
                    ParseType(parameter.Type?.DataType, parameter.Type?.IsArray ?? false),
                    IsVararg(parameter.Type)));
            }

            overloads.Add(new BuiltinOverload(
                overload.CalledOn?.Name,
                parameters.ToImmutable(),
                FormatType(overload.Returns?.Type),
                overload.Returns?.Void ?? false,
                ParseType(overload.Returns?.Type?.DataType, overload.Returns?.Type?.IsArray ?? false),
                ScrTypeSet.None));
        }

        string name = entry.Name ?? "";

        return new BuiltinFunction(
            name,
            entry.Description ?? "",
            overloads.ToImmutable(),
            entry.Example ?? "")
        {
            // The data wins when it says anything, and that ordering is what makes this per GAME:
            // the curated list is Black Ops 3's, so a game whose own library states the answer
            // overrides it and only a silent one falls back. CoD4's four affected names carry
            // `"devOnly": false` for exactly that reason — see DevOnlyBuiltins.
            IsDevOnly = entry.DevOnly ?? DevOnlyBuiltins.Contains(name),
            Confidence = ParseConfidence(entry.Confidence),
        };
    }

    private static BuiltinConfidence ParseConfidence(string? confidence)
    {
        switch ( confidence?.ToLowerInvariant() )
        {
            case "high": return BuiltinConfidence.High;
            case "medium": return BuiltinConfidence.Medium;
            case "low": return BuiltinConfidence.Low;
            default: return BuiltinConfidence.Unstated;
        }
    }

    private static string FormatType(ApiType? type)
    {
        if ( type?.DataType is null )
        {
            return "";
        }

        return type.IsArray ? type.DataType + "[]" : type.DataType;
    }

    /// <summary>True when the declared type is the parameter pack.</summary>
    private static bool IsVararg(ApiType? type)
    {
        return string.Equals(type?.DataType, "vararg", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Parses a declared type onto the lattice, ONCE at load rather than re-switching on display
    /// text at every call.
    ///
    /// The data is richer than the old text switch could see, and all of this was being dropped:
    ///
    /// - <c>isArray</c>. 114 of BO3's GSC declarations set it, and an array return produced nothing
    ///   at all — so <see cref="ScrTypeSet.Array"/> was never once produced by a builtin call. Given
    ///   that arrays are the only kind whose pass semantics differ between dialects, that was the
    ///   single most costly omission here.
    /// - Unions, spelled pipe-separated inside <c>dataType</c>: <c>"int | string"</c>,
    ///   <c>"bool | int"</c>, <c>"number | vector"</c>. The flat lattice had no way to hold one, so
    ///   they were dropped; this one splits them.
    /// - <c>number</c>, which is 349 declarations in BO3's GSC library alone and is exactly
    ///   <c>int|float</c> — expressible now, and previously discarded as vague.
    /// - <c>vararg</c>, the parameter pack, which is an array.
    ///
    /// Returns <see cref="ScrTypeSet.None"/> for a spelling the lattice genuinely cannot express —
    /// <c>any</c>, <c>enum</c>, <c>anim</c> — so the caller can report WHY rather than treating it
    /// as an ordinary unknown.
    /// </summary>
    public static ScrTypeSet ParseType(string? dataType, bool isArray)
    {
        if ( dataType is null )
        {
            return ScrTypeSet.None;
        }

        // An array of anything is an array; the element type is not modelled either way.
        if ( isArray )
        {
            return ScrTypeSet.Array;
        }

        ScrTypeSet parsed = ScrTypeSet.None;
        foreach ( string part in dataType.Split('|') )
        {
            ScrTypeSet member = ParseTypeName(part.Trim());
            if ( member == ScrTypeSet.None )
            {
                // One unmappable member makes the whole union unknowable, since the value could be
                // that member.
                return ScrTypeSet.None;
            }

            parsed |= member;
        }

        return parsed;
    }

    private static ScrTypeSet ParseTypeName(string name)
    {
        switch ( name.ToLowerInvariant() )
        {
            case "int": return ScrTypeSet.Int;
            case "float": return ScrTypeSet.Float;
            case "number": return ScrTypeSet.Number;
            case "bool": return ScrTypeSet.Bool;
            case "string": return ScrTypeSet.String;
            case "istring": return ScrTypeSet.IString;
            case "hash": return ScrTypeSet.HashString;
            case "vector": return ScrTypeSet.Vector;
            case "struct": return ScrTypeSet.Struct;
            case "array": return ScrTypeSet.Array;
            case "entity": return ScrTypeSet.Entity;
            case "function": return ScrTypeSet.Function;
            case "undefined": return ScrTypeSet.Undefined;

            // The pack is bound as an array on the one dialect that has it.
            case "vararg": return ScrTypeSet.Array;

            // Engine handles the lattice does not separate — weapon, pathnode and the rest are
            // entity-shaped, and calling them Entity would claim more than the data supports.
            default: return ScrTypeSet.None;
        }
    }
}
