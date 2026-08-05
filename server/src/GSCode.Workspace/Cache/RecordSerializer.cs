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
    /// <summary>
    /// Serializes and gzip-compresses a record, writing JSON STRAIGHT into the compressor.
    ///
    /// It used to materialise the whole uncompressed document first
    /// (<c>SerializeToUtf8Bytes</c>), then compress that, then copy the result out — three full
    /// buffers per record. The middle one is the largest by far, and a record carrying a whole
    /// file's references and diagnostics runs to hundreds of kilobytes, so it landed on the
    /// large-object heap. That heap is not compacted by default, so every record written left a
    /// hole; on BO1 the fragmented figure after an index is several times the live one.
    ///
    /// Streaming removes that buffer entirely. The output stream still grows by doubling and
    /// <c>ToArray</c> still copies, but both are of the COMPRESSED size, which is roughly an order
    /// of magnitude smaller and usually below the large-object threshold.
    /// </summary>
    public static byte[] Serialize(ScriptRecord record)
    {
        using MemoryStream output = new();

        using ( GZipStream gzip = new(output, CompressionLevel.Fastest, leaveOpen: true) )
        using ( Utf8JsonWriter writer = new(gzip) )
        {
            JsonSerializer.Serialize(writer, record, CacheJsonContext.Default.ScriptRecord);
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
