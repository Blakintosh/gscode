using System.Text.RegularExpressions;
using Xunit;
using Xunit.Abstractions;

namespace GSCode.Server.Tests.Configuration;

/// <summary>
/// Every setting the extension declares must actually be sent to the server.
///
/// `client/src/settings.ts` builds the payload from an explicit list, so a setting declared in
/// `package.json` but missing there never crosses the wire: the server falls back to its own
/// default and the user's choice is silently ignored. Nothing fails, nothing is logged, and the
/// setting simply does not work — which is exactly what happened to four settings at once.
///
/// A cross-file check rather than a unit test, because the bug lives in the gap between two files
/// that no compiler relates to each other.
/// </summary>
public class SettingsReachTheServerTests
{
    private readonly ITestOutputHelper _output;

    public SettingsReachTheServerTests(ITestOutputHelper output)
    {
        _output = output;
    }

    /// <summary>Walks up from the test assembly to the repository root.</summary>
    private static DirectoryInfo? FindClientDirectory()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);

        while ( directory is not null )
        {
            string candidate = Path.Combine(directory.FullName, "client", "package.json");
            if ( File.Exists(candidate) )
            {
                return new DirectoryInfo(Path.Combine(directory.FullName, "client"));
            }

            directory = directory.Parent;
        }

        return null;
    }

    [Fact]
    public void EveryDeclaredSettingIsSentToTheServer()
    {
        DirectoryInfo? client = FindClientDirectory();

        // This is the one guard against a setting that silently does nothing, so a missing
        // client/ must fail loudly rather than pass vacuously — a quiet return here used to read
        // as "every setting checked out" when nothing was checked at all, in whatever environment
        // could not find the directory.
        Assert.True(
            client is not null,
            $"Could not find client/package.json walking up from {AppContext.BaseDirectory}. "
            + "This test asserts nothing when it cannot find the manifest, so a green run here "
            + "must mean the manifest was actually read.");

        string manifest = File.ReadAllText(Path.Combine(client!.FullName, "package.json"));
        string payload = File.ReadAllText(Path.Combine(client.FullName, "src", "settings.ts"));

        // Every "gscode.<name>" key inside contributes.configuration.
        HashSet<string> declared = [.. Regex
            .Matches(manifest, @"""gscode\.(?<name>[A-Za-z0-9_.]+)""\s*:\s*\{")
            .Select(match => match.Groups["name"].Value)];

        Assert.NotEmpty(declared);

        // `<id>.trace.server` is the LSP client's own setting, read by vscode-languageclient to
        // decide how much protocol traffic to echo. The server never sees it and must not.
        declared.Remove("trace.server");

        List<string> missing = [.. declared
            .Where(name => !payload.Contains($"\"{name}\"", StringComparison.Ordinal)
                && !Regex.IsMatch(payload, $@"\b{Regex.Escape(name)}\s*:"))
            .OrderBy(name => name, StringComparer.Ordinal)];

        _output.WriteLine($"{declared.Count} settings declared; {missing.Count} not forwarded.");

        Assert.Empty(missing);
    }
}
