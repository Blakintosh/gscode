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

    public IEnumerable<string> EnumerateFiles(string directory, string searchPattern)
    {
        return Directory.EnumerateFiles(directory, searchPattern, SearchOption.AllDirectories);
    }
}
