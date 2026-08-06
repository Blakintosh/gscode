using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
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
string harvestDir = Path.Combine(root, "..", "..", "tests", "GSCode.Server.Tests", "harvest");

Console.WriteLine($"Curated:   {curatedDir}");
Console.WriteLine($"Originals: {originalsDir}");
Console.WriteLine($"Output:    {apiDir}");

GenerateObjectFields(curatedDir, Path.Combine(apiDir, "t7_object_fields.json"));
GenerateRadiantKeys(Path.Combine(originalsDir, "keys.txt"), Path.Combine(apiDir, "t7_radiant_keys.json"));
GenerateWordfileGameData(
    "cod4",
    Path.Combine(originalsDir, "cod4_wordfile.txt"),
    Path.Combine(originalsDir, "cod4_keys.txt"),
    // CoD4 has no client scripts and ships no clientkeys.txt, which is the cross-check that the
    // two-file split is a Treyarch convention rather than an engine-wide one.
    clientKeysPath: null,
    apiDir,
    Environment.GetEnvironmentVariable("GSCODE_COD4_DOCS"),
    curatedDir);

// BO1's wordfile has the identical CODSCRIPT layout, so the same reader produces its data. Its
// docs/script_docs tree ships with every page stripped, leaving only folder names, so there is no
// documentation source to enrich these names with.
GenerateWordfileGameData(
    "bo1",
    Path.Combine(originalsDir, "bo1_wordfile.txt"),
    Path.Combine(originalsDir, "bo1_keys.txt"),
    Path.Combine(originalsDir, "bo1_clientkeys.txt"),
    apiDir,
    docsRoot: null,
    curatedDir,
    enrichFrom: Path.Combine(apiDir, "cod4_api_gsc.json"),
    empiricalPath: Path.Combine(harvestDir, "bo1_missing_builtins.json"));

// WaW sits between the two in the same lineage and ships the same shaped wordfile and the same
// split radiant keys.
GenerateWordfileGameData(
    "waw",
    Path.Combine(originalsDir, "waw_wordfile.txt"),
    Path.Combine(originalsDir, "waw_keys.txt"),
    Path.Combine(originalsDir, "waw_clientkeys.txt"),
    apiDir,
    docsRoot: null,
    curatedDir,
    enrichFrom: Path.Combine(apiDir, "cod4_api_gsc.json"),
    empiricalPath: Path.Combine(harvestDir, "waw_missing_builtins.json"));

// The client libraries, for the games that have client scripts but no documentation describing them.
// Derived from the GSC artifact just written rather than from the wordfile, because the wordfile has
// no client/server split at all — its /C7 section is one list of script commands. BO3 is absent by
// design: t7_api_csc.json is hand-documented and real, and is what the derivation was checked against.
GenerateClientApi("waw", apiDir, curatedDir, Path.Combine(harvestDir, "waw_missing_builtins.json"));
GenerateClientApi("bo1", apiDir, curatedDir, Path.Combine(harvestDir, "bo1_missing_builtins.json"));

// Keep the empirical answer independent of the next harvest. Once these entries are in the API,
// rerunning BuiltinHarvestTests quite correctly reports zero misses; without a durable curated copy,
// the next field-data regeneration would then forget what the previous harvest proved.
EnsureEmpiricalSources("waw", harvestDir, apiDir, curatedDir);
EnsureEmpiricalSources("bo1", harvestDir, apiDir, curatedDir);

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

    // Only the *_fields*.json files (weapon_fields_simple.json included): curated/ also holds
    // other curated data, such as reconstructed builtins, and sweeping every .json here would turn
    // each of those into a bogus entity kind.
    foreach ( string file in Directory.EnumerateFiles(curatedDir, "*_fields*.json").OrderBy(static f => f, StringComparer.Ordinal) )
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
//
// The games express "this key is client-side" two different ways. BO3 keeps ONE keys.txt and marks
// such lines with a leading 'client'. The pre-BO3 Treyarch games instead SPLIT the data across
// keys.txt and clientkeys.txt, and their keys.txt carries no prefix at all — so on those games the
// prefix rule is inert and every key would otherwise be recorded as visible to both sides. Reading
// the second file supplies the distinction the prefix cannot, and it is where the client-only keys
// live at all: 126 of BO1's 369 client keys appear nowhere in its keys.txt.
static void GenerateRadiantKeys(string keysPath, string outputPath, string? clientKeysPath = null)
{
    if ( !File.Exists(keysPath) )
    {
        Console.WriteLine($"  keys.txt not found at {keysPath}; writing an empty radiant-keys file.");
        WriteJson(outputPath, new List<RadiantKey>());
        return;
    }

    List<RadiantKey> keys = ParseKeysFile(keysPath, out int corrected);

    // Keys that exist only in the client file are client-side; one present in both is visible to
    // both, which is what keys.txt already recorded it as.
    int clientOnly = 0;
    if ( clientKeysPath is not null && File.Exists(clientKeysPath) )
    {
        HashSet<string> known = new(keys.Select(static k => k.Name), StringComparer.OrdinalIgnoreCase);
        foreach ( RadiantKey key in ParseKeysFile(clientKeysPath, out _) )
        {
            if ( known.Add(key.Name) )
            {
                keys.Add(key with { Side = "client" });
                clientOnly++;
            }
        }
    }

    List<RadiantKey> sorted = [.. keys.OrderBy(static k => k.Name, StringComparer.Ordinal)];
    Console.WriteLine($"  radiant keys: {sorted.Count} ({clientOnly} client-only, {corrected} side corrections applied)");
    WriteJson(outputPath, sorted);
}

// One keys file into records. Shared by keys.txt and clientkeys.txt, which have the same shape.
static List<RadiantKey> ParseKeysFile(string keysPath, out int corrected)
{
    List<RadiantKey> keys = [];
    corrected = 0;

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

    return keys;
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
static void GenerateWordfileGameData(string prefix, string wordfilePath, string keysPath, string? clientKeysPath, string apiDir, string? docsRoot, string curatedDir, string? enrichFrom = null, string? empiricalPath = null)
{
    if ( !File.Exists(wordfilePath) )
    {
        Console.WriteLine($"  {prefix} wordfile not found at {wordfilePath}; skipping {prefix} data.");
        return;
    }

    string[] lines = File.ReadAllLines(wordfilePath);

    // /C7 "Script Commands" — the engine builtin functions. Case preserved (as BO3's api file does).
    // Names ONLY: the wordfile is an editor syntax file and carries no signatures. They are merged
    // with the documented pages, which do, so the library keeps full coverage and gains detail
    // wherever a page exists.
    // The documentation pages are NOT vendored — they are a third party's — so the generator reads
    // them from wherever they live via GSCODE_COD4_DOCS, the same way the corpus tests locate game
    // scripts. Unset means the wordfile names alone, so the run is guarded below: the artifact in the
    // repo HOLDS documented detail, and a regeneration on a machine without the docs would replace
    // every signature with a bare name and then hand the wreckage to WaW and BO1 through enrichFrom.
    List<string> functions = CleanNames(ParseWordfileSection(lines, 7), stripLeadingDot: false, lowercase: false);

    GenerateWordfileApi(
        prefix,
        docsRoot,
        functions,
        Path.Combine(curatedDir, $"{prefix}_ai_builtins.json"),
        Path.Combine(curatedDir, $"{prefix}_api_overrides.json"),
        enrichFrom,
        empiricalPath,
        Path.Combine(apiDir, $"{prefix}_api_gsc.json"));

    // Radiant keys come from the game's own keys.txt (the same file Radiant loads), not the
    // wordfile's bare-name /C6 — it carries types, client/both sides and comments for hover.
    Console.Write($"  {prefix}");
    GenerateRadiantKeys(keysPath, Path.Combine(apiDir, $"{prefix}_radiant_keys.json"), clientKeysPath);

    // /C2 "Common Entity Properties" — script-accessible .fields (the wordfile writes the leading
    // dot); untyped, under one generic kind.
    List<FieldEntry> fieldEntries = [.. CleanNames(ParseWordfileSection(lines, 2), stripLeadingDot: true, lowercase: true)
        .Select(static name => new FieldEntry(name, ""))];
    SortedDictionary<string, List<FieldEntry>> fields = new(StringComparer.Ordinal) { ["entity"] = fieldEntries };
    WriteJson(Path.Combine(apiDir, $"{prefix}_object_fields.json"), fields);
    Console.WriteLine($"  {prefix} object fields: {fieldEntries.Count}");
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
        // The default encoder escapes the HTML-sensitive set — < > & ' + — as \uXXXX, which in
        // documentation text full of <target> placeholders means thousands of escapes and an
        // artifact nobody can read in a diff. These files are never interpolated into HTML, so the
        // relaxed encoder is safe here and writes them literally, as the t7 files already are.
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    });

    File.WriteAllText(path, ToAscii(path, json) + "\n", new System.Text.UTF8Encoding(false));
}

// The bundled artifacts are ASCII, and this is where that is enforced rather than hoped for.
//
// Typographic punctuation is the only thing that ever shows up: a documentation page's smart quotes,
// or — as happened here — an em dash typed into a generated description, which put 244 of them into
// mw2_api_gsc.json. They are folded to their ASCII equivalents, since the character carries no
// meaning the replacement loses. Anything else is reported loudly and left alone, because a silent
// substitution in a name or a value would be worse than a file somebody has to look at.
static string ToAscii(string path, string json)
{
    // Written as code points so the mapping cannot itself be corrupted by an editor's encoding -
    // a table of smart quotes is the one place where a re-encoded source file would silently stop
    // matching the very characters it exists to remove.
    string folded = json
        .Replace((char)0x2014, '-')    // em dash
        .Replace((char)0x2013, '-')    // en dash
        .Replace((char)0x2018, (char)0x27)   // left single quote
        .Replace((char)0x2019, (char)0x27)   // right single quote
        .Replace((char)0x201C, (char)0x22)   // left double quote
        .Replace((char)0x201D, (char)0x22)   // right double quote
        .Replace((char)0x00A0, ' ')    // non-breaking space
        .Replace(((char)0x2026).ToString(), "...");  // ellipsis

    int remaining = folded.Count(static character => character > (char)0x7F);
    if ( remaining > 0 )
    {
        Console.WriteLine($"  WARNING: {Path.GetFileName(path)} still holds {remaining} non-ASCII character(s) after folding.");
    }

    return folded;
}

// Converts the per-function documentation pages into the builtin library, merged with the
// wordfile's bare name list so a function documented nowhere is still known to exist.
static void GenerateWordfileApi(
    string prefix,
    string? htmRoot,
    List<string> wordfileNames,
    string curatedPath,
    string overridesPath,
    string? enrichFromPath,
    string? empiricalPath,
    string outputPath)
{
    Dictionary<string, object> byName = new(StringComparer.OrdinalIgnoreCase);

    // Refuse to trade a rich artifact for a poor one. The pages are a third party's and live outside
    // the repo, so "docs not found" is the NORMAL state of a fresh clone — and without this guard the
    // first innocent run there would silently strip every documented signature from a file under
    // source control. Losing data is not a valid outcome of a regeneration; not gaining any is.
    //
    // A game with a SIBLING to enrich from is a different case and must not be caught here: WaW and
    // BO1 have no pages by design (BO1's ship stripped) and rebuild their detail by inheriting CoD4's
    // output, so a run without docs costs them nothing. The pairing that destroys data is no pages
    // AND nothing to inherit, which is CoD4 alone.
    string? docsRoot = !string.IsNullOrWhiteSpace(htmRoot) && Directory.Exists(htmRoot) ? htmRoot : null;
    bool canInherit = enrichFromPath is not null && File.Exists(enrichFromPath);

    if ( docsRoot is null && !canInherit )
    {
        int existing = CountDocumented(outputPath);
        if ( existing > 0 )
        {
            Console.WriteLine(
                $"  {prefix} REFUSING to regenerate: {outputPath} holds {existing} documented entries, and this run has neither a documentation root nor a sibling to inherit from. Set the docs environment variable, or delete the file to rebuild it from names alone.");
            return;
        }
    }

    if ( docsRoot is not null )
    {
        List<string> pages = [.. Directory.EnumerateFiles(docsRoot, "*.htm", SearchOption.AllDirectories)];
        pages.Sort(StringComparer.OrdinalIgnoreCase);

        foreach ( string page in pages )
        {
            Cod4Entry? entry = ParseCod4Page(File.ReadAllText(page));
            if ( entry is not null && !byName.ContainsKey(entry.Name) )
            {
                byName[entry.Name] = entry;
            }
        }

        Console.WriteLine($"  {prefix} documented pages: {byName.Count} of {pages.Count}");
    }
    else
    {
        Console.WriteLine($"  {prefix} documentation not found; names only.");
    }

    int documented = byName.Count;

    // Entries written for functions the pages do not cover, carried in curated/ so they survive a
    // regeneration. They fill gaps only: a documented page always wins, since it is the primary
    // source and these are reconstructions.
    int curated = 0;
    if ( File.Exists(curatedPath) )
    {
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(curatedPath));
        foreach ( JsonElement entry in document.RootElement.EnumerateArray() )
        {
            string? name = entry.GetProperty("name").GetString();
            if ( name is null || byName.ContainsKey(name) )
            {
                continue;
            }

            byName[name] = entry.Clone();
            curated++;
        }
    }

    // A name this game shares with an already-enriched sibling in the same engine lineage takes
    // that entry rather than staying bare. BO1's wordfile lists the SAME functions as CoD4's — the
    // syntax file was carried forward across CoD4, WaW and BO1 unchanged — so the signatures
    // apply, and a name-only entry would throw away work already done.
    int inherited = 0;
    Dictionary<string, JsonElement> sibling = new(StringComparer.OrdinalIgnoreCase);
    if ( enrichFromPath is not null && File.Exists(enrichFromPath) )
    {
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(enrichFromPath));
        foreach ( JsonElement entry in document.RootElement.GetProperty("api").EnumerateArray() )
        {
            string? name = entry.GetProperty("name").GetString();
            if ( name is not null )
            {
                sibling[name] = entry.Clone();
            }
        }
    }

    foreach ( string name in wordfileNames )
    {
        if ( byName.ContainsKey(name) )
        {
            continue;
        }

        if ( sibling.TryGetValue(name, out JsonElement match) )
        {
            byName[name] = match;
            inherited++;
            continue;
        }

        byName[name] = new Cod4Entry(name, null, [], null, null, null, null);
    }

    // The wordfile is a syntax-highlighting list, not a complete engine contract. BO1 and WaW
    // ship functions their carried-forward wordfiles never learned, and their own full script trees
    // are the empirical proof that those names exist. Prefer a documented entry from either
    // lineage when one exists; otherwise keep a deliberately sparse, aiGenerated entry so the
    // resolver knows the name without inventing a signature. The report is committed test output,
    // making this merge reproducible while keeping the generated API itself an artifact.
    int empirical = 0;
    int empiricalInherited = 0;
    Dictionary<string, JsonElement> t7 = LoadApiEntries(Path.Combine(Path.GetDirectoryName(outputPath) ?? "", "t7_api_gsc.json"));
    foreach ( EmpiricalBuiltinEvidence evidence in ReadEmpiricalBuiltins(empiricalPath) )
    {
        if ( !evidence.Languages.Contains("Gsc", StringComparer.OrdinalIgnoreCase)
            || byName.ContainsKey(evidence.Name) )
        {
            continue;
        }

        if ( sibling.TryGetValue(evidence.Name, out JsonElement cod4Entry) )
        {
            byName[evidence.Name] = cod4Entry;
            empiricalInherited++;
        }
        else if ( t7.TryGetValue(evidence.Name, out JsonElement t7Entry) )
        {
            byName[evidence.Name] = t7Entry;
            empiricalInherited++;
        }
        else
        {
            byName[evidence.Name] = SparseEmpiricalEntry(prefix, evidence);
            empirical++;
        }
    }

    int recased = RecaseReconstructedNames(Path.GetDirectoryName(outputPath) ?? "", outputPath, byName);
    int corrected = ApplyOverrides(prefix, overridesPath, byName);

    List<object> all = [.. byName.OrderBy(static pair => pair.Key, StringComparer.OrdinalIgnoreCase).Select(static pair => pair.Value)];
    WriteJson(outputPath, new Cod4ApiFile(all), camelCase: true);
    Console.WriteLine($"  {prefix} api functions: {all.Count} ({documented} documented, {curated} reconstructed, {inherited} inherited, {empiricalInherited} empirical inherited, {empirical} empirical name-only, {all.Count - documented - curated - inherited - empiricalInherited - empirical} name-only, {corrected} corrected, {recased} recased)");
}

// How many entries in an existing artifact came from a documentation page. Read straight off the
// flags the last run wrote, so it needs no knowledge of the doc format.
static int CountDocumented(string outputPath)
{
    if ( !File.Exists(outputPath) )
    {
        return 0;
    }

    try
    {
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(outputPath));
        int count = 0;
        foreach ( JsonElement entry in document.RootElement.GetProperty("api").EnumerateArray() )
        {
            if ( !entry.TryGetProperty("flags", out JsonElement flags) )
            {
                continue;
            }

            foreach ( JsonElement flag in flags.EnumerateArray() )
            {
                if ( flag.ValueKind == JsonValueKind.String
                    && string.Equals(flag.GetString(), "documented", StringComparison.Ordinal) )
                {
                    count++;
                    break;
                }
            }
        }

        return count;
    }
    catch ( JsonException )
    {
        // An unreadable artifact is not something to protect.
        return 0;
    }
}

// Loads the API entries from a sibling artifact without converting them through the generator's
// narrower records. Keeping the JsonElement preserves descriptions, overloads and provenance from
// the documented CoD4/BO3 source exactly as written.
static Dictionary<string, JsonElement> LoadApiEntries(string path)
{
    Dictionary<string, JsonElement> entries = new(StringComparer.OrdinalIgnoreCase);
    if ( !File.Exists(path) )
    {
        return entries;
    }

    try
    {
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
        if ( !document.RootElement.TryGetProperty("api", out JsonElement api)
            || api.ValueKind != JsonValueKind.Array )
        {
            return entries;
        }

        foreach ( JsonElement entry in api.EnumerateArray() )
        {
            if ( entry.TryGetProperty("name", out JsonElement name)
                && name.ValueKind == JsonValueKind.String
                && name.GetString() is string value )
            {
                entries[value] = entry.Clone();
            }
        }
    }
    catch ( JsonException )
    {
        // A missing/invalid sibling is equivalent to having no enrichment source.
    }

    return entries;
}

static Dictionary<string, JsonElement> LoadArrayEntries(string path)
{
    Dictionary<string, JsonElement> entries = new(StringComparer.OrdinalIgnoreCase);
    if ( !File.Exists(path) )
    {
        return entries;
    }

    try
    {
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
        if ( document.RootElement.ValueKind != JsonValueKind.Array )
        {
            return entries;
        }

        foreach ( JsonElement entry in document.RootElement.EnumerateArray() )
        {
            if ( entry.TryGetProperty("name", out JsonElement name)
                && name.ValueKind == JsonValueKind.String
                && name.GetString() is string value )
            {
                entries[value] = entry.Clone();
            }
        }
    }
    catch ( JsonException )
    {
        // A malformed curated source is treated as absent; the report fallback can still run.
    }

    return entries;
}

// Materializes the first successful harvest into the editable curated layer. The guard is
// intentional: these files are the durable evidence once written, so a later zero-miss harvest
// must not erase them.
static void EnsureEmpiricalSources(string prefix, string harvestDir, string apiDir, string curatedDir)
{
    string reportPath = Path.Combine(harvestDir, $"{prefix}_missing_builtins.json");
    string serverSourcePath = Path.Combine(curatedDir, $"{prefix}_ai_builtins.json");
    string clientSourcePath = Path.Combine(curatedDir, $"{prefix}_csc_empirical.json");

    if ( !File.Exists(reportPath) )
    {
        Console.WriteLine($"  {prefix} empirical sources: harvest not found, leaving curated sources unchanged.");
        return;
    }

    using JsonDocument report = JsonDocument.Parse(File.ReadAllText(reportPath));
    if ( !report.RootElement.TryGetProperty("functions", out JsonElement functions)
        || functions.ValueKind != JsonValueKind.Array )
    {
        return;
    }

    Dictionary<string, JsonElement> server = LoadApiEntries(Path.Combine(apiDir, $"{prefix}_api_gsc.json"));
    Dictionary<string, JsonElement> client = LoadApiEntries(Path.Combine(apiDir, $"{prefix}_api_csc.json"));
    List<JsonElement> serverEntries = [];
    List<JsonElement> clientEntries = [];
    foreach ( JsonElement finding in functions.EnumerateArray() )
    {
        if ( !finding.TryGetProperty("name", out JsonElement nameElement)
            || nameElement.ValueKind != JsonValueKind.String
            || nameElement.GetString() is not string name
            || !finding.TryGetProperty("languages", out JsonElement languages)
            || languages.ValueKind != JsonValueKind.Array )
        {
            continue;
        }

        bool gsc = languages.EnumerateArray().Any(static language =>
            language.ValueKind == JsonValueKind.String
            && string.Equals(language.GetString(), "Gsc", StringComparison.OrdinalIgnoreCase));
        bool csc = languages.EnumerateArray().Any(static language =>
            language.ValueKind == JsonValueKind.String
            && string.Equals(language.GetString(), "Csc", StringComparison.OrdinalIgnoreCase));

        if ( gsc && server.TryGetValue(name, out JsonElement serverEntry) )
        {
            serverEntries.Add(serverEntry);
        }

        if ( csc && client.TryGetValue(name, out JsonElement clientEntry) )
        {
            clientEntries.Add(clientEntry);
        }
    }

    // An EMPTY result is never written. The guard above is "first harvest wins", so a file created
    // from a run that found nothing would take that slot permanently and silence every later harvest
    // — and it is not a hypothetical: bringing MW2 up ran the generator before its first sweep, and
    // the resulting [] would have swallowed all 335 findings on the next run.
    if ( serverEntries.Count > 0 && !File.Exists(serverSourcePath) )
    {
        WriteJson(serverSourcePath, serverEntries);
        Console.WriteLine($"  {prefix} empirical GSC source: {serverEntries.Count} entries");
    }

    if ( clientEntries.Count > 0 && !File.Exists(clientSourcePath) )
    {
        WriteJson(clientSourcePath, clientEntries);
        Console.WriteLine($"  {prefix} empirical CSC source: {clientEntries.Count} entries");
    }
}

// The harvest is intentionally a small, stable evidence format. Only the fields needed to make a
// provenance-bearing sparse API entry are read; new report fields can be added without changing the
// generator.
static List<EmpiricalBuiltinEvidence> ReadEmpiricalBuiltins(string? path)
{
    List<EmpiricalBuiltinEvidence> findings = [];
    if ( path is null || !File.Exists(path) )
    {
        return findings;
    }

    try
    {
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
        if ( !document.RootElement.TryGetProperty("game", out JsonElement gameElement)
            || gameElement.ValueKind != JsonValueKind.String
            || gameElement.GetString() is not string game
            || !document.RootElement.TryGetProperty("functions", out JsonElement functions)
            || functions.ValueKind != JsonValueKind.Array )
        {
            return findings;
        }

        foreach ( JsonElement function in functions.EnumerateArray() )
        {
            if ( !function.TryGetProperty("name", out JsonElement nameElement)
                || nameElement.ValueKind != JsonValueKind.String
                || nameElement.GetString() is not string name )
            {
                continue;
            }

            int calls = function.TryGetProperty("calls", out JsonElement callsElement)
                && callsElement.TryGetInt32(out int callCount) ? callCount : 0;
            int files = function.TryGetProperty("fileCount", out JsonElement filesElement)
                && filesElement.TryGetInt32(out int fileCount) ? fileCount : 0;
            List<int> argumentCounts = [];
            if ( function.TryGetProperty("observedArgCounts", out JsonElement counts)
                && counts.ValueKind == JsonValueKind.Array )
            {
                foreach ( JsonElement count in counts.EnumerateArray() )
                {
                    if ( count.TryGetInt32(out int value) )
                    {
                        argumentCounts.Add(value);
                    }
                }
            }

            bool calledAsMethod = function.TryGetProperty("calledAsMethod", out JsonElement methodElement)
                && methodElement.ValueKind == JsonValueKind.True;
            string access = function.TryGetProperty("access", out JsonElement accessElement)
                && accessElement.ValueKind == JsonValueKind.String
                ? accessElement.GetString() ?? "unknown"
                : "unknown";
            List<string> languages = [];
            if ( function.TryGetProperty("languages", out JsonElement languageArray)
                && languageArray.ValueKind == JsonValueKind.Array )
            {
                foreach ( JsonElement language in languageArray.EnumerateArray() )
                {
                    if ( language.ValueKind == JsonValueKind.String && language.GetString() is string value )
                    {
                        languages.Add(value);
                    }
                }
            }

            List<string> sites = [];
            if ( function.TryGetProperty("sites", out JsonElement siteArray)
                && siteArray.ValueKind == JsonValueKind.Array )
            {
                foreach ( JsonElement site in siteArray.EnumerateArray() )
                {
                    if ( site.ValueKind == JsonValueKind.String && site.GetString() is string value )
                    {
                        sites.Add(value);
                    }
                }
            }

            List<EmpiricalArgumentShape> shapes = [];
            if ( function.TryGetProperty("argumentShapes", out JsonElement shapeArray)
                && shapeArray.ValueKind == JsonValueKind.Array )
            {
                foreach ( JsonElement shape in shapeArray.EnumerateArray() )
                {
                    int position = shape.TryGetProperty("position", out JsonElement positionElement)
                        && positionElement.TryGetInt32(out int index) ? index : shapes.Count;
                    shapes.Add(new EmpiricalArgumentShape(
                        position, ReadStrings(shape, "names"), ReadStrings(shape, "types")));
                }
            }

            findings.Add(new EmpiricalBuiltinEvidence(
                game, name, calls, files, argumentCounts, calledAsMethod, access, languages, sites, shapes));
        }
    }
    catch ( JsonException )
    {
        // A malformed report cannot safely add names to an API; leave the artifact unchanged by
        // this layer and let the caller's normal wordfile/sibling generation proceed.
    }

    return findings;
}

// The string entries of a named array property, in the order the report wrote them (which is
// most-observed first), skipping anything that is not a string.
static IReadOnlyList<string> ReadStrings(JsonElement parent, string property)
{
    List<string> values = [];
    if ( !parent.TryGetProperty(property, out JsonElement array) || array.ValueKind != JsonValueKind.Array )
    {
        return values;
    }

    foreach ( JsonElement item in array.EnumerateArray() )
    {
        if ( item.ValueKind == JsonValueKind.String && item.GetString() is string value )
        {
            values.Add(value);
        }
    }

    return values;
}

// An entry RECONSTRUCTED from call sites, for a name no documented CoD4 or Black Ops III library
// covers. The corpus proves the name exists; the shape here is the callers' own evidence about it,
// which is the only evidence there is.
//
// What is claimed, and on what basis:
//   * The parameter COUNT is the widest call seen. Not an upper bound on what the engine accepts —
//     only on what the shipped scripts asked for.
//   * MANDATORY stops at the narrowest call seen. Every position past it is written optional
//     because the corpus demonstrates a call that omits it.
//   * A parameter NAME is the word the callers used for it, taken verbatim, and null where they
//     always passed an expression rather than a variable.
//   * A TYPE only where the spelling IS the type (a string literal, a vector, a number) and every
//     observation of that position agreed. One dissenting spelling drops it to untyped.
//
// The safety of doing this at all rests on HasReliableBuiltinSignatures, which MW2 does NOT set:
// ArgumentCountLint judges a call against a builtin signature only for games that do, so a
// reconstruction here can never turn into a diagnostic on someone's code. It reaches hover,
// completion and signature help — where a named parameter list is worth far more than a bare name —
// and nothing that reports.
// The global objects are the one thing callers write that is never a NAME for the argument. A file
// passing `self` at position two is saying which entity it handed over, not that the parameter is
// called self — and `stopFxOnTag( effect, self, tag )` reads as though the engine wants a thing
// called self. Skipped in favour of the next-ranked spelling, and the report keeps the raw evidence.
static string? PickParameterName(IReadOnlyList<string> names)
{
    string[] globals = ["self", "level", "game", "world", "anim"];
    foreach ( string name in names )
    {
        if ( !globals.Contains(name, StringComparer.OrdinalIgnoreCase) )
        {
            return name;
        }
    }

    return null;
}

static Dictionary<string, object> SparseEmpiricalEntry(string game, EmpiricalBuiltinEvidence evidence)
{
    string arities = evidence.ArgumentCounts.Count == 0
        ? "none recorded"
        : string.Join(", ", evidence.ArgumentCounts);
    string sites = evidence.Sites.Count == 0
        ? ""
        : $" First sites: {string.Join(", ", evidence.Sites)}.";

    int widest = evidence.ArgumentCounts.Count == 0 ? 0 : evidence.ArgumentCounts.Max();
    int narrowest = evidence.ArgumentCounts.Count == 0 ? 0 : evidence.ArgumentCounts.Min();

    List<object> parameters = [];
    for ( int position = 0; position < widest; position++ )
    {
        EmpiricalArgumentShape? shape = evidence.ArgumentShapes.FirstOrDefault(s => s.Position == position);
        string? name = shape is null ? null : PickParameterName(shape.Names);
        string? type = shape is not null && shape.Types.Count == 1 ? shape.Types[0] : null;

        // Alternatives are worth showing: a position the callers name three different ways is
        // telling you what it is far better than any one of those words alone.
        string alternatives = shape is null || shape.Names.Count <= 1
            ? ""
            : $" Also written {string.Join(", ", shape.Names.Skip(1))}.";

        Dictionary<string, object?> parameter = new()
        {
            ["name"] = name ?? $"arg{position + 1}",
            ["description"] = name is null
                ? "Reconstructed from call sites; every observed call passed an expression here, so the callers give it no name."
                : $"Reconstructed from call sites, where callers name it '{name}'.{alternatives}",
            ["mandatory"] = position < narrowest,
        };

        if ( type is not null )
        {
            parameter["type"] = new Dictionary<string, object> { ["dataType"] = type };
        }

        parameters.Add(parameter);
    }

    List<object> overloads = [];
    if ( parameters.Count > 0 || evidence.CalledAsMethod )
    {
        Dictionary<string, object?> overload = new()
        {
            ["calledOn"] = evidence.CalledAsMethod
                ? new Dictionary<string, object?>
                {
                    ["name"] = "entity",
                    ["description"] = "Reconstructed from call sites: this function is called on a target.",
                }
                : null,
            ["parameters"] = parameters,
        };

        overloads.Add(overload);
    }

    return new Dictionary<string, object>
    {
        ["name"] = evidence.Name,
        ["description"] = $"Observed in the {game} shipped script corpus ({evidence.Calls} calls across {evidence.Files} files; argument counts {arities}; access {evidence.Access}{(evidence.CalledAsMethod ? "; called as a method" : "")}); no documented CoD4 or Black Ops III signature was available.",
        ["overloads"] = overloads,
        ["flags"] = new[] { "aiGenerated" },
        ["remarks"] = new[]
        {
            $"Empirical builtin: {evidence.Calls} call(s) across {evidence.Files} {game} script file(s), observed argument counts {arities}, access {evidence.Access}.{(evidence.CalledAsMethod ? " It is called on a target." : "")}{sites} The name is known to exist; the signature below is RECONSTRUCTED from how the shipped scripts call it, not documented; parameter names are the callers' own words, and the count is the widest call seen rather than a limit the engine imposes."
        },
    };
}

// Restores the engine's CamelCase spelling to names that only ever arrived in lower case.
//
// A reconstructed entry is named from a DIAGNOSTIC MESSAGE, which quotes the call site, and GSC is
// case-insensitive so scripts write `playsoundatpos(...)`. Every documented library spells the same
// kind of name `PlaySoundAtPos`, so a completion list mixing the two reads as though the lower-case
// half were a different, lesser kind of thing — which was the report: `playsoundatpos` sitting under
// `PlaySound`, `PlaySoundAsMaster` and `PlaySoundToPlayer`.
//
// Nothing here is invented. The word list comes from the two libraries that ARE documented — CoD4's
// and BO3's — split on their own case boundaries, so a word is only capitalized the way an engine
// name already capitalizes it, and a name that cannot be built entirely out of those words is left
// exactly as the corpus spelled it. That is why the pass reports a count: it is expected to leave
// some behind.
//
// Safe to do at all because BuiltinApi.Find is case-insensitive, so this changes what is DISPLAYED
// and never what resolves.
static int RecaseReconstructedNames(string apiDirectory, string outputPath, Dictionary<string, object> byName)
{
    Dictionary<string, string> vocabulary = BuildNameVocabulary(apiDirectory, outputPath, out Dictionary<string, string> wholeNames);
    if ( vocabulary.Count == 0 )
    {
        return 0;
    }

    int recased = 0;
    foreach ( string key in byName.Keys.ToList() )
    {
        object entry = byName[key];
        string name = NameOf(entry);

        // Any name carrying no case information beyond its first letter, whatever its provenance.
        //
        // The flag is deliberately NOT the test. Reconstructed names arrive fully lower case, but
        // CoD4's documentation pages are themselves inconsistent — the same library holds AllowLean,
        // Allowleanleft and Allowleanright — and a flat documented name is the same defect with a
        // better pedigree. It is still only ever recased, never re-described: everything a page
        // states about the function is untouched.
        //
        // An underscore name (missile_setTargetEnt) is left alone: nothing in the documented
        // libraries uses that convention, so there is no evidence for how to case it.
        if ( name.Length == 0
            || name[1..] != name[1..].ToLowerInvariant()
            || name.Contains('_', StringComparison.Ordinal) )
        {
            continue;
        }

        // A documented library spelling this exact name wins outright over any reconstruction —
        // unless that spelling is the flat one being corrected, which is the Allowleanleft case:
        // the entry IS its own would-be source, so falling through to the words is the only way out.
        string lowered = name.ToLowerInvariant();
        string? replacement = wholeNames.TryGetValue(lowered, out string? exact) && exact[1..] != exact[1..].ToLowerInvariant()
            ? exact
            : SegmentName(lowered, vocabulary);

        if ( replacement is null || replacement == name )
        {
            continue;
        }

        // The dictionary is case-insensitive, so this is the same slot — only the entry's own name
        // field changes, and no entry can collide with another by being recased.
        byName[key] = WithName(entry, replacement);
        recased++;
    }

    return recased;
}

// The words the documented libraries are built from, keyed lower-case, valued with the spelling
// those libraries use most often for that word.
//
// TWO REJECTIONS matter, and both came from the source data disagreeing with itself:
//   * A word under three letters is only trusted when the libraries use it as a word at least three
//     times. Without this, `spawndrone` — whose "drone" nothing documents — was assembled out of
//     "dr" and "one" into SpawnDROne. It is now left lower case, which is the honest answer.
//   * A word that is ITSELF two other words, and is rare, is dropped in favour of its parts. CoD4
//     and BO3 contain VectortoAngles, UseServerVisionset and SetHideonClientWhenScriptedAnimCompleted,
//     so "vectorto", "visionset" and "hideon" all look like words and outvoted the correct split —
//     giving VectortoYaw and HideonClient. Requiring three sightings keeps genuine compounds the
//     libraries use constantly (offset, 18 sightings) while dropping these one-offs.
static Dictionary<string, string> BuildNameVocabulary(
    string apiDirectory, string outputPath, out Dictionary<string, string> wholeNames)
{
    wholeNames = new Dictionary<string, string>(StringComparer.Ordinal);
    Dictionary<string, string> vocabulary = new(StringComparer.Ordinal);
    if ( apiDirectory.Length == 0 )
    {
        return vocabulary;
    }

    // The documented pair only. A reconstructed library must not be a source of spellings for the
    // next reconstruction, or one bad split propagates across every game.
    string[] sources = ["cod4_api_gsc.json", "t7_api_gsc.json", "t7_api_csc.json"];

    Dictionary<string, Dictionary<string, int>> sightings = new(StringComparer.Ordinal);
    foreach ( string source in sources )
    {
        string path = Path.Combine(apiDirectory, source);
        if ( string.Equals(Path.GetFullPath(path), Path.GetFullPath(outputPath), StringComparison.OrdinalIgnoreCase) )
        {
            continue;
        }

        foreach ( string name in LoadApiEntries(path).Keys )
        {
            // An underscore name contributes its WORDS but is never offered as a whole spelling: the
            // underscore convention is a separate one and nothing says a reconstructed name follows
            // it. Skipping them outright cost real vocabulary — "Drone" appears only in
            // Missile_DroneSetVisible, so spawndrone had no correct split available.
            if ( !name.Contains('_', StringComparison.Ordinal) )
            {
                wholeNames.TryAdd(name.ToLowerInvariant(), name);
            }

            foreach ( string word in name.Split('_').SelectMany(SplitCamelCase) )
            {
                string key = word.ToLowerInvariant();
                if ( !sightings.TryGetValue(key, out Dictionary<string, int>? spellings) )
                {
                    spellings = new Dictionary<string, int>(StringComparer.Ordinal);
                    sightings[key] = spellings;
                }

                spellings[word] = spellings.TryGetValue(word, out int seen) ? seen + 1 : 1;
            }
        }
    }

    Dictionary<string, int> totals = sightings.ToDictionary(
        static pair => pair.Key, static pair => pair.Value.Values.Sum(), StringComparer.Ordinal);

    // A SINGLE letter is never a word here. Acronym runs leave "s", "a", "d" and "x" behind looking
    // like frequent words, and the segmenter then spelled ClearCenterPopups as ClearCenterPopupS and
    // HasEyes as HasEyeS. Two letters still qualify (on, to, at, AI, FX) but must be earned.
    HashSet<string> candidates = [.. sightings.Keys.Where(key => key.Length >= 3 || (key.Length == 2 && totals[key] >= 3))];

    foreach ( string key in candidates )
    {
        if ( totals[key] < 3 && IsBuiltFrom(key, candidates) )
        {
            continue;
        }

        string winner = sightings[key].OrderByDescending(static pair => pair.Value)
            .ThenBy(static pair => pair.Key, StringComparer.Ordinal).First().Key;

        // An acronym the libraries write in full caps (FX, AI, HUD) keeps that shape; anything else
        // is a word and takes an initial capital, since that is what every name here does with it.
        vocabulary[key] = winner.All(char.IsUpper) ? winner : char.ToUpperInvariant(winner[0]) + winner[1..];
    }

    return vocabulary;
}

// Whether this word can be spelled out of OTHER words in the same pool.
static bool IsBuiltFrom(string word, HashSet<string> pool)
{
    bool[] reachable = new bool[word.Length + 1];
    reachable[0] = true;

    for ( int end = 1; end <= word.Length; end++ )
    {
        for ( int start = 0; start < end; start++ )
        {
            string part = word[start..end];
            if ( reachable[start] && part.Length != word.Length && pool.Contains(part) )
            {
                reachable[end] = true;
                break;
            }
        }
    }

    return reachable[word.Length];
}

// The words in a name, on its own case boundaries. An upper-case RUN that is not the start of a
// word is one word (the FX of PlayFXOnTag), which is what keeps acronyms whole.
static List<string> SplitCamelCase(string name)
{
    return [.. Regex.Matches(name, "[A-Z]+(?![a-z])|[A-Z][a-z0-9]*|[a-z0-9]+").Select(static match => match.Value)];
}

// One word's spelling, allowing an English plural of a word the libraries only ever use singular.
// The libraries have Popup and Eye but no Popups or Eyes, and without this the plural fell to the
// single-letter split that produced ClearCenterPopupS. The plural adds no capital of its own, so it
// invents nothing: it reuses the documented spelling and appends the letter already in the name.
static string? LookUpWord(string word, Dictionary<string, string> vocabulary, out bool isPlural)
{
    isPlural = false;

    if ( vocabulary.TryGetValue(word, out string? spelling) )
    {
        return spelling;
    }

    if ( word.Length > 3 && word.EndsWith('s') && vocabulary.TryGetValue(word[..^1], out string? singular) )
    {
        isPlural = true;
        return singular + "s";
    }

    return null;
}

// Spells a lower-case name out of the vocabulary, or null when it cannot be spelled entirely.
// Fewest words wins, and longest-first breaks the tie — a name assembled from many short fragments
// is the shape a wrong answer takes.
static string? SegmentName(string name, Dictionary<string, string> vocabulary)
{
    int length = name.Length;
    int[] words = new int[length + 1];
    long[] penalty = new long[length + 1];
    string?[] chosen = new string?[length + 1];
    int[] from = new int[length + 1];

    for ( int index = 1; index <= length; index++ )
    {
        words[index] = int.MaxValue;
    }

    for ( int end = 1; end <= length; end++ )
    {
        for ( int start = Math.Max(0, end - 24); start < end; start++ )
        {
            if ( words[start] == int.MaxValue )
            {
                continue;
            }

            if ( LookUpWord(name[start..end], vocabulary, out bool isPlural) is not string spelling )
            {
                continue;
            }

            // A documented word beats an inferred plural on a tie, and the tie is not hypothetical:
            // canplayerplacesentry splits into place+sentry and places+entry at identical cost, and
            // without this it came out CanPlayerPlacesEntry.
            int count = words[start] + 1;
            long score = penalty[start] - ((long)(end - start) * (end - start)) + (isPlural ? 1 : 0);
            if ( count < words[end] || (count == words[end] && score < penalty[end]) )
            {
                words[end] = count;
                penalty[end] = score;
                chosen[end] = spelling;
                from[end] = start;
            }
        }
    }

    if ( words[length] == int.MaxValue )
    {
        return null;
    }

    List<string> parts = [];
    for ( int at = length; at > 0; at = from[at] )
    {
        parts.Add(chosen[at]!);
    }

    parts.Reverse();
    return string.Concat(parts);
}

// The name of an entry, whichever of the three shapes byName is holding.
static string NameOf(object entry)
{
    switch ( entry )
    {
        case Cod4Entry typed:
            return typed.Name;
        case Dictionary<string, object> map:
            return map.TryGetValue("name", out object? name) ? name as string ?? "" : "";
        case JsonElement element when element.ValueKind == JsonValueKind.Object:
            return element.TryGetProperty("name", out JsonElement value) && value.ValueKind == JsonValueKind.String
                ? value.GetString() ?? ""
                : "";
        default:
            return "";
    }
}

// The same entry under a new name, leaving every other field exactly as it was.
static object WithName(object entry, string name)
{
    switch ( entry )
    {
        case Cod4Entry typed:
            return typed with { Name = name };
        case Dictionary<string, object> map:
        {
            Dictionary<string, object> copy = new(map) { ["name"] = name };
            return copy;
        }
        case JsonElement element when element.ValueKind == JsonValueKind.Object:
        {
            JsonObject copy = JsonNode.Parse(element.GetRawText())!.AsObject();
            copy["name"] = name;
            return copy;
        }
        default:
            return entry;
    }
}

// Corrects signatures the documentation gets WRONG, as the last word over every other layer.
//
// This is the layer to reach for when a page's facts do not match the engine, and it exists because
// the alternative does not survive: the generated api file is an artifact, so an edit made there is
// destroyed by the next run, and the pages themselves belong to somebody else. WaW and BO1 need no
// entries of their own — they inherit CoD4's corrected output through enrichFrom.
//
// Only optionalFrom is expressible, because that is the correction the documentation actually
// needs. Its Required/Optional split is the one thing it gets wrong often, always in the same
// direction (a trailing argument documented as required), and optional arguments in GSC are always
// trailing — so an index past which everything is optional says all there is to say. A shape that
// could rewrite names, types or descriptions would be a second source of truth for the parts the
// pages get RIGHT, which is how a curated layer turns into a fork.
static int ApplyOverrides(string prefix, string overridesPath, Dictionary<string, object> byName)
{
    if ( !File.Exists(overridesPath) )
    {
        return 0;
    }

    int corrected = 0;
    using JsonDocument document = JsonDocument.Parse(File.ReadAllText(overridesPath));

    foreach ( JsonElement element in document.RootElement.EnumerateArray() )
    {
        string? name = element.GetProperty("name").GetString();
        if ( name is null )
        {
            continue;
        }

        // 1-based, matching how the pages number their own argument lists. Absent means the mandatory
        // flags are already right and only the parameter LIST is being corrected.
        int optionalFrom = element.TryGetProperty("optionalFrom", out JsonElement from)
            ? from.GetInt32()
            : int.MaxValue;

        // The function's full parameter list, where the page lists fewer than it takes. Two facts in
        // one field, and both need a human: how MANY there are, which comes from counting arguments
        // at real call sites, and what each one IS, which comes from reading the page's own
        // description and example.
        //
        // Named rather than generated. An earlier pass padded these mechanically as `arg1`, `arg2`,
        // and the result was worse than the gap it filled: hover presented an invented name in the
        // same place, and the same style, as a documented one. A correction layer may repair what
        // the documentation gets wrong; it may not fabricate what the documentation does not say.
        List<string> names = [];
        if ( element.TryGetProperty("parameters", out JsonElement given) )
        {
            foreach ( JsonElement parameterName in given.EnumerateArray() )
            {
                string? text = parameterName.GetString();
                if ( text is not null )
                {
                    names.Add(text);
                }
            }
        }

        // Loud rather than silent. An override naming something this game does not have is a stale
        // entry — a renamed function, or a correction that landed upstream — and a file of
        // corrections nobody is told have stopped applying is worse than no file.
        if ( !byName.TryGetValue(name, out object? existing) || existing is not Cod4Entry entry )
        {
            Console.WriteLine($"  {prefix} override for '{name}' matched no documented entry; ignoring.");
            continue;
        }

        List<Cod4Overload> overloads = [];
        foreach ( Cod4Overload overload in entry.Overloads )
        {
            List<Cod4Parameter> parameters = [];
            for ( int index = 0; index < overload.Parameters.Count; index++ )
            {
                Cod4Parameter parameter = overload.Parameters[index];
                bool mandatory = parameter.Mandatory && index + 1 < optionalFrom;

                // A curated name replaces a placeholder, never a documented one: the pages get names
                // right far more often than they get the count right.
                string parameterName = index < names.Count && string.IsNullOrEmpty(parameter.Name)
                    ? names[index]
                    : parameter.Name;

                parameters.Add(parameter with { Name = parameterName, Mandatory = mandatory });
            }

            // Everything the page never listed. Optional without exception — widening a list can
            // then never turn code that works into an error.
            for ( int index = parameters.Count; index < names.Count; index++ )
            {
                parameters.Add(new Cod4Parameter(names[index], null, false, null));
            }

            overloads.Add(overload with { Parameters = parameters });
        }

        // A name the pages cover with no parameters at all: there is no overload to widen, so one is
        // made. Without this the correction does nothing for exactly the entries needing it most —
        // `IsSubStr` and `ToLower` list none and are called with two and one.
        if ( overloads.Count == 0 && names.Count > 0 )
        {
            List<Cod4Parameter> created = [];
            foreach ( string parameterName in names )
            {
                created.Add(new Cod4Parameter(parameterName, null, false, null));
            }

            overloads.Add(new Cod4Overload(null, created));
        }

        // Flagged so the correction is visible in the artifact itself rather than only in this tool.
        List<string> flags = entry.Flags is null ? [] : [.. entry.Flags];
        if ( !flags.Contains("corrected") )
        {
            flags.Add("corrected");
        }

        // Whether the engine ships this function only in a development build, which is a fact about
        // THIS game and cannot be inherited from another's. Stated here so the answer travels with
        // the function's own data; absent, the loader falls back to its curated list.
        bool? devOnly = element.TryGetProperty("devOnly", out JsonElement dev) ? dev.GetBoolean() : null;

        byName[name] = entry with { Overloads = overloads, Flags = flags, DevOnly = devOnly };
        corrected++;
    }

    return corrected;
}

// One documentation page. The pages use two templates; both put the signature in H1 and label
// their sections with H2, so the parse keys off those rather than the surrounding markup.
static Cod4Entry? ParseCod4Page(string html)
{
    string? heading = MatchInner(html, @"<H1>(.*?)</H1>");
    if ( heading is null )
    {
        return null;
    }

    int paren = heading.IndexOf('(');
    string name = (paren < 0 ? heading : heading[..paren]).Trim();
    // Index and navigation pages have prose headings rather than a call signature.
    if ( name.Length == 0 || name.Contains(' ') || name.Contains('-') )
    {
        return null;
    }

    string? module = MatchInner(html, @"Module:\s*([^<]*)<") ?? MatchInner(html, @"Module<PRE>(.*?)</PRE>");
    string? spmp = html.Contains("SP Only", StringComparison.OrdinalIgnoreCase) ? "SP"
        : html.Contains("MP Only", StringComparison.OrdinalIgnoreCase) ? "MP"
        : null;

    Dictionary<string, string> sections = Cod4Sections(html);
    string? description = SectionText(sections, "Summary");
    string? example = SectionText(sections, "Example");

    Cod4CalledOn? calledOn = null;
    string? calledOnText = SectionText(sections, "Call this on") ?? SectionText(sections, "Call this function on");
    if ( !string.IsNullOrWhiteSpace(calledOnText) )
    {
        string? target = MatchInner(calledOnText, @"^<([^>]*)>");
        string rest = target is null ? calledOnText : calledOnText[(calledOnText.IndexOf('>') + 1)..].Trim();
        calledOn = new Cod4CalledOn(NormalizeArgName(target ?? "self", 1), rest.Length == 0 ? null : rest);
    }

    List<Cod4Parameter> parameters = [];
    parameters.AddRange(Cod4Args(sections, "Required Args", mandatory: true));
    parameters.AddRange(Cod4Args(sections, "Optional Args", mandatory: false));

    List<Cod4Overload> overloads = [];
    if ( calledOn is not null || parameters.Count > 0 )
    {
        overloads.Add(new Cod4Overload(calledOn, parameters));
    }

    return new Cod4Entry(
        name,
        description,
        overloads,
        example,
        string.IsNullOrWhiteSpace(module) ? null : module.Trim(),
        spmp,
        ["documented"]);
}

// Splits the page into H2-labelled sections. Headings carry stray markup and a trailing colon in
// one template but not the other, so both are stripped to give one lookup key.
static Dictionary<string, string> Cod4Sections(string html)
{
    Dictionary<string, string> sections = new(StringComparer.OrdinalIgnoreCase);
    MatchCollection headings = Regex.Matches(html, @"<H2>(.*?)</H2>", RegexOptions.IgnoreCase | RegexOptions.Singleline);

    for ( int i = 0; i < headings.Count; i++ )
    {
        string key = StripTags(headings[i].Groups[1].Value).Trim().TrimEnd(':').Trim();
        int start = headings[i].Index + headings[i].Length;
        int end = i + 1 < headings.Count ? headings[i + 1].Index : html.Length;
        if ( key.Length > 0 && !sections.ContainsKey(key) )
        {
            sections[key] = html[start..end];
        }
    }

    return sections;
}

static string? SectionText(Dictionary<string, string> sections, string key)
{
    if ( !sections.TryGetValue(key, out string? body) )
    {
        return null;
    }

    // An example is preformatted, so its own text is the whole value; everything else is prose.
    string? pre = MatchInner(body, @"<PRE>(.*?)</PRE>");
    string text = StripTags(pre ?? body).Trim();
    return text.Length == 0 ? null : text;
}

// `1 : <target> (entity) The entity to check.` — the index, name, type and description are each
// optional in practice, so every part is matched independently.
static List<Cod4Parameter> Cod4Args(Dictionary<string, string> sections, string key, bool mandatory)
{
    List<Cod4Parameter> parameters = [];
    if ( !sections.TryGetValue(key, out string? body) )
    {
        return parameters;
    }

    int index = 0;
    foreach ( Match item in Regex.Matches(body, @"<LI>(.*?)</LI>", RegexOptions.IgnoreCase | RegexOptions.Singleline) )
    {
        index++;
        string text = StripTags(item.Groups[1].Value).Trim();
        text = Regex.Replace(text, @"^\d+\s*:\s*", "");

        string? argName = MatchInner(text, @"^<([^>]*)>");
        if ( argName is not null )
        {
            text = text[(text.IndexOf('>') + 1)..].Trim();
        }

        string? type = MatchInner(text, @"^\(([^)]*)\)");
        if ( type is not null )
        {
            text = text[(text.IndexOf(')') + 1)..].Trim();
        }

        parameters.Add(new Cod4Parameter(
            NormalizeArgName(argName, index),
            text.Length == 0 ? null : text,
            mandatory,
            type is null ? null : new Cod4Type(NormalizeType(type))));
    }

    return parameters;
}

// Documentation names an argument in prose ("aim at point"); an identifier is wanted.
static string NormalizeArgName(string? raw, int index)
{
    if ( string.IsNullOrWhiteSpace(raw) )
    {
        return "arg" + index.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    string cleaned = Regex.Replace(raw.Trim().ToLowerInvariant(), @"[^a-z0-9]+", "_").Trim('_');
    return cleaned.Length == 0 ? "arg" + index.ToString(System.Globalization.CultureInfo.InvariantCulture) : cleaned;
}

// The pages spell types in prose; these map onto the vocabulary the api files already use.
static string NormalizeType(string raw)
{
    string type = raw.Trim().ToLowerInvariant();
    if ( type.StartsWith("const ", StringComparison.Ordinal) )
    {
        type = type[6..];
    }

    return type switch
    {
        "boolean" => "bool",
        "integer" => "int",
        "floating point number" => "float",
        "point" or "a point" => "vector",
        "node" or "path node" => "entity",
        "animation" => "anim",
        _ => type,
    };
}

static string? MatchInner(string input, string pattern)
{
    Match match = Regex.Match(input, pattern, RegexOptions.IgnoreCase | RegexOptions.Singleline);
    return match.Success ? System.Net.WebUtility.HtmlDecode(match.Groups[1].Value).Trim() : null;
}

static string StripTags(string html)
{
    string text = Regex.Replace(html, @"<[^>]+>", " ");
    text = System.Net.WebUtility.HtmlDecode(text);
    return Ascii(Regex.Replace(text, @"\s+", " ").Trim());
}

// Documentation prose carries typographic characters — smart quotes, dashes, non-breaking spaces —
// which serialize as \uXXXX escapes and read as noise. The ones with an obvious ASCII equivalent are
// folded to it; anything else is dropped rather than guessed at, so the artifact stays plain ASCII.
static string Ascii(string text)
{
    System.Text.StringBuilder builder = new(text.Length);
    foreach ( char c in text )
    {
        switch ( c )
        {
            case '‘' or '’' or 'ʼ': builder.Append('\''); break;
            case '“' or '”': builder.Append('"'); break;
            case '–' or '—' or '−': builder.Append('-'); break;
            case '…': builder.Append("..."); break;
            case ' ': builder.Append(' '); break;
            case '°': builder.Append(" degrees"); break;
            default:
                if ( c <= '~' && c >= ' ' )
                {
                    builder.Append(c);
                }

                break;
        }
    }

    return builder.ToString();
}

// Derives a game's CLIENT builtin library from its server one.
//
// WaW and BO1 have client scripts and no documentation describing them, so without this a .csc file
// in those games loads BuiltinApi.Empty — no hover, no signature help, no completion, which is
// exactly what the absence of waw_api_csc.json looks like from the editor. Most engine functions
// exist on both VMs under the same name, so the server library is close to the right answer and far
// better than nothing.
//
// Derived from the GSC ARTIFACT rather than from the wordfile, because the wordfile has no
// client/server split to read: its /C7 section is one undifferentiated list of script commands.
//
// Most of the server library is NOT client-side, so it is pruned to the names there is evidence for
// — either this game's own client scripts call them, or BO3's hand-documented client library lists
// them. See {prefix}_csc_functions.json for the rule and for the standing caveat that none of it is
// documentation-verified, which is exactly why BO3 is not put through this: t7_api_csc.json is a real
// source, and is neither generated nor pruned here.
//
// The one systematic correction is the leading localClientNum — see the other curated list.
static void GenerateClientApi(string prefix, string apiDir, string curatedDir, string? empiricalPath = null)
{
    string serverPath = Path.Combine(apiDir, $"{prefix}_api_gsc.json");
    if ( !File.Exists(serverPath) )
    {
        Console.WriteLine($"  {prefix} client api: no {prefix}_api_gsc.json to derive from; skipped.");
        return;
    }

    HashSet<string> clientIndexed = ReadClientIndexedNames(
        Path.Combine(curatedDir, $"{prefix}_csc_client_indexed.json"));
    HashSet<string>? keep = ReadKeptClientNames(Path.Combine(curatedDir, $"{prefix}_csc_functions.json"));

    JsonNode? root = JsonNode.Parse(File.ReadAllText(serverPath));
    JsonArray? api = root?["api"]?.AsArray();
    if ( api is null )
    {
        Console.WriteLine($"  {prefix} client api: {prefix}_api_gsc.json has no api array; skipped.");
        return;
    }

    int servers = api.Count;

    // A client corpus can call a real VM function that the server wordfile never listed. Keep
    // those names in the client artifact as sparse entries; if BO3 documents one, reuse its full
    // signature, otherwise preserve the same evidence-only shape as the server merge above.
    List<EmpiricalBuiltinEvidence> empirical = ReadEmpiricalBuiltins(empiricalPath);
    Dictionary<string, JsonElement> empiricalSource = LoadArrayEntries(
        Path.Combine(curatedDir, $"{prefix}_csc_empirical.json"));
    Dictionary<string, JsonElement> t7 = LoadApiEntries(Path.Combine(apiDir, "t7_api_csc.json"));
    int empiricalAdded = 0;
    foreach ( JsonElement sourceEntry in empiricalSource.Values )
    {
        string name = sourceEntry.GetProperty("name").GetString()!;
        if ( api.Any(node => string.Equals(
                (node as JsonObject)?["name"]?.GetValue<string>(), name, StringComparison.OrdinalIgnoreCase)) )
        {
            continue;
        }

        api.Add(JsonNode.Parse(sourceEntry.GetRawText()));
        empiricalAdded++;
    }

    foreach ( EmpiricalBuiltinEvidence evidence in empirical.Where(static e => e.Languages.Contains("Csc", StringComparer.OrdinalIgnoreCase)) )
    {
        bool exists = api.Any(node => string.Equals(
            (node as JsonObject)?["name"]?.GetValue<string>(), evidence.Name, StringComparison.OrdinalIgnoreCase));
        if ( exists )
        {
            continue;
        }

        if ( t7.TryGetValue(evidence.Name, out JsonElement documented) )
        {
            api.Add(JsonNode.Parse(documented.GetRawText()));
        }
        else
        {
            api.Add(JsonNode.Parse(JsonSerializer.Serialize(SparseEmpiricalEntry(prefix, evidence), new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            })));
        }

        empiricalAdded++;
    }

    if ( keep is not null )
    {
        foreach ( string name in empiricalSource.Keys )
        {
            keep.Add(name);
        }

        foreach ( EmpiricalBuiltinEvidence evidence in empirical.Where(static e => e.Languages.Contains("Csc", StringComparer.OrdinalIgnoreCase)) )
        {
            keep.Add(evidence.Name);
        }
    }

    // Right to left: removing from a JsonArray shifts everything after it.
    if ( keep is not null )
    {
        for ( int i = api.Count - 1; i >= 0; i-- )
        {
            string? name = (api[i] as JsonObject)?["name"]?.GetValue<string>();
            if ( name is null || !keep.Contains(name) )
            {
                api.RemoveAt(i);
            }
        }
    }

    int adjusted = 0;
    foreach ( JsonNode? entry in api )
    {
        if ( entry is JsonObject function
            && function["name"]?.GetValue<string>() is string name
            && clientIndexed.Contains(name) )
        {
            PrependClientIndex(function);
            adjusted++;
        }
    }

    WriteJson(Path.Combine(apiDir, $"{prefix}_api_csc.json"), root);
    Console.WriteLine(
        $"  {prefix} client api: {api.Count} functions kept of {servers}, {empiricalAdded} empirical-only added, {adjusted} given a leading localClientNum");

    // A curated name the server library does not carry corrects nothing and would sit there looking
    // as though it did, so it is worth a word rather than a silent no-op.
    if ( adjusted != clientIndexed.Count )
    {
        Console.WriteLine(
            $"  {prefix} client api: WARNING {clientIndexed.Count - adjusted} curated name(s) absent from the server library");
    }
}

// Gives one function the client VM's extra leading parameter, on every overload it has.
static void PrependClientIndex(JsonObject function)
{
    JsonObject parameter = new()
    {
        ["name"] = "localClientNum",
        ["description"] = "The splitscreen client this call acts on, 0-3. Client scripts run one script VM per splitscreen client.",
        ["mandatory"] = true,
        ["type"] = new JsonObject
        {
            ["dataType"] = "int",
            ["isArray"] = false,
        },
    };

    if ( function["overloads"] is not JsonArray overloads || overloads.Count == 0 )
    {
        // No documented signature at all, which is the common case on these two games. The client
        // index is still the one parameter known to be there, so state it rather than leave it bare.
        function["overloads"] = new JsonArray(new JsonObject { ["parameters"] = new JsonArray(parameter) });
        return;
    }

    foreach ( JsonNode? node in overloads )
    {
        if ( node is not JsonObject overload )
        {
            continue;
        }

        // A clone per overload: a JsonNode belongs to one parent, so sharing the instance would
        // move it out of the previous overload rather than copy it.
        if ( overload["parameters"] is JsonArray parameters )
        {
            parameters.Insert(0, parameter.DeepClone());
        }
        else
        {
            overload["parameters"] = new JsonArray(parameter.DeepClone());
        }
    }
}

// The curated names to keep in the client library. Null — not empty — when the file is absent, which
// the caller reads as "no prune list, keep everything": an empty set and a missing file mean opposite
// things here, and conflating them would silently ship an empty library.
static HashSet<string>? ReadKeptClientNames(string path)
{
    if ( !File.Exists(path) )
    {
        return null;
    }

    HashSet<string> names = new(StringComparer.OrdinalIgnoreCase);
    JsonArray? listed = JsonNode.Parse(File.ReadAllText(path))?["functions"]?.AsArray();
    if ( listed is null )
    {
        return null;
    }

    foreach ( JsonNode? entry in listed )
    {
        if ( entry?["name"]?.GetValue<string>() is string name )
        {
            names.Add(name);
        }
    }

    return names;
}

// The curated names that take a leading localClientNum. Absent means no corrections, which still
// produces a usable client library — just one that under-declares the client-indexed names.
static HashSet<string> ReadClientIndexedNames(string path)
{
    HashSet<string> names = new(StringComparer.OrdinalIgnoreCase);
    if ( !File.Exists(path) )
    {
        return names;
    }

    JsonArray? listed = JsonNode.Parse(File.ReadAllText(path))?["clientIndexed"]?.AsArray();
    if ( listed is null )
    {
        return names;
    }

    foreach ( JsonNode? entry in listed )
    {
        if ( entry?["name"]?.GetValue<string>() is string name )
        {
            names.Add(name);
        }
    }

    return names;
}

internal sealed record FieldEntry(string Name, string Type, bool ReadOnly = false);
internal sealed record RadiantKey(string Name, string Type, string Side, string Comment);

// The builtin-API artifact shape (matching t7_api_gsc.json): a name plus its overloads. From the
// wordfile only names are known, so overloads is empty until the online-API enrichment pass.
internal sealed record ApiFileOut(List<ApiEntryOut> Api);
internal sealed record ApiEntryOut(string Name, IReadOnlyList<object> Overloads);

// The richer shape the documented pages fill in. Mirrors what ApiLoader reads; WriteJson's
// WhenWritingDefault drops the nulls and falses, so a sparse entry stays sparse.
internal sealed record Cod4ApiFile(List<object> Api);
internal sealed record Cod4Entry(
    string Name,
    string? Description,
    List<Cod4Overload> Overloads,
    string? Example,
    string? Module,
    string? Spmp,
    List<string>? Flags,
    // Whether the engine ships this function only in a development build. Nullable and omitted when
    // unset, so it appears only where an override states it and the loader falls back to its own
    // curated list everywhere else.
    bool? DevOnly = null);
internal sealed record Cod4Overload(Cod4CalledOn? CalledOn, List<Cod4Parameter> Parameters);
internal sealed record Cod4CalledOn(string Name, string? Description);
internal sealed record Cod4Parameter(string Name, string? Description, bool Mandatory, Cod4Type? Type);
internal sealed record Cod4Type(string DataType);
internal sealed record EmpiricalBuiltinEvidence(
    string Game,
    string Name,
    int Calls,
    int Files,
    IReadOnlyList<int> ArgumentCounts,
    bool CalledAsMethod,
    string Access,
    IReadOnlyList<string> Languages,
    IReadOnlyList<string> Sites,
    IReadOnlyList<EmpiricalArgumentShape> ArgumentShapes);

// One argument position as the corpus writes it: the callers' own words for it, most common first,
// and any type its SPELLING proves (a literal, a vector). Empty lists are the normal case for a
// position always passed a computed expression, and mean "nothing observed" rather than "nothing".
internal sealed record EmpiricalArgumentShape(
    int Position,
    IReadOnlyList<string> Names,
    IReadOnlyList<string> Types);
