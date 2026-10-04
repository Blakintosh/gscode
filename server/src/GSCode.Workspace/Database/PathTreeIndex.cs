namespace GSCode.Workspace.Database;

/// <summary>
/// The script-relative folder tree, one level at a time: for each directory, the segments directly
/// under it and whether each is a folder or a file. What path completion inside a directive lists.
///
/// It replaces a walk of the whole store per keystroke. Completing <c>#using scripts\shared\</c>
/// rewrote every record's relative path — separators, extension — and tested it against the typed
/// folder, to list the few dozen names in that one folder. At 50,000 files that took a cod4
/// completion to its 10 ms budget and was still growing (PERF.md, the scale section).
///
/// Counted per context rather than per file, because what a folder shows depends on who is asking:
/// a raw script lists what raw and nothing else holds, a mod script adds its own mod's files. A
/// segment is listed when any context the asker can see holds a file under it, which is the
/// visibility test the walk made per record.
///
/// Folders and directories compare ignoring case, as the walk's <c>StartsWith</c> and its segment
/// dictionary did. The spelling kept for a segment is the first one indexed.
/// </summary>
public sealed class PathTreeIndex
{
    private sealed class Counts
    {
        public int Folders;
        public int Files;
    }

    private sealed class Child
    {
        public Child(string display)
        {
            Display = display;
        }

        public string Display { get; }

        public Dictionary<string, Counts> ByContext { get; } = new(StringComparer.Ordinal);
    }

    private readonly Dictionary<string, Dictionary<string, Child>> _children = new(StringComparer.OrdinalIgnoreCase);
    private readonly bool _keepExtension;
    private readonly Lock _gate = new();

    /// <param name="keepExtension">
    /// True for headers, which <c>#insert</c> names with their <c>.gsh</c>; false for scripts, which
    /// <c>#using</c> and <c>#include</c> name without one.
    /// </param>
    public PathTreeIndex(bool keepExtension)
    {
        _keepExtension = keepExtension;
    }

    /// <summary>
    /// The path as a directive names it, or null for a record outside every root. The same
    /// rewrite completion applied to each record while it walked them.
    /// </summary>
    public string? DirectiveFormOf(ScriptRecord? record)
    {
        if ( record is null || record.RelativePath.Length == 0 )
        {
            return null;
        }

        string relative = record.RelativePath.Replace('/', '\\');
        if ( _keepExtension )
        {
            return relative;
        }

        return System.IO.Path.ChangeExtension(relative, null) ?? relative;
    }

    /// <summary>Replaces one file's contribution: the record it was, and the record it is now.</summary>
    public void Apply(ScriptRecord? previous, ScriptRecord? next)
    {
        string? oldPath = DirectiveFormOf(previous);
        string? newPath = DirectiveFormOf(next);

        if ( string.Equals(oldPath, newPath, StringComparison.Ordinal)
            && string.Equals(previous?.ContextId, next?.ContextId, StringComparison.Ordinal) )
        {
            return;
        }

        lock ( _gate )
        {
            if ( oldPath is not null )
            {
                Adjust(oldPath, previous!.ContextId, -1);
            }

            if ( newPath is not null )
            {
                Adjust(newPath, next!.ContextId, 1);
            }
        }
    }

    /// <summary>
    /// The segments directly under <paramref name="directory"/> — as typed, up to and including its
    /// last backslash, or "" for the top — that a file in <paramref name="askingContextId"/> can see.
    /// A segment that is both a folder and a file is a folder, the one with more below it to reach.
    /// </summary>
    public List<(string Segment, bool IsFolder)> Children(string directory, string askingContextId)
    {
        List<(string Segment, bool IsFolder)> children = [];

        lock ( _gate )
        {
            if ( !_children.TryGetValue(directory, out Dictionary<string, Child>? segments) )
            {
                return children;
            }

            foreach ( Child child in segments.Values )
            {
                bool visible = false;
                bool isFolder = false;
                foreach ( KeyValuePair<string, Counts> context in child.ByContext )
                {
                    if ( !ScriptDatabase.CanSee(askingContextId, context.Key) )
                    {
                        continue;
                    }

                    visible = true;
                    if ( context.Value.Folders > 0 )
                    {
                        isFolder = true;
                        break;
                    }
                }

                if ( visible )
                {
                    children.Add((child.Display, isFolder));
                }
            }
        }

        return children;
    }

    /// <summary>
    /// Counts one path in or out of every directory on the way down to it: under each directory
    /// prefix ending in a backslash (and "" for the top), the next segment, as a folder when more
    /// path follows it.
    /// </summary>
    private void Adjust(string path, string contextId, int delta)
    {
        int directoryEnd = 0;
        while ( directoryEnd < path.Length )
        {
            string remainder = path[directoryEnd..];
            int separator = remainder.IndexOf('\\');
            bool isFolder = separator >= 0;
            string segment = isFolder ? remainder[..separator] : remainder;

            AdjustChild(path[..directoryEnd], segment, isFolder, contextId, delta);

            if ( !isFolder )
            {
                break;
            }

            directoryEnd += separator + 1;
        }
    }

    private void AdjustChild(string directory, string segment, bool isFolder, string contextId, int delta)
    {
        if ( !_children.TryGetValue(directory, out Dictionary<string, Child>? segments) )
        {
            if ( delta < 0 )
            {
                return;
            }

            segments = new Dictionary<string, Child>(StringComparer.OrdinalIgnoreCase);
            _children[directory] = segments;
        }

        if ( !segments.TryGetValue(segment, out Child? child) )
        {
            if ( delta < 0 )
            {
                return;
            }

            child = new Child(segment);
            segments[segment] = child;
        }

        if ( !child.ByContext.TryGetValue(contextId, out Counts? counts) )
        {
            if ( delta < 0 )
            {
                return;
            }

            counts = new Counts();
            child.ByContext[contextId] = counts;
        }

        if ( isFolder )
        {
            counts.Folders += delta;
        }
        else
        {
            counts.Files += delta;
        }

        if ( counts.Folders > 0 || counts.Files > 0 )
        {
            return;
        }

        child.ByContext.Remove(contextId);
        if ( child.ByContext.Count > 0 )
        {
            return;
        }

        segments.Remove(segment);
        if ( segments.Count == 0 )
        {
            _children.Remove(directory);
        }
    }
}
