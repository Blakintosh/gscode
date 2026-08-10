using System.Collections.Immutable;
using System.IO.Enumeration;

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

    /// <summary>
    /// Recursively enumerates files under <paramref name="directory"/> whose extension is one of
    /// <paramref name="extensions"/> (each including the dot, compared case-insensitively).
    ///
    /// Separate from <see cref="EnumerateFiles"/> because the indexer wants several extensions at
    /// once and neither obvious spelling is good enough: one call per pattern walks the whole tree
    /// once per extension, while a single "*" walk hands back every file on disk to be filtered in
    /// managed code. Black Ops 1's raw folder holds 160,382 files of which 2,960 are scripts, so
    /// that second spelling would allocate 157,422 strings to throw them all away.
    /// </summary>
    IEnumerable<string> EnumerateFilesWithExtensions(string directory, ImmutableArray<string> extensions);
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

    /// <summary>
    /// One walk of the tree, with the extension test applied to the entry's name in place.
    ///
    /// <see cref="FileSystemEnumerable{TResult}"/> rather than <see cref="Directory.EnumerateFiles(string, string)"/>
    /// because its predicate sees a <see cref="FileSystemEntry"/> whose FileName is a span over the
    /// buffer the OS already filled. A file that is not a script is rejected without a string ever
    /// existing for it, so the 157,422 non-scripts in a Black Ops 1 install cost a comparison each
    /// and nothing else.
    /// </summary>
    public IEnumerable<string> EnumerateFilesWithExtensions(string directory, ImmutableArray<string> extensions)
    {
        EnumerationOptions options = new()
        {
            RecurseSubdirectories = true,

            // Matches Directory.EnumerateFiles, which skips what it cannot open rather than
            // throwing part-way through a walk.
            IgnoreInaccessible = true,
        };

        return new FileSystemEnumerable<string>(
            directory,
            static (ref FileSystemEntry entry) => entry.ToFullPath(),
            options)
        {
            ShouldIncludePredicate = (ref FileSystemEntry entry) =>
            {
                if ( entry.IsDirectory )
                {
                    return false;
                }

                ReadOnlySpan<char> name = entry.FileName;
                foreach ( string extension in extensions )
                {
                    if ( name.EndsWith(extension, StringComparison.OrdinalIgnoreCase) )
                    {
                        return true;
                    }
                }

                return false;
            },
        };
    }
}
