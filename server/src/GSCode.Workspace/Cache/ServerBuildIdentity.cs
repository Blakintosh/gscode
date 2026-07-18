using System.Reflection;
using System.Security.Cryptography;
using System.Text;

namespace GSCode.Workspace.Cache;

/// <summary>
/// A fingerprint of the running server that changes whenever analysis behavior might
/// have: the module version IDs (MVIDs) of the engine assemblies plus SHA-256 of the
/// bundled data files. A mismatch against the cached identity invalidates the whole
/// cache — this catches rebuilds the hand-bumped version numbers would miss.
/// </summary>
public static class ServerBuildIdentity
{
    /// <summary>Computes the identity from the given data-file paths (missing files are skipped).</summary>
    public static string Compute(IEnumerable<string> dataFilePaths)
    {
        StringBuilder material = new();

        // Assembly MVIDs change on every recompilation of that assembly.
        foreach ( Assembly assembly in EngineAssemblies() )
        {
            material.Append(assembly.GetName().Name);
            material.Append(':');
            material.Append(assembly.ManifestModule.ModuleVersionId.ToString("N"));
            material.Append(';');
        }

        // Data-file contents change the analysis surface without any code change.
        foreach ( string path in dataFilePaths.OrderBy(static path => path, StringComparer.Ordinal) )
        {
            if ( !File.Exists(path) )
            {
                continue;
            }

            material.Append(Path.GetFileName(path));
            material.Append('=');
            material.Append(HashFile(path));
            material.Append(';');
        }

        byte[] digest = SHA256.HashData(Encoding.UTF8.GetBytes(material.ToString()));
        return Convert.ToHexString(digest);
    }

    private static IEnumerable<Assembly> EngineAssemblies()
    {
        yield return typeof(Core.GameProfile).Assembly;
        yield return typeof(Parser.ParseResult).Assembly;
        yield return typeof(Database.ScriptDatabase).Assembly;
    }

    private static string HashFile(string path)
    {
        using FileStream stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }
}
