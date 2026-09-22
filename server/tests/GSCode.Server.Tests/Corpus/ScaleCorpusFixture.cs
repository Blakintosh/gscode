using System.Text.RegularExpressions;
using GSCode.Core;
using GSCode.Workspace.Resolution;

namespace GSCode.Server.Tests.Corpus;

/// <summary>
/// One generated scale workspace: the real game's raw folder as raw, plus a generated workspace
/// folder holding copies of its scripts, so the index sees <see cref="TotalFiles"/> files in all.
/// </summary>
internal sealed record ScaleCorpus(
    GameProfile Profile, string RawRoot, string WorkspaceRoot, int TotalFiles, int CopiedFiles, double ComplexShare);

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
/// </summary>
internal static class ScaleCorpusFixture
{
    /// <summary>Bumped whenever the generation rule changes, so a stale workspace is rebuilt.</summary>
    private const string GeneratorVersion = "1";

    private const string MarkerFileName = ".gscode-scale-complete";

    private static readonly Regex NamespaceDirective = new(
        @"^(\s*#namespace\s+)(\w+)(\s*;)", RegexOptions.Multiline | RegexOptions.CultureInvariant);

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

        string expectedMarker = $"{GeneratorVersion}|{plan.Count}";
        if ( File.Exists(marker) && File.ReadAllText(marker) == expectedMarker )
        {
            return new ScaleCorpus(profile, rawRoot, workspaceRoot, stock.Count + plan.Count, plan.Count, complexShare);
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
                text = NamespaceDirective.Replace(text, match => match.Groups[1].Value + match.Groups[2].Value + suffix + match.Groups[3].Value);
            }

            File.WriteAllText(item.Target, text);
        });

        File.WriteAllText(marker, expectedMarker);
        return new ScaleCorpus(profile, rawRoot, workspaceRoot, stock.Count + plan.Count, plan.Count, complexShare);
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
