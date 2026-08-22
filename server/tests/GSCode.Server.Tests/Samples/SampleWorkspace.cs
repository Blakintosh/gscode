using GSCode.Core;
using GSCode.Core.Diagnostics;
using GSCode.Core.Symbols;
using GSCode.Core.Text;
using GSCode.Parser;
using GSCode.Parser.Preprocessing;
using GSCode.Workspace.Analysis;
using GSCode.Workspace.Api;
using GSCode.Workspace.Database;
using GSCode.Workspace.Indexing;
using GSCode.Workspace.Resolution;

namespace GSCode.Server.Tests.Samples;

/// <summary>One sample script and everything the editor would say about it.</summary>
internal sealed record SampleFile(string Path, string RelativePath, string Text, IReadOnlyList<Diagnostic> Diagnostics);

/// <summary>
/// Indexes one game's sample folder as a workspace and runs the WHOLE diagnostic pipeline over every
/// file in it — the same call the corpus sweep makes, so a sample is judged by the pipeline the
/// editor runs rather than by a reduced one.
///
/// The samples are a real directory on disk, indexed through the real <see cref="WorkspaceIndexer"/>
/// over a <see cref="PhysicalFileSystem"/>, because half of what they demonstrate is cross-file:
/// <c>#using</c>/<c>#include</c> resolution, <c>#insert</c>, and the lints that can only fire once a
/// second file exists. A <c>FakeFileSystem</c> would serve the parse diagnostics and quietly report
/// nothing for the rest.
///
/// Each game gets its own raw root — <c>Samples/bo3</c>, <c>Samples/cod4</c> — so a path is
/// resolved against that game's tree alone and the folder layout can follow the game's own
/// conventions (<c>scripts\shared</c> on BO3, <c>maps\mp</c> on the Infinity Ward line).
/// </summary>
internal static class SampleWorkspace
{
    /// <summary>Where the samples are copied to beside the test assembly. See the csproj.</summary>
    public static string Root => Path.Combine(AppContext.BaseDirectory, "Samples");

    private static string ApiDirectory => Path.Combine(AppContext.BaseDirectory, "Api");

    /// <summary>The sample raw root for a game, or null when that game has no sample folder yet.</summary>
    public static string? RootFor(GameProfile profile)
    {
        string root = Path.Combine(Root, profile.ShortName);
        return Directory.Exists(root) ? root : null;
    }

    /// <summary>Every supported game that has samples committed.</summary>
    public static IReadOnlyList<GameProfile> Games()
    {
        List<GameProfile> games = [];
        foreach ( GameProfile profile in GameProfile.All )
        {
            if ( profile.Supported && RootFor(profile) is not null )
            {
                games.Add(profile);
            }
        }

        return games;
    }

    /// <summary>
    /// Indexes <paramref name="profile"/>'s samples and analyses every one of them.
    ///
    /// <c>GameProfile.Select</c> is called first because several lints and the parser's directive
    /// handling fall back to <c>Active</c> when nothing hands them a profile — <c>WorkspaceLints</c>
    /// takes no profile at all — so passing the profile to the indexer alone would analyse a file
    /// under one dialect and lint it under another.
    ///
    /// And it is put BACK afterwards. <c>Active</c> is process-global and every other class in this
    /// assembly reads it without saying so, expecting the BO3 default; leaving it on CoD4 failed 132
    /// of them, in the formatter and the handlers, for a reason that looked like the thing under
    /// test. Restoring it is what keeps this class's business its own — the collection membership
    /// only orders the classes that join, and a reader joins nothing.
    /// </summary>
    public static async Task<IReadOnlyList<SampleFile>> AnalyzeAsync(GameProfile profile)
    {
        GameProfile previous = GameProfile.Active;
        try
        {
            return await AnalyzeUnderProfileAsync(profile);
        }
        finally
        {
            GameProfile.Select(previous.ShortName);
        }
    }

    private static async Task<IReadOnlyList<SampleFile>> AnalyzeUnderProfileAsync(GameProfile profile)
    {
        string root = RootFor(profile) ?? throw new DirectoryNotFoundException(
            $"No sample folder for {profile.ShortName}; expected {Path.Combine(Root, profile.ShortName)}.");

        GameProfile.Select(profile.ShortName);

        PhysicalFileSystem fileSystem = new();
        RootConfig config = RootConfig.Create(
            rawEnabled: true, rawPath: root, modsPath: null, workspaceFolders: [], fileSystem: fileSystem);
        PathResolver resolver = new(config, fileSystem);

        NameTable names = new();
        ScriptDatabase database = new();

        WorkspaceIndexer indexer = new(database, () => resolver, fileSystem, names, profile: profile);
        await indexer.IndexAsync(IndexingMode.Full, NullIndexProgressListener.Instance, CancellationToken.None);

        BuiltinApiSet builtins = BuiltinApiSet.Load(ApiDirectory);
        ObjectFields objectFields = ObjectFields.Load(ApiDirectory);

        // A cache per call rather than a shared static: two games' headers are different files that
        // can share a relative path, and a sample run is small enough that re-lexing costs nothing.
        InsertCache inserts = new();

        List<SampleFile> analysed = [];

        foreach ( string path in Scripts(profile, root) )
        {
            ScriptLanguage language = profile.LanguageFromPath(path);
            string text = File.ReadAllText(path);

            ResolverInsertProvider provider = new(resolver, resolver.GetContext(path), fileSystem, inserts);
            ParseResult result = ScriptAnalysis.Analyze(
                path, language, SourceText.From(text), provider, names, profile, inserts);

            List<Diagnostic> diagnostics =
            [
                .. WorkspaceLints.Analyze(result, language, path, database, resolver, builtins, objectFields),
            ];

            analysed.Add(new SampleFile(
                path,
                Path.GetRelativePath(root, path).Replace('\\', '/'),
                text,
                diagnostics));
        }

        return analysed;
    }

    /// <summary>
    /// Every sample under the root, in the game's own extensions and a stable order. Filtering by
    /// <c>ScriptExtensions</c> means a <c>.csc</c> left in a game without client scripts is simply
    /// not analysed, which the extension-coverage test then reports as the mistake it is.
    /// </summary>
    private static IReadOnlyList<string> Scripts(GameProfile profile, string root)
    {
        List<string> files = [];

        foreach ( string path in Directory.EnumerateFiles(root, "*.*", SearchOption.AllDirectories) )
        {
            string extension = Path.GetExtension(path);
            foreach ( string candidate in profile.ScriptExtensions )
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
}
