using System.Collections.Immutable;
using System.Text.Json;
using System.Text.RegularExpressions;
using GSCode.Core;
using GSCode.Core.Symbols;
using GSCode.Workspace.Resolution;

namespace GSCode.Server.Tests.Corpus;

/// <summary>
/// One generated scale workspace: the real game's raw folder as raw, plus a generated workspace
/// folder holding copies of its scripts, so the index sees <see cref="TotalFiles"/> files in all.
/// <see cref="Shape"/> names how the copies were made — <c>mixed</c>, or the one file duplicated —
/// and <see cref="Sources"/> are the scripts duplicated, empty for the mixed shape.
/// </summary>
internal sealed record ScaleCorpus(
    GameProfile Profile, string RawRoot, string WorkspaceRoot, int TotalFiles, int CopiedFiles, double ComplexShare,
    string Shape, ImmutableArray<string> Sources, string CodeWorkspace);

/// <summary>
/// Builds workspaces far larger than any real game ships, to find what breaks between the ~3,000
/// files the biggest stock corpus has and the 50,000 a large mod workspace could reach.
///
/// Every file is a real shipped script, so the per-file shape is real; what is synthetic is only the
/// COUNT. The copies live in one workspace folder, one subfolder per copy
/// (<c>c0001\scripts\shared\util_shared.gsc</c>), so every copy has its own relative path and nothing
/// shadows anything. Their <c>#using</c>/<c>#include</c>/<c>#insert</c> paths are left as written, so
/// they resolve to the stock raw tree — the same shape a mod's scripts have. On a namespace dialect
/// each copy's <c>#namespace</c> gets the copy's suffix, or fifty copies of <c>util</c> would all
/// declare into one namespace, which no real workspace does.
///
/// "Mostly complex": a copy always takes every file at or above the corpus's median size and only a
/// third of the smaller ones, so roughly three quarters of the copied files are the larger half.
///
/// Off unless <c>GSCODE_SCALE_SIZES</c> names the totals to build (for example
/// <c>10000,25000,50000</c>), and only for games whose <c>GSCODE_CORPUS_&lt;GAME&gt;</c> is present.
/// <c>GSCODE_SCALE_GAMES</c> picks the games (default <c>bo3</c>) and <c>GSCODE_SCALE_ROOT</c> where
/// the copies go (default the system temp folder). A finished workspace is marked and reused, since
/// writing 50,000 files is minutes the measurement should not pay twice.
///
/// <c>GSCODE_SCALE_SOURCE</c> switches to a second shape: a few stock scripts duplicated until the
/// total is reached, rather than the whole tree. <c>heaviest</c> picks the largest GSC/CSC by bytes
/// (bo3 <c>scripts\zm\_zm.gsc</c>: ~2,950 calls, 75 imports), <c>heaviest:N</c> the N largest
/// round-robin, or name a raw-relative path. Where the
/// mixed shape spreads references across the tree, this one piles them onto the keys a single
/// file calls, so every reference bucket and every hot stock function holds one entry per copy.
/// <c>GSCODE_SCALE_NAMESPACE=same</c> also leaves each copy's <c>#namespace</c> as written — every
/// copy declaring into one namespace, which no real mod does and PERF.md never measured — instead
/// of suffixing it per copy. Each shape gets its own folder, so neither evicts the other.
///
/// Every generated workspace gets a <c>.code-workspace</c> beside its folder, set up for the game,
/// the raw path and full indexing, so the same files can be opened in the editor.
/// </summary>
internal static class ScaleCorpusFixture
{
    /// <summary>Bumped whenever the generation rule changes, so a stale workspace is rebuilt.</summary>
    private const string GeneratorVersion = "1";

    private const string MarkerFileName = ".gscode-scale-complete";

    private static readonly Regex s_namespaceDirective = new(
        @"^(\s*#namespace\s+)(\w+)(\s*;)", RegexOptions.Multiline | RegexOptions.CultureInvariant);

    private static readonly Regex s_classDeclaration = new(
        @"^\s*class\s+(\w+)", RegexOptions.Multiline | RegexOptions.CultureInvariant);

    /// <summary>
    /// Every class a source declares, as one whole-word pattern, or null when it declares none. A BO3
    /// class name is global — it has no namespace to suffix — so without renaming each copy's classes
    /// too, two thousand copies of <c>scene_shared</c> all declare <c>cScene</c>, and every class lookup
    /// walks every one of them. No real workspace does that; <c>GSCODE_SCALE_CLASSES=same</c> keeps it
    /// for measuring exactly that.
    /// </summary>
    private static Regex? ClassNamesIn(string text)
    {
        List<string> names = [];
        foreach ( Match match in s_classDeclaration.Matches(text) )
        {
            names.Add(Regex.Escape(match.Groups[1].Value));
        }

        return names.Count == 0
            ? null
            : new Regex(@"\b(" + string.Join('|', names) + @")\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    public static IReadOnlyList<int> Sizes()
    {
        string? configured = Environment.GetEnvironmentVariable("GSCODE_SCALE_SIZES");
        List<int> sizes = [];
        if ( string.IsNullOrWhiteSpace(configured) )
        {
            return sizes;
        }

        foreach ( string part in configured.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) )
        {
            if ( int.TryParse(part, out int size) && size > 0 )
            {
                sizes.Add(size);
            }
        }

        sizes.Sort();
        return sizes;
    }

    public static IReadOnlyList<GameProfile> Games()
    {
        string configured = Environment.GetEnvironmentVariable("GSCODE_SCALE_GAMES") is string value && value.Length > 0
            ? value
            : "bo3";

        List<GameProfile> games = [];
        foreach ( string part in configured.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) )
        {
            GameProfile? profile = GameProfile.All.FirstOrDefault(
                candidate => string.Equals(candidate.ShortName, part, StringComparison.OrdinalIgnoreCase));
            if ( profile is not null && RawRootFor(profile) is not null )
            {
                games.Add(profile);
            }
        }

        return games;
    }

    public static string? RawRootFor(GameProfile profile)
    {
        if ( profile.ShortName == "bo3" )
        {
            return CorpusFixture.RawRoot;
        }

        return GameCorpusFixture.For(profile)?.RawRoot;
    }

    /// <summary>The generated workspace for one game and total, built on first use.</summary>
    public static ScaleCorpus Ensure(GameProfile profile, int totalFiles)
    {
        string rawRoot = RawRootFor(profile)
            ?? throw new InvalidOperationException($"No corpus configured for {profile.ShortName}.");

        List<string> stock = StockScripts(profile, rawRoot);
        int copiesWanted = Math.Max(0, totalFiles - stock.Count);

        string scaleRoot = Environment.GetEnvironmentVariable("GSCODE_SCALE_ROOT") is string configured && configured.Length > 0
            ? configured
            : Path.Combine(Path.GetTempPath(), "gscode-scale");

        List<string> sources = Sources(profile, rawRoot, stock);
        if ( sources.Count > 0 )
        {
            return EnsureSources(profile, rawRoot, scaleRoot, stock.Count, sources, copiesWanted);
        }

        string workspaceRoot = Path.Combine(scaleRoot, $"{profile.ShortName}-{totalFiles}");
        string marker = Path.Combine(workspaceRoot, MarkerFileName);

        Dictionary<string, long> lengths = new(StringComparer.OrdinalIgnoreCase);
        foreach ( string path in stock )
        {
            lengths[path] = new FileInfo(path).Length;
        }

        List<long> sizes = [.. lengths.Values.Order()];
        long median = sizes.Count == 0 ? 0 : sizes[sizes.Count / 2];

        List<(string Source, string Target)> plan = PlanCopies(stock, lengths, rawRoot, workspaceRoot, median, copiesWanted);
        int complex = plan.Count(item => lengths[item.Source] >= median);
        double complexShare = plan.Count == 0 ? 0 : (double)complex / plan.Count;

        string codeWorkspace = WriteCodeWorkspace(profile, rawRoot, workspaceRoot);
        ScaleCorpus corpus = new(
            profile, rawRoot, workspaceRoot, stock.Count + plan.Count, plan.Count, complexShare, "mixed", [], codeWorkspace);

        string expectedMarker = $"{GeneratorVersion}|{plan.Count}";
        if ( File.Exists(marker) && File.ReadAllText(marker) == expectedMarker )
        {
            return corpus;
        }

        if ( Directory.Exists(workspaceRoot) )
        {
            Directory.Delete(workspaceRoot, recursive: true);
        }

        Directory.CreateDirectory(workspaceRoot);

        Parallel.ForEach(plan, item =>
        {
            Directory.CreateDirectory(Path.GetDirectoryName(item.Target)!);
            string text = File.ReadAllText(item.Source);

            if ( profile.ShortName == "bo3" )
            {
                string suffix = "_" + CopyNameOf(item.Target, workspaceRoot);
                text = s_namespaceDirective.Replace(text, match => match.Groups[1].Value + match.Groups[2].Value + suffix + match.Groups[3].Value);
            }

            File.WriteAllText(item.Target, text);
        });

        File.WriteAllText(marker, expectedMarker);
        return corpus;
    }

    /// <summary>
    /// The stock scripts <c>GSCODE_SCALE_SOURCE</c> names, or none for the mixed shape.
    /// <c>heaviest</c> is the largest GSC/CSC by bytes — the one with the most calls and imports to
    /// pile up, not the slowest to analyse, which is preprocessing-bound and stresses a different
    /// thing. <c>heaviest:N</c> is the N largest, so one run covers reference-heavy files and the
    /// preprocessor-bound ones among them (bo3's top 25 average 98 KB and ~1,500 calls, against
    /// 12 KB across the corpus). Anything else is one raw-relative path.
    /// </summary>
    private static List<string> Sources(GameProfile profile, string rawRoot, List<string> stock)
    {
        string? configured = Environment.GetEnvironmentVariable("GSCODE_SCALE_SOURCE");
        if ( string.IsNullOrWhiteSpace(configured) )
        {
            return [];
        }

        configured = configured.Trim();
        int count = 0;
        if ( configured.Equals("heaviest", StringComparison.OrdinalIgnoreCase) )
        {
            count = 1;
        }
        else if ( configured.StartsWith("heaviest:", StringComparison.OrdinalIgnoreCase)
            && int.TryParse(configured["heaviest:".Length..], out int parsed) && parsed > 0 )
        {
            count = parsed;
        }

        if ( count == 0 )
        {
            string named = Path.GetFullPath(Path.Combine(rawRoot, configured));
            if ( !File.Exists(named) )
            {
                throw new InvalidOperationException($"GSCODE_SCALE_SOURCE names {named}, which does not exist.");
            }

            return [named];
        }

        List<(long Length, string Path)> scripts = [];
        foreach ( string path in stock )
        {
            if ( profile.LanguageFromPath(path) is ScriptLanguage.Gsc or ScriptLanguage.Csc )
            {
                scripts.Add((new FileInfo(path).Length, path));
            }
        }

        // Largest first, the path breaking a tie, so the same N always names the same files.
        scripts.Sort(static (left, right) =>
        {
            int byLength = right.Length.CompareTo(left.Length);
            return byLength != 0 ? byLength : string.CompareOrdinal(left.Path, right.Path);
        });

        List<string> sources = [];
        for ( int index = 0; index < count && index < scripts.Count; index++ )
        {
            sources.Add(scripts[index].Path);
        }

        return sources;
    }

    /// <summary>
    /// <paramref name="copies"/> copies of the source scripts, handed out round-robin, a thousand to
    /// a folder so the editor's explorer stays usable: <c>b00\scripts\zm\_zm_00001.gsc</c>. The
    /// raw-relative folder is kept so a copy sits where a mod's override of the file would.
    ///
    /// The copy number is ONE counter across every source, and it is what suffixes both the file
    /// name and the namespace. Numbered per source, the two <c>_globallogic</c> scripts — one under
    /// <c>mp\gametypes</c>, one under <c>zm\gametypes</c> — would hand two copies the same suffixed
    /// namespace, which is the one-namespace shape this is not.
    /// </summary>
    private static ScaleCorpus EnsureSources(
        GameProfile profile, string rawRoot, string scaleRoot, int stockCount, List<string> sources, int copies)
    {
        bool sameNamespace = string.Equals(
            Environment.GetEnvironmentVariable("GSCODE_SCALE_NAMESPACE"), "same", StringComparison.OrdinalIgnoreCase);
        bool sameClasses = sameNamespace || string.Equals(
            Environment.GetEnvironmentVariable("GSCODE_SCALE_CLASSES"), "same", StringComparison.OrdinalIgnoreCase);
        string firstRelative = Path.GetRelativePath(rawRoot, sources[0]);

        string folderName = sources.Count == 1
            ? $"{profile.ShortName}-{stockCount + copies}-{Path.GetFileNameWithoutExtension(sources[0]).TrimStart('_')}"
            : $"{profile.ShortName}-{stockCount + copies}-top{sources.Count}";
        if ( sameNamespace )
        {
            folderName += "-samens";
        }
        else if ( sameClasses && sources.Count > 1 )
        {
            folderName += "-sameclasses";
        }

        string workspaceRoot = Path.Combine(scaleRoot, folderName);
        string marker = Path.Combine(workspaceRoot, MarkerFileName);
        string shape = (sources.Count == 1 ? firstRelative : $"the {sources.Count} largest scripts")
            + $" x{copies:N0}" + (sameNamespace ? ", one namespace" : sameClasses && sources.Count > 1 ? ", classes not renamed" : "");

        string codeWorkspace = WriteCodeWorkspace(profile, rawRoot, workspaceRoot);
        ScaleCorpus corpus = new(
            profile, rawRoot, workspaceRoot, stockCount + copies, copies, 1.0, shape, [.. sources], codeWorkspace);

        string namespaces = sameNamespace ? "same" : "suffix";
        string expectedMarker = sources.Count == 1
            ? $"{GeneratorVersion}|single|{firstRelative}|{namespaces}|{copies}"
            : $"{GeneratorVersion}|top|{string.Join(';', sources.Select(source => Path.GetRelativePath(rawRoot, source)))}|{namespaces}|"
                + $"{(sameClasses ? "same" : "renamed")} classes|{copies}";
        if ( File.Exists(marker) && File.ReadAllText(marker) == expectedMarker )
        {
            return corpus;
        }

        if ( Directory.Exists(workspaceRoot) )
        {
            Directory.Delete(workspaceRoot, recursive: true);
        }

        Directory.CreateDirectory(workspaceRoot);

        List<string> texts = [];
        List<Regex?> classNames = [];
        foreach ( string source in sources )
        {
            string text = File.ReadAllText(source);
            texts.Add(text);
            classNames.Add(sameClasses ? null : ClassNamesIn(text));
        }

        Parallel.For(1, copies + 1, copy =>
        {
            int which = (copy - 1) % sources.Count;
            string source = sources[which];
            string relativeFolder = Path.GetDirectoryName(Path.GetRelativePath(rawRoot, source)) ?? "";
            string copyName = $"{Path.GetFileNameWithoutExtension(source)}_{copy:D5}";
            string folder = Path.Combine(workspaceRoot, $"b{(copy - 1) / 1000:D2}", relativeFolder);
            Directory.CreateDirectory(folder);

            string copyText = texts[which];
            if ( profile.ShortName == "bo3" && !sameNamespace )
            {
                string suffix = $"_{copy:D5}";
                copyText = s_namespaceDirective.Replace(
                    copyText, match => match.Groups[1].Value + match.Groups[2].Value + suffix + match.Groups[3].Value);

                if ( classNames[which] is Regex classes )
                {
                    copyText = classes.Replace(copyText, match => match.Value + suffix);
                }
            }

            File.WriteAllText(Path.Combine(folder, copyName + Path.GetExtension(source)), copyText);
        });

        File.WriteAllText(marker, expectedMarker);
        return corpus;
    }

    /// <summary>
    /// A <c>.code-workspace</c> beside the generated folder, so the scale workspace opens in the editor
    /// exactly as the suite indexes it: the game, the stock raw tree as raw, full indexing (the shape
    /// that lints every closed file), the cache, workspace-scope diagnostics and CodeLens on.
    /// Rewritten on every run since it is tiny and names this machine's raw path.
    /// </summary>
    private static string WriteCodeWorkspace(GameProfile profile, string rawRoot, string workspaceRoot)
    {
        string path = workspaceRoot + ".code-workspace";
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        Dictionary<string, object> settings = new()
        {
            ["gscode.game"] = profile.ShortName,
            ["gscode.raw.enabled"] = true,
            ["gscode.rawPath"] = rawRoot,
            ["gscode.workspaceIndexingMode"] = "full",
            ["gscode.enableWorkspaceCache"] = true,
            ["gscode.diagnostics.scope"] = "workspace",
            ["gscode.codeLens.enabled"] = true,
        };

        Dictionary<string, object> document = new()
        {
            ["folders"] = new[] { new Dictionary<string, string> { ["path"] = Path.GetFileName(workspaceRoot) } },
            ["settings"] = settings,
        };

        string json = JsonSerializer.Serialize(document, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(path, json.ReplaceLineEndings("\n") + "\n");
        return path;
    }

    /// <summary>A resolver with the stock tree as raw and the generated copies as one workspace folder.</summary>
    public static PathResolver Resolver(ScaleCorpus corpus)
    {
        PhysicalFileSystem fileSystem = new();
        RootConfig config = RootConfig.Create(
            rawEnabled: true, rawPath: corpus.RawRoot, modsPath: null,
            workspaceFolders: [corpus.WorkspaceRoot], fileSystem: fileSystem, profile: corpus.Profile);

        return new PathResolver(config, fileSystem);
    }

    /// <summary>The stock scripts under the raw root, ordered so a sample taken from it reproduces.</summary>
    public static List<string> StockScripts(ScaleCorpus corpus)
    {
        return StockScripts(corpus.Profile, corpus.RawRoot);
    }

    /// <summary>Every generated copy, ordered so a sample taken from it reproduces.</summary>
    public static List<string> CopiedScripts(ScaleCorpus corpus)
    {
        List<string> files = [];
        foreach ( string path in Directory.EnumerateFiles(corpus.WorkspaceRoot, "*.*", SearchOption.AllDirectories) )
        {
            if ( IsScript(corpus.Profile, path) )
            {
                files.Add(path);
            }
        }

        files.Sort(StringComparer.OrdinalIgnoreCase);
        return files;
    }

    private static List<(string Source, string Target)> PlanCopies(
        List<string> stock, Dictionary<string, long> lengths, string rawRoot, string workspaceRoot, long median,
        int copiesWanted)
    {
        List<(string Source, string Target)> plan = [];
        if ( stock.Count == 0 )
        {
            return plan;
        }

        int copy = 0;
        while ( plan.Count < copiesWanted )
        {
            copy++;
            string copyRoot = Path.Combine(workspaceRoot, $"c{copy:D4}");

            for ( int index = 0; index < stock.Count && plan.Count < copiesWanted; index++ )
            {
                string source = stock[index];
                bool large = lengths[source] >= median;

                // Deterministic, and rotating with the copy so every small file appears in some.
                bool takeSmall = (index + copy) % 3 == 0;
                if ( !large && !takeSmall )
                {
                    continue;
                }

                plan.Add((source, Path.Combine(copyRoot, Path.GetRelativePath(rawRoot, source))));
            }
        }

        return plan;
    }

    private static string CopyNameOf(string target, string workspaceRoot)
    {
        string relative = Path.GetRelativePath(workspaceRoot, target);
        int separator = relative.IndexOfAny(['\\', '/']);
        return separator < 0 ? relative : relative[..separator];
    }

    private static List<string> StockScripts(GameProfile profile, string rawRoot)
    {
        List<string> files = [];
        foreach ( string path in Directory.EnumerateFiles(rawRoot, "*.*", SearchOption.AllDirectories) )
        {
            if ( IsScript(profile, path) )
            {
                files.Add(path);
            }
        }

        files.Sort(StringComparer.OrdinalIgnoreCase);
        return files;
    }

    private static bool IsScript(GameProfile profile, string path)
    {
        string extension = Path.GetExtension(path);
        foreach ( string candidate in profile.ScriptExtensions )
        {
            if ( extension.Equals(candidate, StringComparison.OrdinalIgnoreCase) )
            {
                return true;
            }
        }

        return false;
    }
}
