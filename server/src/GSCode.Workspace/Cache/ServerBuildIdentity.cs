using System.Reflection;
using System.Security.Cryptography;
using System.Text;

namespace GSCode.Workspace.Cache;

/// <summary>
/// A fingerprint of the running server that changes whenever analysis behavior might
/// have: the active game, the module version IDs (MVIDs) of the engine assemblies, and
/// SHA-256 of the bundled data files. A mismatch against the cached identity invalidates
/// the whole cache — this catches rebuilds the hand-bumped version numbers would miss.
/// </summary>
public static class ServerBuildIdentity
{
    /// <summary>
    /// Computes the identity from the active game and the given data-file paths (missing files are
    /// skipped).
    /// </summary>
    /// <param name="game">
    /// The game the records were analysed under. Every cached record is dialect-specific: the same
    /// source text yields different keywords, a different import style and different builtins per
    /// game, so restoring one game's records into another's session is wrong in a way nothing
    /// downstream can detect — the records look entirely valid and simply describe another language.
    ///
    /// A game change did already invalidate before this was explicit, but only as a SIDE EFFECT of
    /// each game bundling differently-named data files. That is not a property worth resting on:
    /// MW2 ships no data at all, so its material was the assembly MVIDs alone, and a second
    /// data-less game added later would have shared an identity with it exactly.
    /// </param>
    public static string Compute(IEnumerable<string> dataFilePaths, string game)
    {
        StringBuilder material = new();

        material.Append("game=");
        material.Append(game);
        material.Append(';');

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
