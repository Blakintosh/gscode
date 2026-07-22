using System.Text.Json;
using System.Text.Json.Serialization;

// Generates the runtime engine-field artifacts (t7_object_fields.json, t7_radiant_keys.json)
// in GSCode.Workspace/Api from the curated sources. Run: dotnet run --project this-folder.
// The `import` (xlsx -> curated) step is manual for now; the curated JSON is the source of truth.

string toolDir = AppContext.BaseDirectory;
// Walk up from bin/Debug/net10.0 to the tool folder.
string root = FindToolRoot(toolDir);
string curatedDir = Path.Combine(root, "sources", "curated");
string originalsDir = Path.Combine(root, "sources", "originals");
string apiDir = Path.Combine(root, "..", "..", "src", "GSCode.Workspace", "Api");
apiDir = Path.GetFullPath(apiDir);

Console.WriteLine($"Curated:   {curatedDir}");
Console.WriteLine($"Originals: {originalsDir}");
Console.WriteLine($"Output:    {apiDir}");

GenerateObjectFields(curatedDir, Path.Combine(apiDir, "t7_object_fields.json"));
GenerateRadiantKeys(Path.Combine(originalsDir, "keys.txt"), Path.Combine(apiDir, "t7_radiant_keys.json"));

Console.WriteLine("Done.");
return 0;

static string FindToolRoot(string start)
{
    DirectoryInfo? dir = new(start);
    while ( dir is not null )
    {
        if ( Directory.Exists(Path.Combine(dir.FullName, "sources", "curated")) )
        {
            return dir.FullName;
        }

        dir = dir.Parent;
    }

    throw new DirectoryNotFoundException("Could not locate the field-data tool root (sources/curated).");
}

// Merges every curated *_fields.json into one { entityKind: [fields] } document, sorted.
static void GenerateObjectFields(string curatedDir, string outputPath)
{
    JsonSerializerOptions readOptions = new() { PropertyNameCaseInsensitive = true, ReadCommentHandling = JsonCommentHandling.Skip };
    SortedDictionary<string, List<FieldEntry>> byKind = new(StringComparer.Ordinal);

    foreach ( string file in Directory.EnumerateFiles(curatedDir, "*.json").OrderBy(static f => f, StringComparer.Ordinal) )
    {
        string kind = EntityKindFromFileName(Path.GetFileNameWithoutExtension(file));

        // Curated files may carry a leading // comment line; ReadCommentHandling.Skip handles it.
        string json = File.ReadAllText(file);
        List<FieldEntry>? entries = JsonSerializer.Deserialize<List<FieldEntry>>(json, readOptions);
        if ( entries is null )
        {
            continue;
        }

        List<FieldEntry> sorted = [.. entries.OrderBy(static e => e.Name, StringComparer.Ordinal)];
        byKind[kind] = sorted;
        Console.WriteLine($"  {kind}: {sorted.Count} fields");
    }

    WriteJson(outputPath, byKind);
}

// Parses radiant/keys.txt: optional 'client' prefix, <type> <field>, optional // comment.
static void GenerateRadiantKeys(string keysPath, string outputPath)
{
    if ( !File.Exists(keysPath) )
    {
        Console.WriteLine($"  keys.txt not found at {keysPath}; writing an empty radiant-keys file.");
        WriteJson(outputPath, new List<RadiantKey>());
        return;
    }

    List<RadiantKey> keys = [];
    int corrected = 0;

    foreach ( string rawLine in File.ReadAllLines(keysPath) )
    {
        string line = rawLine.Trim();
        if ( line.Length == 0 || line.StartsWith("//", StringComparison.Ordinal) )
        {
            continue;
        }

        string comment = "";
        int commentIndex = line.IndexOf("//", StringComparison.Ordinal);
        if ( commentIndex >= 0 )
        {
            comment = line[(commentIndex + 2)..].Trim();
            line = line[..commentIndex].Trim();
        }

        string[] parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if ( parts.Length < 2 )
        {
            continue;
        }

        string side = "both";
        int cursor = 0;
        if ( string.Equals(parts[0], "client", StringComparison.OrdinalIgnoreCase) )
        {
            side = "client";
            cursor = 1;
        }

        if ( parts.Length < cursor + 2 )
        {
            continue;
        }

        string name = parts[cursor + 1].ToLowerInvariant();
        string correctedSide = CorrectSide(name, side);
        if ( !string.Equals(correctedSide, side, StringComparison.Ordinal) )
        {
            corrected++;
        }

        keys.Add(new RadiantKey(name, parts[cursor].ToLowerInvariant(), correctedSide, comment));
    }

    List<RadiantKey> sorted = [.. keys.OrderBy(static k => k.Name, StringComparer.Ordinal)];
    Console.WriteLine($"  radiant keys: {sorted.Count} ({corrected} side corrections applied)");
    WriteJson(outputPath, sorted);
}

// keys.txt mismarks a few keys as client-only that are in fact readable from both sides.
// The file is committed verbatim as upstream provenance, so it cannot be edited; shipping our
// own generated artifacts is precisely what lets us correct mistakes like this. Fixing it here
// rather than at runtime means every consumer (hover, completion, future type seeds) is simply
// right without special-casing. Add a line per key as more are confirmed.
static string CorrectSide(string name, string parsedSide)
{
    // classname is read by GSC just as much as CSC (e.g. spawner and trigger classification).
    if ( string.Equals(name, "classname", StringComparison.OrdinalIgnoreCase) )
    {
        return "both";
    }

    return parsedSide;
}

static string EntityKindFromFileName(string fileName)
{
    string name = fileName;
    if ( name.EndsWith("_simple", StringComparison.Ordinal) )
    {
        name = name[..^"_simple".Length];
    }

    if ( name.EndsWith("_fields", StringComparison.Ordinal) )
    {
        name = name[..^"_fields".Length];
    }

    if ( name == "entity_generic" )
    {
        name = "entity";
    }

    return name;
}

static void WriteJson<T>(string path, T value)
{
    string json = JsonSerializer.Serialize(value, new JsonSerializerOptions
    {
        WriteIndented = true,
        // Indentation defaults to the platform newline, which on Windows makes every regeneration
        // rewrite the whole artifact as CRLF against an LF repo. Pinned so the output is identical
        // wherever it is generated.
        NewLine = "\n",
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault,
    });
    File.WriteAllText(path, json + "\n");
}

internal sealed record FieldEntry(string Name, string Type, bool ReadOnly = false);
internal sealed record RadiantKey(string Name, string Type, string Side, string Comment);
