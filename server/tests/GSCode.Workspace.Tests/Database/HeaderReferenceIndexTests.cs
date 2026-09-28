using System.Collections.Immutable;
using GSCode.Core.Symbols;
using GSCode.Core.Text;
using GSCode.Workspace.Database;
using Xunit;

namespace GSCode.Workspace.Tests.Database;

/// <summary>
/// References inside headers are read through the header store's reference index rather than by
/// scanning every header. These keep the scan as a reference and require the same references back,
/// across visibility, a header edited to drop a use, and a removed header.
/// </summary>
public class HeaderReferenceIndexTests
{
    private static readonly SymbolKey s_damage = new(null, "damage", SymbolKind.Macro);
    private static readonly SymbolKey s_health = new(null, "health", SymbolKind.Macro);
    private static readonly SymbolKey s_nothing = new(null, "nothing", SymbolKind.Macro);

    private static ReferenceEntry Use(SymbolKey key, int line)
    {
        return new ReferenceEntry(key, TextRange.FromCoordinates(line, 0, line, 6), ReferenceKind.Definition);
    }

    private static ScriptRecord Header(string path, string contextId, params ReferenceEntry[] references)
    {
        return new ScriptRecord
        {
            Path = path,
            Language = ScriptLanguage.Gsh,
            ContextId = contextId,
            ContentHash = 0,
            RelativePath = path[(path.IndexOf(@"\scripts\", StringComparison.Ordinal) + 1)..],
            References = [.. references],
        };
    }

    private static ScriptDatabase Workspace()
    {
        ScriptDatabase database = new();
        database.CommitRecord(Header(@"c:\raw\scripts\shared\shared.gsh", "raw", Use(s_damage, 1), Use(s_damage, 4), Use(s_health, 2)));
        database.CommitRecord(Header(@"c:\raw\scripts\shared\other.gsh", "raw", Use(s_health, 1)));
        database.CommitRecord(Header(@"c:\mods\m\scripts\shared\mine.gsh", "mod:m", Use(s_damage, 3)));
        database.CommitRecord(Header(@"c:\mods\n\scripts\shared\theirs.gsh", "mod:n", Use(s_damage, 5)));
        return database;
    }

    /// <summary>The scan FindGshReferences replaced: every header, every reference.</summary>
    private static List<string> Scan(ScriptDatabase database, string contextId, SymbolKey key)
    {
        List<string> found = [];
        foreach ( ScriptRecord record in database.AllGshRecords )
        {
            if ( !ScriptDatabase.CanSee(contextId, record.ContextId) )
            {
                continue;
            }

            foreach ( ReferenceEntry entry in record.References )
            {
                if ( entry.Key == key )
                {
                    found.Add($"{record.Path}@{entry.Range}");
                }
            }
        }

        found.Sort(StringComparer.Ordinal);
        return found;
    }

    private static List<string> Indexed(ScriptDatabase database, string contextId, SymbolKey key)
    {
        List<string> found = [];
        foreach ( (ScriptRecord Record, ReferenceEntry Entry) reference in DatabaseQueries.FindGshReferences(database, contextId, key) )
        {
            found.Add($"{reference.Record.Path}@{reference.Entry.Range}");
        }

        found.Sort(StringComparer.Ordinal);
        return found;
    }

    public static TheoryData<string, int> Questions()
    {
        TheoryData<string, int> questions = new();
        foreach ( string context in new[] { "raw", "mod:m", "mod:n" } )
        {
            for ( int key = 0; key < 3; key++ )
            {
                questions.Add(context, key);
            }
        }

        return questions;
    }

    private static SymbolKey KeyAt(int index)
    {
        return index switch
        {
            0 => s_damage,
            1 => s_health,
            _ => s_nothing,
        };
    }

    [Theory]
    [MemberData(nameof(Questions))]
    public void FindGshReferences_MatchesTheScanItReplaced(string contextId, int keyIndex)
    {
        ScriptDatabase database = Workspace();

        Assert.Equal(Scan(database, contextId, KeyAt(keyIndex)), Indexed(database, contextId, KeyAt(keyIndex)));
    }

    [Fact]
    public void EditsAndRemovals_LeaveTheIndexBehindThem()
    {
        ScriptDatabase database = Workspace();
        database.CommitRecord(Header(@"c:\raw\scripts\shared\shared.gsh", "raw", Use(s_health, 2)));
        database.Remove(@"c:\mods\m\scripts\shared\mine.gsh", ScriptLanguage.Gsh);

        foreach ( string context in new[] { "raw", "mod:m", "mod:n" } )
        {
            Assert.Equal(Scan(database, context, s_damage), Indexed(database, context, s_damage));
            Assert.Equal(Scan(database, context, s_health), Indexed(database, context, s_health));
        }

        Assert.Empty(Indexed(database, "mod:m", s_damage));
        ImmutableArray<string> declaring = database.GshFilesReferencing(s_damage);
        Assert.Equal([@"c:\mods\n\scripts\shared\theirs.gsh"], declaring.ToArray());
    }
}
