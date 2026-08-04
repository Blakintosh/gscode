using GSCode.Core;
using GSCode.Core.Symbols;
using GSCode.Core.Text;
using GSCode.Parser;
using GSCode.Parser.Preprocessing;
using GSCode.Workspace.Resolution;

namespace GSCode.Server.Tests.Corpus;

/// <summary>One game's real script corpus: the profile to analyse with, and the raw root to find.</summary>
internal sealed record GameCorpus(GameProfile Profile, string RawRoot);

/// <summary>
/// The per-game corpus, for the games beyond BO3. Where <see cref="CorpusFixture"/> covers BO3 via
/// <c>%GSCODE_CORPUS_BO3%</c>, each other supported game points at its own scripts through an
/// environment variable — <c>GSCODE_CORPUS_COD4</c>, <c>GSCODE_CORPUS_WAW</c>,
/// <c>GSCODE_CORPUS_MW2</c>, <c>GSCODE_CORPUS_BO1</c> — set to that game's raw script root.
///
/// Env vars rather than committed paths for two reasons: the corpora are large and unshippable, and
/// their locations are per-machine. As with the BO3 fixture, an absent corpus is a no-op rather than
/// a failure, so the suite stays runnable for anyone without the tools.
/// </summary>
internal static class GameCorpusFixture
{
    /// <summary>The environment variable naming a game's raw script root.</summary>
    public static string EnvironmentVariableFor(GameProfile profile)
    {
        return "GSCODE_CORPUS_" + profile.ShortName.ToUpperInvariant();
    }

    /// <summary>The corpus for one game, or null when its root is not configured or not present.</summary>
    public static GameCorpus? For(GameProfile profile)
    {
        string? root = Environment.GetEnvironmentVariable(EnvironmentVariableFor(profile));
        if ( string.IsNullOrWhiteSpace(root) || !Directory.Exists(root) )
        {
            return null;
        }

        return new GameCorpus(profile, root);
    }

    /// <summary>Every supported game that has a corpus configured on this machine.</summary>
    public static IReadOnlyList<GameCorpus> Available()
    {
        List<GameCorpus> corpora = [];
        foreach ( GameProfile profile in GameProfile.All )
        {
            if ( !profile.Supported || profile.ShortName == "bo3" )
            {
                // BO3 has its own fixture, rooted at the tools install.
                continue;
            }

            GameCorpus? corpus = For(profile);
            if ( corpus is not null )
            {
                corpora.Add(corpus);
            }
        }

        return corpora;
    }

    /// <summary>
    /// Every script under the corpus root, in the game's own extensions, ordered so runs reproduce.
    /// A game without client scripts never picks up a stray <c>.csc</c>, which is itself part of what
    /// the profile asserts.
    /// </summary>
    public static IReadOnlyList<string> Scripts(GameCorpus corpus)
    {
        List<string> files = [];
        foreach ( string path in Directory.EnumerateFiles(corpus.RawRoot, "*.*", SearchOption.AllDirectories) )
        {
            string extension = Path.GetExtension(path);
            foreach ( string candidate in corpus.Profile.ScriptExtensions )
            {
                if ( extension.Equals(candidate, StringComparison.OrdinalIgnoreCase) )
                {
                    files.Add(path);
                    break;
                }
            }
        }

        files.Sort(StringComparer.OrdinalIgnoreCase);
        return files;
    }

    /// <summary>A resolver rooted at this game's raw folder, so its includes actually resolve.</summary>
    public static PathResolver Resolver(GameCorpus corpus)
    {
        PhysicalFileSystem fileSystem = new();
        RootConfig config = RootConfig.Create(rawEnabled: true, rawPath: corpus.RawRoot, modsPath: null, workspaceFolders: [], fileSystem: fileSystem);

        return new PathResolver(config, fileSystem);
    }

    /// <summary>
    /// Shared lexed headers, as the server has. Without it every file re-lexes the headers it
    /// inserts, which is what the perf sweep is measuring.
    /// </summary>
    public static InsertCache Inserts { get; } = new();

    /// <summary>
    /// Analyses one file with THIS game's profile — the whole point of the per-game sweep.
    /// <paramref name="source"/> stands in for the file's contents when given, which is how a test
    /// asks what the editor would say about an EDIT to a shipped script rather than the script as
    /// it is. Everything else — the insert provider, the shared header cache, the profile — has to
    /// match the sweep, or the answer is about a different pipeline.
    /// </summary>
    public static ParseResult Analyze(
        GameCorpus corpus, string path, PathResolver resolver, NameTable names, string? source = null)
    {
        PhysicalFileSystem fileSystem = new();
        ResolverInsertProvider inserts = new(resolver, resolver.GetContext(path), fileSystem, Inserts);

        return ScriptAnalysis.Analyze(
            path,
            corpus.Profile.LanguageFromPath(path),
            SourceText.From(source ?? File.ReadAllText(path)),
            inserts,
            names,
            corpus.Profile,
            Inserts);
    }
}
