using GSCode.Core.Symbols;
using GSCode.Workspace.Database;
using Xunit;

namespace GSCode.Workspace.Tests.Database;

/// <summary>
/// Path completion inside a directive reads the folder index instead of rewriting every record's
/// path per keystroke. These keep that walk as a reference and require the same listing from the
/// index, for raw, mod, sibling-mod and workspace askers, across casing, a name that is both a folder
/// and a file, and edits and removals that must take a file's folders with it.
/// </summary>
public class PathTreeIndexTests
{
    private static ScriptRecord Record(string path, string contextId, string relativePath)
    {
        return new ScriptRecord
        {
            Path = path,
            Language = ScriptLanguage.Gsc,
            ContextId = contextId,
            ContentHash = 0,
            RelativePath = relativePath,
        };
    }

    private static List<ScriptRecord> Records()
    {
        return
        [
            Record(@"c:\raw\scripts\shared\util.gsc", "raw", @"scripts\shared\util.gsc"),
            Record(@"c:\raw\scripts\shared\math.gsc", "raw", @"scripts/shared/math.gsc"),
            Record(@"c:\raw\scripts\shared\ai\zombie.gsc", "raw", @"scripts\shared\ai\zombie.gsc"),
            Record(@"c:\raw\scripts\shared\ai.gsc", "raw", @"scripts\shared\ai.gsc"),
            Record(@"c:\raw\scripts\mp\gametype.gsc", "raw", @"Scripts\MP\gametype.gsc"),
            Record(@"c:\mods\m\scripts\shared\util.gsc", "mod:m", @"scripts\shared\util.gsc"),
            Record(@"c:\mods\m\scripts\m_only\thing.gsc", "mod:m", @"scripts\m_only\thing.gsc"),
            Record(@"c:\mods\n\scripts\n_only\thing.gsc", "mod:n", @"scripts\n_only\thing.gsc"),
            Record(@"c:\ws\scripts\ws_only.gsc", @"workspace:c:\ws", @"scripts\ws_only.gsc"),
            Record(@"c:\outside\loose.gsc", "raw", ""),
            Record(@"c:\raw\odd\\double.gsc", "raw", @"odd\\double.gsc"),
        ];
    }

    private static LanguageStore Store(List<ScriptRecord> records)
    {
        LanguageStore store = new();
        foreach ( ScriptRecord record in records )
        {
            store.Upsert(record);
        }

        return store;
    }

    /// <summary>The walk path completion replaced, over every record in the store.</summary>
    private static SortedDictionary<string, bool> Walk(LanguageStore store, string directory, string contextId)
    {
        Dictionary<string, bool> segments = new(StringComparer.OrdinalIgnoreCase);
        foreach ( ScriptRecord record in store.AllRecords )
        {
            if ( record.RelativePath.Length == 0 || !ScriptDatabase.CanSee(contextId, record.ContextId) )
            {
                continue;
            }

            string relative = record.RelativePath.Replace('/', '\\');
            string path = System.IO.Path.ChangeExtension(relative, null) ?? relative;
            if ( !path.StartsWith(directory, StringComparison.OrdinalIgnoreCase) )
            {
                continue;
            }

            string remainder = path[directory.Length..];
            if ( remainder.Length == 0 )
            {
                continue;
            }

            int separator = remainder.IndexOf('\\');
            bool isFolder = separator >= 0;
            string segment = isFolder ? remainder[..separator] : remainder;
            segments[segment] = segments.TryGetValue(segment, out bool existing) ? existing || isFolder : isFolder;
        }

        return Listing(segments.Select(static pair => (pair.Key, pair.Value)));
    }

    private static SortedDictionary<string, bool> Indexed(LanguageStore store, string directory, string contextId)
    {
        return Listing(store.PathChildren(directory, contextId));
    }

    /// <summary>Keyed lowercase: the spelling kept for a segment is whichever was seen first, in either path.</summary>
    private static SortedDictionary<string, bool> Listing(IEnumerable<(string Segment, bool IsFolder)> children)
    {
        SortedDictionary<string, bool> listing = new(StringComparer.Ordinal);
        foreach ( (string Segment, bool IsFolder) child in children )
        {
            listing[child.Segment.ToLowerInvariant()] = child.IsFolder;
        }

        return listing;
    }

    public static TheoryData<string, string> Questions()
    {
        TheoryData<string, string> questions = new();
        string[] directories = ["", @"scripts\", @"SCRIPTS\", @"scripts\shared\", @"scripts\shared\ai\", @"scripts\mp\", @"odd\", @"odd\\", @"scripts/shared\", @"nowhere\"];
        string[] contexts = ["raw", "mod:m", "mod:n", @"workspace:c:\ws"];
        foreach ( string directory in directories )
        {
            foreach ( string context in contexts )
            {
                questions.Add(directory, context);
            }
        }

        return questions;
    }

    [Theory]
    [MemberData(nameof(Questions))]
    public void Children_MatchTheWalkTheyReplaced(string directory, string contextId)
    {
        LanguageStore store = Store(Records());

        Assert.Equal(Walk(store, directory, contextId), Indexed(store, directory, contextId));
    }

    [Fact]
    public void AFolderThatIsAlsoAFile_ListsAsAFolder()
    {
        LanguageStore store = Store(Records());

        Assert.True(Indexed(store, @"scripts\shared\", "raw")["ai"]);
    }

    [Fact]
    public void AModsOwnFolder_IsListedOnlyForThatMod()
    {
        LanguageStore store = Store(Records());

        Assert.Contains("m_only", Indexed(store, @"scripts\", "mod:m").Keys);
        Assert.DoesNotContain("m_only", Indexed(store, @"scripts\", "mod:n").Keys);
        Assert.DoesNotContain("m_only", Indexed(store, @"scripts\", "raw").Keys);
    }

    [Fact]
    public void EditsAndRemovals_TakeTheirFoldersWithThem()
    {
        List<ScriptRecord> records = Records();
        LanguageStore store = Store(records);

        // The only file under ai\ moves out, and the only file under mp\ goes away.
        store.Upsert(Record(@"c:\raw\scripts\shared\ai\zombie.gsc", "raw", @"scripts\shared\zombie.gsc"));
        store.Remove(@"c:\raw\scripts\mp\gametype.gsc");
        store.Remove(@"c:\mods\m\scripts\m_only\thing.gsc");

        foreach ( string directory in new[] { "", @"scripts\", @"scripts\shared\", @"scripts\shared\ai\", @"scripts\mp\" } )
        {
            foreach ( string context in new[] { "raw", "mod:m" } )
            {
                Assert.Equal(Walk(store, directory, context), Indexed(store, directory, context));
            }
        }

        Assert.False(Indexed(store, @"scripts\shared\", "raw")["ai"]);
        Assert.DoesNotContain("mp", Indexed(store, @"scripts\", "raw").Keys);
        Assert.DoesNotContain("m_only", Indexed(store, @"scripts\", "mod:m").Keys);
    }

    [Fact]
    public void HeadersKeepTheirExtension()
    {
        ScriptDatabase database = new();
        database.CommitRecord(new ScriptRecord
        {
            Path = @"c:\raw\scripts\shared\shared.gsh",
            Language = ScriptLanguage.Gsh,
            ContextId = "raw",
            ContentHash = 0,
            RelativePath = @"scripts\shared\shared.gsh",
        });

        (string Segment, bool IsFolder) child = Assert.Single(database.GshPathChildren(@"scripts\shared\", "raw"));
        Assert.Equal("shared.gsh", child.Segment);
        Assert.False(child.IsFolder);
        Assert.Empty(database.Gsc.PathChildren(@"scripts\shared\", "raw"));
    }
}
