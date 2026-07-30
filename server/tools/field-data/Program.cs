using System.Text.Json;
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
    enrichFrom: Path.Combine(apiDir, "cod4_api_gsc.json"));

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
    enrichFrom: Path.Combine(apiDir, "cod4_api_gsc.json"));

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
static void GenerateWordfileGameData(string prefix, string wordfilePath, string keysPath, string? clientKeysPath, string apiDir, string? docsRoot, string curatedDir, string? enrichFrom = null)
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
    File.WriteAllText(path, json + "\n", new System.Text.UTF8Encoding(false));
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

    int corrected = ApplyOverrides(prefix, overridesPath, byName);

    List<object> all = [.. byName.OrderBy(static pair => pair.Key, StringComparer.OrdinalIgnoreCase).Select(static pair => pair.Value)];
    WriteJson(outputPath, new Cod4ApiFile(all), camelCase: true);
    Console.WriteLine($"  {prefix} api functions: {all.Count} ({documented} documented, {curated} reconstructed, {inherited} inherited, {all.Count - documented - curated - inherited} name-only, {corrected} corrected)");
}

/// <summary>
/// How many entries in an existing artifact came from a documentation page. Read straight off the
/// flags the last run wrote, so it needs no knowledge of the doc format.
/// </summary>
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

/// <summary>
/// Corrects signatures the documentation gets WRONG, as the last word over every other layer.
///
/// This is the layer to reach for when a page's facts do not match the engine, and it exists because
/// the alternative does not survive: the generated api file is an artifact, so an edit made there is
/// destroyed by the next run, and the pages themselves belong to somebody else. WaW and BO1 need no
/// entries of their own — they inherit CoD4's corrected output through enrichFrom.
///
/// Only <c>optionalFrom</c> is expressible, because that is the correction the documentation actually
/// needs. Its Required/Optional split is the one thing it gets wrong often, always in the same
/// direction (a trailing argument documented as required), and optional arguments in GSC are always
/// trailing — so an index past which everything is optional says all there is to say. A shape that
/// could rewrite names, types or descriptions would be a second source of truth for the parts the
/// pages get RIGHT, which is how a curated layer turns into a fork.
/// </summary>
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

        // 1-based, matching how the pages number their own argument lists.
        int optionalFrom = element.GetProperty("optionalFrom").GetInt32();

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
                parameters.Add(parameter with { Mandatory = mandatory });
            }

            overloads.Add(overload with { Parameters = parameters });
        }

        // Flagged so the correction is visible in the artifact itself rather than only in this tool.
        List<string> flags = entry.Flags is null ? [] : [.. entry.Flags];
        if ( !flags.Contains("corrected") )
        {
            flags.Add("corrected");
        }

        byName[name] = entry with { Overloads = overloads, Flags = flags };
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
    List<string>? Flags);
internal sealed record Cod4Overload(Cod4CalledOn? CalledOn, List<Cod4Parameter> Parameters);
internal sealed record Cod4CalledOn(string Name, string? Description);
internal sealed record Cod4Parameter(string Name, string? Description, bool Mandatory, Cod4Type? Type);
internal sealed record Cod4Type(string DataType);
