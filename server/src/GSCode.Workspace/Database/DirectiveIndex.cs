using System.Collections.Immutable;

namespace GSCode.Workspace.Database;

/// <summary>
/// Which files write a directive path, and which files <c>#insert</c> a header that resolved to a
/// given file. The reverse of every dependency edge, keyed two ways.
///
/// It replaces two walks of every record in the workspace. A header changing on disk made the file
/// watcher test every GSC and CSC record's edges, and every header's again per round of its
/// header-to-header closure, to find the handful that insert it. A file rename made the planner do
/// the same to find the directives naming it. At 50,000 files both read every edge in the workspace
/// to keep a few (PERF.md, the scale section).
///
/// The written-path key is looser than either caller's comparison — separators, case, surrounding
/// whitespace and leading backslashes all fold away — so the files it names are a superset of what
/// the caller's own test keeps, and the caller still runs that test. <see cref="DependentsIndex"/>
/// is not reused because it leaves insert edges out on purpose, and keeps the extension-less form.
/// </summary>
public sealed class DirectiveIndex
{
    /// <summary>What one file contributes, built outside the caller's write gate.</summary>
    public sealed class Contribution
    {
        public static Contribution None { get; } = new([], []);

        public Contribution(HashSet<string> written, HashSet<string> inserted)
        {
            Written = written;
            Inserted = inserted;
        }

        /// <summary>Every directive path the file writes, in <see cref="WrittenKey"/>'s form.</summary>
        public HashSet<string> Written { get; }

        /// <summary>The resolved absolute path of every header the file inserts.</summary>
        public HashSet<string> Inserted { get; }
    }

    private readonly PackedInvertedIndex<string> _byWrittenPath = new(StringComparer.Ordinal);
    private readonly PackedInvertedIndex<string> _byInsertedPath = new(StringComparer.Ordinal);

    public static Contribution Of(ScriptRecord? record)
    {
        if ( record is null || record.Dependencies.IsDefaultOrEmpty )
        {
            return Contribution.None;
        }

        HashSet<string> written = new(StringComparer.Ordinal);
        HashSet<string> inserted = new(StringComparer.Ordinal);
        foreach ( DependencyEdge edge in record.Dependencies )
        {
            written.Add(WrittenKey(edge.RawPath));

            if ( edge.IsInsert && edge.ResolvedPath.Length > 0 )
            {
                inserted.Add(edge.ResolvedPath);
            }
        }

        return new Contribution(written, inserted);
    }

    /// <summary>
    /// A directive path with everything either caller's comparison ignores folded away: forward
    /// slashes, case, surrounding whitespace, and leading backslashes. Two paths the watcher's
    /// <c>NormalizeScriptPath</c> or the rename planner's canonical form call equal always share
    /// this key, which is what lets both callers read this index and keep their own test.
    /// </summary>
    public static string WrittenKey(string directivePath)
    {
        int start = 0;
        int end = directivePath.Length;

        while ( start < end && (directivePath[start] is '\\' or '/' || char.IsWhiteSpace(directivePath[start])) )
        {
            start++;
        }

        while ( end > start && char.IsWhiteSpace(directivePath[end - 1]) )
        {
            end--;
        }

        return directivePath[start..end].Replace('/', '\\').ToLowerInvariant();
    }

    /// <summary>Replaces one file's contribution.</summary>
    public void Apply(string path, Contribution oldContribution, Contribution newContribution)
    {
        _byWrittenPath.Apply(path, oldContribution.Written, newContribution.Written);
        _byInsertedPath.Apply(path, oldContribution.Inserted, newContribution.Inserted);
    }

    /// <summary>Paths of the files writing a directive path with this <see cref="WrittenKey"/> (snapshot).</summary>
    public ImmutableArray<string> FilesWriting(string writtenKey)
    {
        return _byWrittenPath.FilesFor(writtenKey);
    }

    /// <summary>Paths of the files with an insert edge resolved to this header (snapshot).</summary>
    public ImmutableArray<string> FilesInserting(string resolvedHeaderPath)
    {
        return _byInsertedPath.FilesFor(resolvedHeaderPath);
    }
}
