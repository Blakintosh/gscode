using System.Collections.Frozen;
using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using GSCode.Core.Symbols;

namespace GSCode.Workspace.Api;

// DTOs mirroring the t7_api_*.json shape, deserialized via source generation.
internal sealed record ApiFile(List<ApiEntry>? Api);
internal sealed record ApiEntry(string? Name, string? Description, List<ApiOverload>? Overloads, string? Example, bool? DevOnly);
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
    /// <summary>Loads the library for a language from the given Api directory (empty when the file is absent).</summary>
    public static BuiltinApi Load(string apiDirectory, ScriptLanguage language)
    {
        string fileName = language == ScriptLanguage.Csc ? "t7_api_csc.json" : "t7_api_gsc.json";
        string path = Path.Combine(apiDirectory, fileName);
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
                    FormatType(parameter.Type)));
            }

            overloads.Add(new BuiltinOverload(
                overload.CalledOn?.Name,
                parameters.ToImmutable(),
                FormatType(overload.Returns?.Type),
                overload.Returns?.Void ?? false));
        }

        string name = entry.Name ?? "";

        return new BuiltinFunction(
            name,
            entry.Description ?? "",
            overloads.ToImmutable(),
            entry.Example ?? "")
        {
            // The data wins when it says anything, so a future `devOnly` field needs no code
            // change here; until then the curated list is the only source that is accurate.
            IsDevOnly = entry.DevOnly ?? DevOnlyBuiltins.Contains(name),
        };
    }

    private static string FormatType(ApiType? type)
    {
        if ( type?.DataType is null )
        {
            return "";
        }

        return type.IsArray ? type.DataType + "[]" : type.DataType;
    }
}
