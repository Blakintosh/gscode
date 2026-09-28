using System.Collections.Immutable;
using GSCode.Core.Symbols;
using GSCode.Core.Text;
using GSCode.Workspace.Database;
using Xunit;

namespace GSCode.Workspace.Tests.Database;

/// <summary>
/// A qualified class lookup reads the files declaring the class INTO the namespace asked for,
/// rather than every declarer of the bare name. These keep the old read-then-filter as a reference
/// and require the same answer, and pin the qualified bucket across an edit that moves a class to
/// another namespace and a removal.
/// </summary>
public class QualifiedClassLookupTests
{
    private static readonly TextRange s_anywhere = new(new Position(1, 1), new Position(1, 5));

    private static ClassSymbol Class(string namespaceName, string name)
    {
        return new ClassSymbol
        {
            Name = name,
            KeyName = name.ToLowerInvariant(),
            Namespace = namespaceName,
            NameRange = s_anywhere,
            FullRange = s_anywhere,
        };
    }

    private static ScriptRecord Record(string path, string contextId, string relativePath, params ClassSymbol[] classes)
    {
        return new ScriptRecord
        {
            Path = path,
            Language = ScriptLanguage.Gsc,
            ContextId = contextId,
            ContentHash = 0,
            RelativePath = relativePath,
            Classes = [.. classes],
        };
    }

    /// <summary>
    /// cScene declared into three namespaces, twice into one of them, with a mod overlay of one raw
    /// file and a sibling mod declaring its own.
    /// </summary>
    private static LanguageStore Workspace()
    {
        LanguageStore store = new();
        store.Upsert(Record(@"c:\raw\scripts\a.gsc", "raw", @"scripts\a.gsc", Class("scene", "cScene"), Class("scene", "cOther")));
        store.Upsert(Record(@"c:\raw\scripts\b.gsc", "raw", @"scripts\b.gsc", Class("scene_b", "cScene")));
        store.Upsert(Record(@"c:\raw\scripts\c.gsc", "raw", @"scripts\c.gsc", Class("scene", "cScene")));
        store.Upsert(Record(@"c:\mods\m\scripts\a.gsc", "mod:m", @"scripts\a.gsc", Class("scene", "cScene")));
        store.Upsert(Record(@"c:\mods\n\scripts\d.gsc", "mod:n", @"scripts\d.gsc", Class("scene_d", "cScene")));
        return store;
    }

    /// <summary>The read LookupClasses replaced: every declarer of the bare name, filtered by namespace.</summary>
    private static List<string> Reference(LanguageStore store, string contextId, string? namespaceName, string keyName)
    {
        ImmutableArray<ResolvedClass>.Builder matches = ImmutableArray.CreateBuilder<ResolvedClass>();
        foreach ( string path in store.Classes.PathsDeclaring(keyName) )
        {
            if ( !store.TryGet(path, out ScriptRecord record) || !ScriptDatabase.CanSee(contextId, record.ContextId) )
            {
                continue;
            }

            foreach ( ClassSymbol classSymbol in record.Classes )
            {
                if ( classSymbol.KeyName == keyName && (namespaceName is null || classSymbol.Namespace == namespaceName) )
                {
                    matches.Add(new ResolvedClass(classSymbol, record));
                }
            }
        }

        return Describe(DatabaseQueries.ApplyShadowing(
            matches.ToImmutable(), static match => match.Record, static match => match.Class.KeyName, store, contextId));
    }

    private static List<string> Describe(ImmutableArray<ResolvedClass> classes)
    {
        List<string> described = [];
        foreach ( ResolvedClass resolved in classes )
        {
            described.Add($"{resolved.Record.Path}:{resolved.Class.Namespace}::{resolved.Class.KeyName}");
        }

        described.Sort(StringComparer.Ordinal);
        return described;
    }

    public static TheoryData<string, string?, string> Questions()
    {
        TheoryData<string, string?, string> questions = new();
        foreach ( string context in new[] { "raw", "mod:m", "mod:n" } )
        {
            foreach ( string? namespaceName in new[] { null, "scene", "scene_b", "scene_d", "nowhere" } )
            {
                foreach ( string keyName in new[] { "cscene", "cother", "cnothing" } )
                {
                    questions.Add(context, namespaceName, keyName);
                }
            }
        }

        return questions;
    }

    [Theory]
    [MemberData(nameof(Questions))]
    public void LookupClasses_MatchesTheFilterItReplaced(string contextId, string? namespaceName, string keyName)
    {
        LanguageStore store = Workspace();

        Assert.Equal(
            Reference(store, contextId, namespaceName, keyName),
            Describe(DatabaseQueries.LookupClasses(store, contextId, namespaceName, keyName)));
    }

    [Fact]
    public void AClassMovedToAnotherNamespace_LeavesItsOldBucket()
    {
        LanguageStore store = Workspace();
        store.Upsert(Record(@"c:\raw\scripts\c.gsc", "raw", @"scripts\c.gsc", Class("scene_c", "cScene")));

        Assert.Equal([@"c:\mods\m\scripts\a.gsc", @"c:\raw\scripts\a.gsc"], store.Classes.PathsDeclaring("scene", "cscene").Order().ToArray());
        Assert.Equal([@"c:\raw\scripts\c.gsc"], store.Classes.PathsDeclaring("scene_c", "cscene").ToArray());
        Assert.Equal(Reference(store, "raw", "scene", "cscene"), Describe(DatabaseQueries.LookupClasses(store, "raw", "scene", "cscene")));
    }

    [Fact]
    public void ARemovedFile_LeavesNoQualifiedBucket()
    {
        LanguageStore store = Workspace();
        store.Remove(@"c:\raw\scripts\b.gsc");

        Assert.Empty(store.Classes.PathsDeclaring("scene_b", "cscene"));
        Assert.Empty(DatabaseQueries.LookupClasses(store, "raw", "scene_b", "cscene"));
    }
}
