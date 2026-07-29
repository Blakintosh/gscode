using GSCode.Core;
using GSCode.Core.Symbols;
using GSCode.Core.Text;
using GSCode.Parser;
using GSCode.Parser.Preprocessing;
using GSCode.Workspace.Resolution;

namespace GSCode.Server.Tests.Corpus;

/// <summary>
/// Locates the real BO3 script corpus under <c>%GSCODE_CORPUS_BO3%</c> and analyses files
/// from it exactly as the server would, inserts resolved and all.
///
/// The corpus is not committed and is absent on CI, so these tests no-op when it cannot be
/// found. That is deliberate: the value is the local signal against thousands of real scripts,
/// and a hard failure on machines without the mod tools would make the suite unrunnable for
/// anyone else. <see cref="Available"/> makes the no-op explicit at each call site rather than
/// hiding it, and every corpus test reports which branch it took.
/// </summary>
internal static class CorpusFixture
{
    /// <summary>Kept well under the full corpus so the whole-file gates stay minutes, not hours.</summary>
    public const int FormatterSampleSize = 250;

    public static string? RawRoot
    {
        get
        {
            // GSCODE_CORPUS_BO3, the same convention the other four games use. This used to name
            // the tools install and derive the raw folder beneath it, until the server stopped
            // discovering roots from an environment variable at all; the fixture followed, so there
            // is one way to point a corpus at a game rather than one for BO3 and another for
            // everyone else. It names the raw folder directly.
            string? root = Environment.GetEnvironmentVariable("GSCODE_CORPUS_BO3");
            return !string.IsNullOrWhiteSpace(root) && Directory.Exists(root) ? root : null;
        }
    }

    public static bool Available
    {
        get { return RawRoot is not null; }
    }

    /// <summary>Every script under the raw root, ordered so runs are reproducible.</summary>
    public static IReadOnlyList<string> Scripts()
    {
        string? root = RawRoot;
        if ( root is null )
        {
            return [];
        }

        List<string> files = [];
        foreach ( string path in Directory.EnumerateFiles(root, "*.*", SearchOption.AllDirectories) )
        {
            string extension = Path.GetExtension(path);
            if ( extension.Equals(".gsc", StringComparison.OrdinalIgnoreCase)
                || extension.Equals(".csc", StringComparison.OrdinalIgnoreCase)
                || extension.Equals(".gsh", StringComparison.OrdinalIgnoreCase) )
            {
                files.Add(path);
            }
        }

        files.Sort(StringComparer.OrdinalIgnoreCase);
        return files;
    }

    /// <summary>
    /// A resolver rooted at the real raw folder, so `#insert` targets actually resolve and the
    /// corpus exercises the preprocessor rather than a wall of insert-not-found diagnostics.
    /// </summary>
    public static PathResolver Resolver()
    {
        PhysicalFileSystem fileSystem = new();
        RootConfig config = RootConfig.Create(rawEnabled: true, rawPath: RawRoot, modsPath: null, workspaceFolders: [], fileSystem: fileSystem);

        return new PathResolver(config, fileSystem);
    }

    /// <summary>Analyses one corpus file the way the server does, with inserts resolved.</summary>
    public static ParseResult Analyze(string path, PathResolver resolver, NameTable names)
    {
        PhysicalFileSystem fileSystem = new();
        ResolverInsertProvider inserts = new(resolver, resolver.GetContext(path), fileSystem);

        return ScriptAnalysis.Analyze(
            path,
            ScriptAnalysis.LanguageFromPath(path),
            SourceText.From(File.ReadAllText(path)),
            inserts,
            names);
    }
}
