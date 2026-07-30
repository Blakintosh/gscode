namespace GSCode.Workspace.Resolution;

/// <summary>
/// The thin file-system seam: everything path resolution and indexing touches on disk
/// goes through here, so tests run on fake in-memory trees.
/// </summary>
public interface IFileSystem
{
    bool FileExists(string absolutePath);

    bool DirectoryExists(string absolutePath);

    string ReadAllText(string absolutePath);

    /// <summary>
    /// When the file was last written, or <see cref="DateTime.MinValue"/> when that cannot be read.
    /// Lets a cache tell a live entry from a stale one without re-reading the contents — which is
    /// the whole point of caching the file in the first place.
    /// </summary>
    DateTime GetLastWriteTimeUtc(string absolutePath);

    /// <summary>Recursively enumerates files under <paramref name="directory"/> matching the pattern (e.g. "*.gsc").</summary>
    IEnumerable<string> EnumerateFiles(string directory, string searchPattern);
}

/// <summary>The real file system, used everywhere outside tests.</summary>
public sealed class PhysicalFileSystem : IFileSystem
{
    public bool FileExists(string absolutePath)
    {
        return File.Exists(absolutePath);
    }

    public bool DirectoryExists(string absolutePath)
    {
        return Directory.Exists(absolutePath);
    }

    public string ReadAllText(string absolutePath)
    {
        return File.ReadAllText(absolutePath);
    }

    public DateTime GetLastWriteTimeUtc(string absolutePath)
    {
        try
        {
            return File.GetLastWriteTimeUtc(absolutePath);
        }
        catch ( IOException )
        {
            return DateTime.MinValue;
        }
        catch ( UnauthorizedAccessException )
        {
            return DateTime.MinValue;
        }
    }

    public IEnumerable<string> EnumerateFiles(string directory, string searchPattern)
    {
        return Directory.EnumerateFiles(directory, searchPattern, SearchOption.AllDirectories);
    }
}
