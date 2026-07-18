using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Serialization;
using GSCode.Workspace.Database;

namespace GSCode.Workspace.Cache;

/// <summary>Source-generated JSON context for the cache blob (no runtime reflection).</summary>
[JsonSourceGenerationOptions(GenerationMode = JsonSourceGenerationMode.Default)]
[JsonSerializable(typeof(ScriptRecord))]
internal sealed partial class CacheJsonContext : JsonSerializerContext;

/// <summary>Serializes a ScriptRecord to a gzipped JSON blob and back.</summary>
public static class RecordSerializer
{
    /// <summary>Serializes and gzip-compresses a record.</summary>
    public static byte[] Serialize(ScriptRecord record)
    {
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(record, CacheJsonContext.Default.ScriptRecord);

        using MemoryStream output = new();
        using ( GZipStream gzip = new(output, CompressionLevel.Fastest, leaveOpen: true) )
        {
            gzip.Write(json, 0, json.Length);
        }

        return output.ToArray();
    }

    /// <summary>Decompresses and deserializes a record, or null when the blob is unreadable.</summary>
    public static ScriptRecord? Deserialize(byte[] blob)
    {
        try
        {
            using MemoryStream input = new(blob);
            using GZipStream gzip = new(input, CompressionMode.Decompress);
            using MemoryStream json = new();
            gzip.CopyTo(json);
            json.Position = 0;

            return JsonSerializer.Deserialize(json, CacheJsonContext.Default.ScriptRecord);
        }
        catch ( InvalidDataException )
        {
            return null;
        }
        catch ( JsonException )
        {
            return null;
        }
    }
}
