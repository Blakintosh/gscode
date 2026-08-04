using System.Runtime.CompilerServices;
using GSCode.Core;
using GSCode.Core.Symbols;
using GSCode.Parser;
using GSCode.Parser.Syntax;
using GSCode.Parser.Syntax.Ast;
using GSCode.Workspace.Resolution;
using Xunit;
using Xunit.Abstractions;

namespace GSCode.Server.Tests.Corpus;

/// <summary>
/// Generates the per-game stock-script lists that drive the raw-folder save warning
/// (<c>gscode.rawFileWarningMode = "stock"</c>), which needs to tell a file the game shipped from one
/// the user wrote. WaW and BO1 promised such a list through <c>BundledDataFileNames</c> and shipped
/// none, so the warning simply never fired for them.
///
/// TWO SOURCES, because the obvious one is incomplete. The extracted script tree is the bulk of it,
/// but a dump is only as complete as whoever made it — and the scripts themselves say so. A reference
/// to a file that is not in the tree is not a broken script; the game linked it, so the file shipped
/// and this particular extraction is missing it. Those paths are stock too, and mining them is free:
/// the same resolver failure already drives <c>gscode-5009</c>.
///
/// It is worth real coverage. BO1 recovers 165 files that way — WaW-era animscripts it inherited,
/// DLC map scripts (Silo, Golf Course, Moon), the frontend client scripts, model aliases — against
/// 1 for CoD4 and 1 for WaW, whose dumps are near-complete.
///
/// Being generous is the right error to make here. The list only ever decides whether to WARN before
/// overwriting something, so a path wrongly included costs one warning on a file the user did author,
/// while a path wrongly missing costs silence on a stock file being clobbered. A typo'd import in a
/// stock script therefore does no real harm — it names a file that does not exist, which nothing can
/// open anyway.
///
/// A generator rather than a check, so it is a <see cref="FactAttribute"/> that writes and reports
/// instead of asserting a count: the corpora are per-machine and whatever version is configured, so
/// a hard number would fail on someone else's copy. Re-run it when a corpus changes; it no-ops
/// entirely for anyone without the game files.
/// </summary>
[Trait("Category", "Corpus")]
[Collection(GameProfileCollection.Name)]
public class StockScriptListTests
{
    private readonly ITestOutputHelper _output;

    public StockScriptListTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public void GenerateStockScriptLists()
    {
        IReadOnlyList<GameCorpus> corpora = GameCorpusFixture.Available();
        if ( corpora.Count == 0 )
        {
            _output.WriteLine("SKIPPED: no per-game corpora configured (set GSCODE_CORPUS_<GAME>).");
            return;
        }

        string? apiDirectory = FindApiDirectory();
        if ( apiDirectory is null )
        {
            _output.WriteLine("SKIPPED: could not locate src/GSCode.Workspace/Api from the test binary.");
            return;
        }

        foreach ( GameCorpus corpus in corpora )
        {
            if ( corpus.Profile.StockScriptsFileName is null )
            {
                // No DataFilePrefix, so the profile ships no bundled data to put this beside.
                continue;
            }

            Generate(corpus, apiDirectory);
        }
    }

    private void Generate(GameCorpus corpus, string apiDirectory)
    {
        GameProfile profile = corpus.Profile;
        IReadOnlyList<string> scripts = GameCorpusFixture.Scripts(corpus);

        // Ordinal, not OrdinalIgnoreCase: the file stores one canonical spelling and StockScripts
        // lowercases on both write and read, so the set must compare the same way it is written.
        SortedSet<string> paths = new(StringComparer.Ordinal);
        foreach ( string path in scripts )
        {
            paths.Add(Canonical(Path.GetRelativePath(corpus.RawRoot, path)));
        }

        int extracted = paths.Count;

        PathResolver resolver = GameCorpusFixture.Resolver(corpus);
        NameTable names = new();
        SortedSet<string> fromImports = new(StringComparer.Ordinal);

        foreach ( string path in scripts )
        {
            ParseResult result = GameCorpusFixture.Analyze(corpus, path, resolver, names);
            ResolutionContext context = resolver.GetContext(path);

            // The extension follows the ASKING file's language, which is what the engine does: an
            // include from a .csc names a .csc. Getting this wrong would mine every client import as
            // a missing server script.
            string extension = profile.LanguageFromPath(path) == ScriptLanguage.Csc
                ? profile.ClientScriptExtension
                : profile.ServerScriptExtension;

            foreach ( string target in ReferencedScripts(result.Tree.Root) )
            {
                // Exactly the test gscode-5009 applies: does it resolve to a file on disk. A target
                // that does resolve is already in the set from the enumeration above.
                if ( resolver.Resolve(context, target + extension) is null )
                {
                    fromImports.Add(Canonical(target + extension));
                }
            }
        }

        foreach ( string path in fromImports )
        {
            paths.Add(path);
        }

        string file = Path.Combine(apiDirectory, profile.StockScriptsFileName!);
        Write(file, profile, paths, extracted, fromImports.Count);

        _output.WriteLine(
            $"{profile.ShortName}: {paths.Count} stock scripts "
            + $"({extracted} extracted + {fromImports.Count} named only by an unresolved import) -> {file}");
    }

    private static void Write(
        string file, GameProfile profile, SortedSet<string> paths, int extracted, int fromImports)
    {
        using StreamWriter writer = new(file, false, new System.Text.UTF8Encoding(false));
        writer.NewLine = "\n";

        writer.WriteLine($"# Stock script files shipped with the {profile.DisplayName} mod tools, relative to the raw root.");
        writer.WriteLine("# Used by the raw-folder save warning (gscode.rawFileWarningMode = \"stock\") to tell stock");
        writer.WriteLine("# scripts from user-authored ones.");
        writer.WriteLine("#");
        writer.WriteLine($"# Generated by StockScriptListTests from the extracted script tree ({extracted} files) plus");
        writer.WriteLine($"# {fromImports} more that only an import or a path-qualified call names, resolving to nothing on");
        writer.WriteLine("# disk. Those shipped too — the game linked them — and their absence is this extraction's,");
        writer.WriteLine("# not the game's.");

        foreach ( string path in paths )
        {
            writer.WriteLine(path);
        }
    }

    /// <summary>
    /// Every other script this one names, by whichever of the three routes the dialect offers — and all
    /// three are needed, because they are not evenly distributed.
    ///
    /// <c>#include</c> is the merge dialects' (WaW, BO1, CoD4) and <c>#using</c> the namespace ones'
    /// (BO3), so a run that handled only <see cref="UsingNode"/> mined nothing at all from the two
    /// games this exists for. The third is the one that actually carries the volume: a path-qualified
    /// call, <c>maps\mp\_utility::func()</c>, names a file without importing it, and it is where all
    /// 918 of BO1's <c>gscode-5009</c> reports come from.
    /// </summary>
    private static IEnumerable<string> ReferencedScripts(AstNode node)
    {
        switch ( node )
        {
            case IncludeNode include when include.Path.Length > 0:
                yield return include.Path;
                break;

            case UsingNode import when import.Path.Length > 0:
                yield return import.Path;
                break;

            case PathQualifiedNode call when call.Path.Length > 0:
                yield return call.Path;
                break;
        }

        // Imports sit at the root, but a path-qualified call is an expression buried in a function.
        foreach ( AstNode child in AstSearch.ChildrenOf(node) )
        {
            foreach ( string target in ReferencedScripts(child) )
            {
                yield return target;
            }
        }
    }

    /// <summary>The same canonical form <c>StockScripts</c> reads with: forward slashes, lowercase.</summary>
    private static string Canonical(string relativePath)
    {
        return relativePath.Replace('\\', '/').TrimStart('/').ToLowerInvariant();
    }

    /// <summary>
    /// The bundled-data folder in the SOURCE tree, not the copy beside the test binary — this writes an
    /// artifact that gets committed, and a copy in bin/ dies on the next clean.
    ///
    /// Anchored on this file's own compile-time path rather than on <c>AppContext.BaseDirectory</c>,
    /// which is not reliably inside the repo: building with <c>-p:BaseOutputPath</c> (the standard way
    /// round a running Extension Development Host holding the DLLs) puts the binary somewhere else
    /// entirely, and walking up from there finds nothing.
    /// </summary>
    private static string? FindApiDirectory([CallerFilePath] string sourceFile = "")
    {
        string start = sourceFile.Length > 0 && Path.GetDirectoryName(sourceFile) is string sourceDirectory
            ? sourceDirectory
            : AppContext.BaseDirectory;

        DirectoryInfo? directory = new(start);
        while ( directory is not null )
        {
            string candidate = Path.Combine(directory.FullName, "src", "GSCode.Workspace", "Api");
            if ( Directory.Exists(candidate) )
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        return null;
    }
}
