using System.Collections.Immutable;
using GSCode.Core;
using System.IO.Enumeration;
using System.Text;

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

    /// <summary>
    /// Recursively enumerates files under <paramref name="directory"/> whose extension is one of
    /// <paramref name="extensions"/> (each including the dot, compared case-insensitively).
    ///
    /// The one enumeration this seam offers, because neither obvious single-pattern spelling is
    /// good enough for what the indexer wants: one call per pattern walks the whole tree once per
    /// extension, while a single "*" walk hands back every file on disk to be filtered in managed
    /// code. Black Ops 1's raw folder holds 160,382 files of which 2,960 are scripts, so
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

    /// <summary>
    /// Reads a script, decoding the bytes in one pass instead of through a StreamReader.
    ///
    /// <see cref="File.ReadAllText(string)"/> pulls the file through a 4 KB decode buffer and grows
    /// a builder as it goes; a script is read whole or not at all, so its length is known before a
    /// character is decoded and one <c>GetString</c> can produce the final string directly. The
    /// cold index is the caller that cares — <c>index.read</c> was measured at 12-18% of thread-time
    /// on a run where every file was already in the OS cache, which is not the shape of I/O.
    ///
    /// The byte-order marks are recognised in the same order the framework does, longest first, so
    /// a UTF-32 little-endian mark is not mistaken for a UTF-16 one that happens to share its first
    /// two bytes. With no mark this decodes UTF-8, replacing invalid bytes rather than throwing —
    /// the same answer <see cref="File.ReadAllText(string)"/> gives, and the one that matters for a
    /// decompiler's output.
    /// </summary>
    public string ReadAllText(string absolutePath)
    {
        return DecodeText(File.ReadAllBytes(absolutePath));
    }

    private static readonly UTF32Encoding s_utf32BigEndian = new(bigEndian: true, byteOrderMark: true);

    // Every one of these is the same character, U+FEFF, encoded the way its name says. That is
    // why the UTF-32 little-endian mark opens with the entire UTF-16 little-endian one: U+FEFF
    // in UTF-32 little-endian is its UTF-16 little-endian form followed by two zero bytes.
    // Declared in the order DecodeText tests them.
    private static readonly byte[] s_utf8Bom = [0xEF, 0xBB, 0xBF];
    private static readonly byte[] s_utf32LittleEndianBom = [0xFF, 0xFE, 0x00, 0x00];
    private static readonly byte[] s_utf32BigEndianBom = [0x00, 0x00, 0xFE, 0xFF];
    private static readonly byte[] s_utf16LittleEndianBom = [0xFF, 0xFE];
    private static readonly byte[] s_utf16BigEndianBom = [0xFE, 0xFF];

    internal static string DecodeText(ReadOnlySpan<byte> bytes)
    {
        if ( bytes.StartsWith(s_utf8Bom) )
        {
            return Encoding.UTF8.GetString(bytes[3..]);
        }

        // Before the UTF-16 little-endian mark, which is this one's first two bytes.
        if ( bytes.StartsWith(s_utf32LittleEndianBom) )
        {
            return Encoding.UTF32.GetString(bytes[4..]);
        }

        if ( bytes.StartsWith(s_utf32BigEndianBom) )
        {
            return s_utf32BigEndian.GetString(bytes[4..]);
        }

        if ( bytes.StartsWith(s_utf16LittleEndianBom) )
        {
            return Encoding.Unicode.GetString(bytes[2..]);
        }

        if ( bytes.StartsWith(s_utf16BigEndianBom) )
        {
            return Encoding.BigEndianUnicode.GetString(bytes[2..]);
        }

        return Encoding.UTF8.GetString(bytes);
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

    /// <summary>
    /// Every script under a root, walked ONCE per subtree and in parallel across them.
    ///
    /// The walk is serial work that blocks every indexing worker behind it, and its cost is set by
    /// the size of the WORKSPACE rather than by the number of scripts: a workspace folder that is a
    /// whole Black Ops III install is 295,640 files hiding 1,105 scripts. Measured on that install,
    /// warm: 741-792 ms as it was, 483-504 ms fanned out across the top-level subtrees, 277-281 ms
    /// pruned, and 231-233 ms with both. All four return the same 1,105 files.
    ///
    /// The fan-out is per TOP-LEVEL subdirectory, which is uneven by nature — one subtree can hold
    /// most of the files — so it is worth less than the pruning and is kept because it costs
    /// nothing and helps any tree that happens to be wide.
    /// </summary>
    public IEnumerable<string> EnumerateFilesWithExtensions(string directory, ImmutableArray<string> extensions)
    {
        List<string> results = [];

        string[] subdirectories;
        try
        {
            subdirectories = Directory.GetDirectories(directory);
        }
        catch ( Exception exception ) when ( exception is IOException or UnauthorizedAccessException )
        {
            // Same contract as the enumerator below: a root that cannot be opened contributes
            // nothing rather than throwing part-way through indexing.
            return results;
        }

        Parallel.ForEach(
            subdirectories.Where(subdirectory => !IsToolOutput(subdirectory)),
            new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount - 1) },
            subdirectory =>
            {
                List<string> found = [.. WalkOne(subdirectory, extensions)];
                lock ( results )
                {
                    results.AddRange(found);
                }
            });

        // The root's own files, which the fan-out skips because it starts one level down.
        foreach ( string file in WalkOne(directory, extensions, recurse: false) )
        {
            results.Add(file);
        }

        return results;
    }

    private static bool IsToolOutput(string directory)
    {
        string name = Path.GetFileName(directory);
        foreach ( string toolOutput in GameProfile.ToolOutputDirectories )
        {
            if ( string.Equals(name, toolOutput, StringComparison.OrdinalIgnoreCase) )
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// One walk of the tree, with the extension test applied to the entry's name in place.
    ///
    /// <see cref="FileSystemEnumerable{TResult}"/> rather than <c>Directory.EnumerateFiles</c>
    /// because its predicate sees a <see cref="FileSystemEntry"/> whose FileName is a span over the
    /// buffer the OS already filled. A file that is not a script is rejected without a string ever
    /// existing for it, so the 157,422 non-scripts in a Black Ops 1 install cost a comparison each
    /// and nothing else.
    /// </summary>
    private static IEnumerable<string> WalkOne(
        string directory, ImmutableArray<string> extensions, bool recurse = true)
    {
        EnumerationOptions options = new()
        {
            RecurseSubdirectories = recurse,

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

            // Nothing under a tool-output tree is source, and on a game install those trees are
            // most of the files. Checked here as well as at the fan-out because they nest: BO3
            // keeps assetconvert under `share`, not at the root.
            ShouldRecursePredicate = static (ref FileSystemEntry entry) =>
            {
                foreach ( string toolOutput in GameProfile.ToolOutputDirectories )
                {
                    if ( entry.FileName.Equals(toolOutput, StringComparison.OrdinalIgnoreCase) )
                    {
                        return false;
                    }
                }

                return true;
            },
        };
    }
}
