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
GenerateCod4Data(Path.Combine(originalsDir, "cod4_wordfile.txt"), Path.Combine(originalsDir, "cod4_keys.txt"), apiDir);

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

// CoD4 has no bundled API/field data; its only complete list of engine functions, radiant keys and
// entity properties is the mod-tools syntax-highlighting wordfile. This reads the CODSCRIPT block's
// sections into the same runtime artifacts BO3 ships. Names only — the wordfile carries no types,
// signatures or docs; those are a later enrichment pass. BO1's wordfile has the same shape.
static void GenerateCod4Data(string wordfilePath, string keysPath, string apiDir)
{
    if ( !File.Exists(wordfilePath) )
    {
        Console.WriteLine($"  cod4 wordfile not found at {wordfilePath}; skipping cod4 data.");
        return;
    }

    string[] lines = File.ReadAllLines(wordfilePath);

    // /C7 "Script Commands" — the engine builtin functions. Case preserved (as BO3's api file does).
    List<string> functions = CleanNames(ParseWordfileSection(lines, 7), stripLeadingDot: false, lowercase: false);
    ApiFileOut api = new([.. functions.Select(static name => new ApiEntryOut(name, []))]);
    WriteJson(Path.Combine(apiDir, "cod4_api_gsc.json"), api, camelCase: true);
    Console.WriteLine($"  cod4 api functions: {functions.Count}");

    // Radiant keys come from the game's own keys.txt (the same file Radiant loads), not the
    // wordfile's bare-name /C6 — it carries types, client/both sides and comments for hover.
    Console.Write("  cod4");
    GenerateRadiantKeys(keysPath, Path.Combine(apiDir, "cod4_radiant_keys.json"));

    // /C2 "Common Entity Properties" — script-accessible .fields (the wordfile writes the leading
    // dot); untyped, under one generic kind.
    List<FieldEntry> fieldEntries = [.. CleanNames(ParseWordfileSection(lines, 2), stripLeadingDot: true, lowercase: true)
        .Select(static name => new FieldEntry(name, ""))];
    SortedDictionary<string, List<FieldEntry>> fields = new(StringComparer.Ordinal) { ["entity"] = fieldEntries };
    WriteJson(Path.Combine(apiDir, "cod4_object_fields.json"), fields);
    Console.WriteLine($"  cod4 object fields: {fieldEntries.Count}");
}

// The entries of a /C&lt;index&gt; section within a wordfile's GSC language block. UltraEdit wordfiles
// list one word per line under a /C header; a section ends at the next /C or /L marker.
static List<string> ParseWordfileSection(string[] lines, int sectionIndex)
{
    int blockStart = Array.FindIndex(lines, static line =>
        line.StartsWith("/L", StringComparison.Ordinal) && MentionsGscExtension(line));
    if ( blockStart < 0 )
    {
        return [];
    }

    int blockEnd = lines.Length;
    for ( int i = blockStart + 1; i < lines.Length; i++ )
    {
        if ( lines[i].StartsWith("/L", StringComparison.Ordinal) )
        {
            blockEnd = i;
            break;
        }
    }

    string marker = "/C" + sectionIndex.ToString(System.Globalization.CultureInfo.InvariantCulture);
    int start = -1;
    for ( int i = blockStart + 1; i < blockEnd; i++ )
    {
        // Exact section: "/C7" must not also match "/C70".
        if ( lines[i].StartsWith(marker, StringComparison.Ordinal)
            && (lines[i].Length == marker.Length || !char.IsDigit(lines[i][marker.Length])) )
        {
            start = i + 1;
            break;
        }
    }

    if ( start < 0 )
    {
        return [];
    }

    List<string> entries = [];
    for ( int i = start; i < blockEnd; i++ )
    {
        if ( lines[i].StartsWith("/C", StringComparison.Ordinal) || lines[i].StartsWith("/L", StringComparison.Ordinal) )
        {
            break;
        }

        // One identifier per line; split defensively in case a line lists several.
        foreach ( string token in lines[i].Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries) )
        {
            entries.Add(token);
        }
    }

    return entries;
}

// A wordfile language header names its file extensions, e.g. "... File Extensions = GSC".
static bool MentionsGscExtension(string languageHeader)
{
    int index = languageHeader.IndexOf("File Extensions", StringComparison.OrdinalIgnoreCase);
    if ( index < 0 )
    {
        return false;
    }

    return languageHeader[index..]
        .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
        .Contains("GSC", StringComparer.OrdinalIgnoreCase);
}

// Normalizes and filters wordfile tokens to clean names: an optional leading dot removed (fields
// are written .field), everything that is not a valid GSC identifier dropped (the section can end
// in stray operators, quoted labels or tab runs), lowercased for keys/fields, then sorted-unique.
static List<string> CleanNames(IEnumerable<string> raw, bool stripLeadingDot, bool lowercase)
{
    IEnumerable<string> names = raw
        .Select(name => stripLeadingDot ? name.TrimStart('.') : name)
        .Where(IsValidIdentifier);

    if ( lowercase )
    {
        names = names.Select(static name => name.ToLowerInvariant());
    }

    return [.. names.Distinct(StringComparer.Ordinal).OrderBy(static n => n, StringComparer.Ordinal)];
}

static bool IsValidIdentifier(string name)
{
    if ( name.Length == 0 || !(char.IsLetter(name[0]) || name[0] == '_') )
    {
        return false;
    }

    foreach ( char character in name )
    {
        if ( !char.IsLetterOrDigit(character) && character != '_' )
        {
            return false;
        }
    }

    return true;
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

// camelCase matches BO3's api_gsc.json (the field data files stay PascalCase); the loaders read
// both case-insensitively, so this is only for a clean, consistent diff against the existing files.
static void WriteJson<T>(string path, T value, bool camelCase = false)
{
    string json = JsonSerializer.Serialize(value, new JsonSerializerOptions
    {
        WriteIndented = true,
        // Indentation defaults to the platform newline, which on Windows makes every regeneration
        // rewrite the whole artifact as CRLF against an LF repo. Pinned so the output is identical
        // wherever it is generated.
        NewLine = "\n",
        PropertyNamingPolicy = camelCase ? JsonNamingPolicy.CamelCase : null,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault,
    });
    File.WriteAllText(path, json + "\n");
}

internal sealed record FieldEntry(string Name, string Type, bool ReadOnly = false);
internal sealed record RadiantKey(string Name, string Type, string Side, string Comment);

// The builtin-API artifact shape (matching t7_api_gsc.json): a name plus its overloads. From the
// wordfile only names are known, so overloads is empty until the online-API enrichment pass.
internal sealed record ApiFileOut(List<ApiEntryOut> Api);
internal sealed record ApiEntryOut(string Name, IReadOnlyList<object> Overloads);
