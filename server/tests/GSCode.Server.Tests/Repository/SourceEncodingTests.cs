using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Xunit;

namespace GSCode.Server.Tests.Repository;

/// <summary>
/// The gate on how source files are ENCODED, as opposed to what they say.
///
/// The rule was already declared and never checked, which is the only reason there was anything to
/// find: <c>server/.editorconfig</c> is <c>root = true</c> and says <c>charset = utf-8</c> —
/// EditorConfig's name for UTF-8 with NO byte-order mark, <c>utf-8-bom</c> being the separate value
/// it does not use.
///
/// A declaration nothing enforces drifts silently, and this one drifted twice over. 57 <c>.cs</c>
/// files carried a BOM against that charset line, most written by an editor that adds one by
/// default. 19 more were added in a single afternoon by a script that read through Python's
/// <c>utf-8-sig</c>, which STRIPS a BOM on read and always WRITES one — so every file it rewrote
/// acquired one whether or not it had started with one, and the change reached committed blobs.
/// Nothing failed either time. The C# compiler accepts both forms, <c>.gitattributes</c> normalizes
/// line endings and says nothing about byte-order marks, and the only visible signal was a diff
/// touching the first line of files whose first line nobody had edited.
///
/// Line endings are the same bargain and were the same afternoon's second slip. `.gitattributes`
/// says LF "everywhere, in the repository AND in the working tree", and git enforces only the first
/// half: it normalizes on the way in, so blobs stayed LF while 43 files sat CRLF ON DISK from a
/// checkout that predated the policy. Git warned on every commit and the warning was read as
/// pre-existing noise, which it was — it was pre-existing and it was also right.
///
/// That asymmetry is why the working tree is what gets checked here. A clone is LF by construction
/// and can only go wrong afterwards: a stale checkout, or an editor writing CRLF into one file.
/// Neither shows up in a diff, because the clean filter erases both on the way back in.
///
/// This is the bargain the samples strike, applied to bytes instead of diagnostics: the artifact and
/// the thing that checks it are one, or the artifact stops being true and nobody notices.
/// </summary>
public class SourceEncodingTests
{
    /// <summary>
    /// Text formats the repository authors by hand. Deliberately a list of what IS checked rather
    /// than a list of binaries to skip: a new binary format nobody remembered to exclude would
    /// otherwise fail this suite on its magic bytes, and the fix would look like an exclusion rather
    /// than a decision.
    /// </summary>
    private static readonly string[] TextExtensions =
    [
        ".cs", ".csproj", ".props", ".slnx", ".editorconfig", ".gitattributes", ".gitignore",
        ".json", ".md", ".ts", ".mjs", ".js", ".svelte", ".css", ".html", ".yml", ".yaml",
        ".gsc", ".csc", ".gsh", ".txt", ".toml", ".py",
    ];

    [Fact]
    public void NoTrackedTextFileCarriesAByteOrderMark()
    {
        List<string> offenders = [];
        foreach ( string relative in TrackedTextFiles() )
        {
            string full = Path.Combine(RepositoryRoot, relative);
            if ( File.Exists(full) && StartsWithBom(full) )
            {
                offenders.Add(relative);
            }
        }

        Assert.True(
            offenders.Count == 0,
            $"server/.editorconfig declares `charset = utf-8`, which is UTF-8 with NO byte-order mark. "
            + $"{offenders.Count} tracked file(s) carry one:\n  "
            + string.Join("\n  ", offenders.Take(40)));
    }

    [Fact]
    public void NoTrackedTextFileHoldsACarriageReturn()
    {
        List<string> offenders = [];
        foreach ( string relative in TrackedTextFiles() )
        {
            string full = Path.Combine(RepositoryRoot, relative);
            if ( File.Exists(full) && File.ReadAllBytes(full).Contains((byte)'\r') )
            {
                offenders.Add(relative);
            }
        }

        // The fix is a checkout, not an edit: the blobs are already LF, so re-normalizing the tree
        // restores it. `git add --renormalize .` then `git checkout -- .` does it repository-wide.
        Assert.True(
            offenders.Count == 0,
            $".gitattributes declares LF in the repository AND in the working tree; git enforces only "
            + $"the first half, so these are on disk and invisible to `git diff`. {offenders.Count} "
            + $"tracked file(s) hold a carriage return:\n  " + string.Join("\n  ", offenders.Take(40)));
    }

    private static bool StartsWithBom(string path)
    {
        using FileStream stream = File.OpenRead(path);
        byte[] head = new byte[3];

        return stream.Read(head, 0, 3) == 3 && head[0] == 0xEF && head[1] == 0xBB && head[2] == 0xBF;
    }

    /// <summary>
    /// The TRACKED files, from git rather than from a directory walk.
    ///
    /// A walk was written first and was wrong in the way that matters: it read whatever happened to
    /// be on disk, so a BOM in someone's scratch file under <c>temp/</c> or in an editor's
    /// <c>.vscode/</c> folder failed the suite, and the rule this enforces has nothing to say about
    /// files the repository does not carry. "Tracked" is the whole claim, and git is the only thing
    /// that knows it.
    /// </summary>
    private static IEnumerable<string> TrackedTextFiles()
    {
        foreach ( string relative in RunGit("ls-files").Split('\n') )
        {
            string trimmed = relative.Trim();
            if ( trimmed.Length > 0
                && TextExtensions.Contains(Path.GetExtension(trimmed), StringComparer.OrdinalIgnoreCase) )
            {
                yield return trimmed.Replace('/', Path.DirectorySeparatorChar);
            }
        }
    }

    /// <remarks>
    /// Throws rather than skipping when git cannot answer. A suite that quietly passes because it
    /// found nothing to look at is the exact failure mode this test exists to close, and it would be
    /// indistinguishable from a clean repository in every report.
    /// </remarks>
    private static string RunGit(string arguments)
    {
        using Process? git = Process.Start(new ProcessStartInfo("git", arguments)
        {
            WorkingDirectory = RepositoryRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        });

        if ( git is null )
        {
            throw new InvalidOperationException("Could not start git, so no file list could be checked.");
        }

        string output = git.StandardOutput.ReadToEnd();
        git.WaitForExit();

        if ( git.ExitCode != 0 )
        {
            throw new InvalidOperationException($"`git {arguments}` failed: {git.StandardError.ReadToEnd()}");
        }

        return output;
    }

    /// <summary>
    /// The repository root, found by walking up from the test binary for the file that sits at it.
    /// Throws when there is none, for the reason <see cref="RunGit"/> does.
    /// </summary>
    private static string RepositoryRoot
    {
        get
        {
            DirectoryInfo? directory = new(AppContext.BaseDirectory);
            while ( directory is not null )
            {
                if ( File.Exists(Path.Combine(directory.FullName, ".gitattributes")) )
                {
                    return directory.FullName;
                }

                directory = directory.Parent;
            }

            throw new DirectoryNotFoundException(
                $"No .gitattributes above {AppContext.BaseDirectory}, so the encoding policy could not "
                + "be checked against anything. This test must not pass by finding no files.");
        }
    }
}
