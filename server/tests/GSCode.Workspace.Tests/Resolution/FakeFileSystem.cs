using GSCode.Core.Paths;
using GSCode.Workspace.Resolution;

namespace GSCode.Workspace.Tests.Resolution;

/// <summary>An in-memory file tree for resolver tests: add files, directories are implied.</summary>
public sealed class FakeFileSystem : IFileSystem
{
    private readonly Dictionary<string, string> _files = new(StringComparer.Ordinal);

    public FakeFileSystem AddFile(string absolutePath, string content = "")
    {
        _files[PathUtil.NormalizeAbsolute(absolutePath)] = content;
        return this;
    }

    public bool FileExists(string absolutePath)
    {
        return _files.ContainsKey(PathUtil.NormalizeAbsolute(absolutePath));
    }

    public bool DirectoryExists(string absolutePath)
    {
        string directory = PathUtil.NormalizeAbsolute(absolutePath);
        foreach ( string file in _files.Keys )
        {
            if ( PathUtil.IsUnder(file, directory) )
            {
                return true;
            }
        }

        return false;
    }

    public string ReadAllText(string absolutePath)
    {
        return _files[PathUtil.NormalizeAbsolute(absolutePath)];
    }

    /// <summary>
    /// A fixed stamp: an in-memory tree has no clock, and a test that wants to model an edit calls
    /// the cache's Invalidate rather than pretending time passed.
    /// </summary>
    public DateTime GetLastWriteTimeUtc(string absolutePath)
    {
        return FileExists(absolutePath) ? DateTime.UnixEpoch : DateTime.MinValue;
    }

    public IEnumerable<string> EnumerateFiles(string directory, string searchPattern)
    {
        return EnumerateFilesWithExtensions(directory, [searchPattern.TrimStart('*')]);
    }

    public IEnumerable<string> EnumerateFilesWithExtensions(
        string directory, System.Collections.Immutable.ImmutableArray<string> extensions)
    {
        string normalizedDirectory = PathUtil.NormalizeAbsolute(directory);

        foreach ( string file in _files.Keys )
        {
            if ( !PathUtil.IsUnder(file, normalizedDirectory) )
            {
                continue;
            }

            foreach ( string extension in extensions )
            {
                // Ordinal, matching the real one's OrdinalIgnoreCase in effect: keys here are
                // already normalized to lower case by AddFile.
                if ( file.EndsWith(extension, StringComparison.Ordinal) )
                {
                    yield return file;
                    break;
                }
            }
        }
    }
}
