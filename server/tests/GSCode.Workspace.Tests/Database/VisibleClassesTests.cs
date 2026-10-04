using System.Collections.Immutable;
using GSCode.Core.Paths;
using GSCode.Core.Symbols;
using GSCode.Workspace.Database;
using Xunit;

namespace GSCode.Workspace.Tests.Database;

/// <summary>
/// The classes statement-scope completion offers come from the asking file and its imports, read
/// by path, rather than from every class-declaring file filtered to the imported ones. These keep
/// that filter as a reference and require the same class names from both, across an overlay, a
/// sibling mod, a file that imports itself, and an import that names nothing.
/// </summary>
public class VisibleClassesTests
{

    private static ScriptRecord Record(string path, string contextId, string relativePath, params string[] classes)
    {
        return TestRecords.At(path, contextId, relativePath) with
        {
            Classes = [.. classes.Select(static name => TestRecords.Class(name))],
        };
    }

    private static LanguageStore Workspace()
    {
        LanguageStore store = new();
        store.Upsert(Record(@"c:\raw\scripts\scene.gsc", "raw", @"scripts\scene.gsc", "cScene", "cSceneObject"));
        store.Upsert(Record(@"c:\raw\scripts\ai.gsc", "raw", @"scripts\ai.gsc", "cAi"));
        store.Upsert(Record(@"c:\raw\scripts\plain.gsc", "raw", @"scripts\plain.gsc"));
        store.Upsert(Record(@"c:\raw\scripts\own.gsc", "raw", @"scripts\own.gsc", "cOwn"));
        store.Upsert(Record(@"c:\mods\m\scripts\scene.gsc", "mod:m", @"scripts\scene.gsc", "cScene", "cModScene"));
        store.Upsert(Record(@"c:\mods\n\scripts\ai.gsc", "mod:n", @"scripts\ai.gsc", "cSiblingAi"));
        store.Upsert(Record(@"c:\mods\m\scripts\own.gsc", "mod:m", @"scripts\m_own.gsc", "cModOwn"));
        return store;
    }

    /// <summary>The filter AllVisibleClasses replaced: every class-declaring file, kept when it is the asker or imported.</summary>
    private static List<string> Reference(LanguageStore store, string contextId, string askingPath, ImmutableArray<string> imported)
    {
        string normalizedAskingPath = askingPath.Length == 0 ? "" : PathUtil.NormalizeAbsolute(askingPath);
        ImmutableArray<(ScriptRecord Record, ClassSymbol Class)>.Builder matches =
            ImmutableArray.CreateBuilder<(ScriptRecord, ClassSymbol)>();

        foreach ( string path in store.Classes.AllDeclaringPaths() )
        {
            if ( !store.TryGet(path, out ScriptRecord record) || !ScriptDatabase.CanSee(contextId, record.ContextId) )
            {
                continue;
            }

            bool sameFile = normalizedAskingPath.Length > 0
                && string.Equals(record.Path, normalizedAskingPath, StringComparison.OrdinalIgnoreCase);
            string relative = PathUtil.WithoutExtension(PathUtil.NormalizeScriptPath(record.RelativePath));
            if ( !sameFile && !imported.Contains(relative) )
            {
                continue;
            }

            foreach ( ClassSymbol classSymbol in record.Classes )
            {
                matches.Add((record, classSymbol));
            }
        }

        HashSet<string> names = new(StringComparer.Ordinal);
        foreach ( (ScriptRecord _, ClassSymbol classSymbol) in DatabaseQueries.ApplyShadowing(
            matches.ToImmutable(), static m => m.Record, static m => m.Class.KeyName, store, contextId) )
        {
            names.Add(classSymbol.KeyName);
        }

        return [.. names.Order(StringComparer.Ordinal)];
    }

    private static List<string> Indexed(LanguageStore store, string contextId, string askingPath, ImmutableArray<string> imported)
    {
        return [.. DatabaseQueries.AllVisibleClasses(store, contextId, askingPath, imported)
            .Select(static classSymbol => classSymbol.KeyName)
            .Distinct()
            .Order(StringComparer.Ordinal)];
    }

    public static TheoryData<string, string, string[]> Questions()
    {
        TheoryData<string, string, string[]> questions = new();
        string[][] importLists =
        [
            [],
            [@"scripts\scene"],
            [@"scripts\scene", @"scripts\ai"],
            [@"scripts\plain", @"scripts\nowhere"],
            [@"scripts\own"],
            [@"scripts\m_own"],
        ];

        foreach ( (string asking, string context) in new[]
        {
            (@"c:\raw\scripts\own.gsc", "raw"),
            (@"c:\mods\m\scripts\own.gsc", "mod:m"),
            (@"c:\mods\n\scripts\ai.gsc", "mod:n"),
            (@"c:\ws\unindexed.gsc", "raw"),
            ("", "raw"),
        } )
        {
            foreach ( string[] imports in importLists )
            {
                questions.Add(asking, context, imports);
            }
        }

        return questions;
    }

    [Theory]
    [MemberData(nameof(Questions))]
    public void AllVisibleClasses_MatchesTheFilterItReplaced(string askingPath, string contextId, string[] imports)
    {
        LanguageStore store = Workspace();
        ImmutableArray<string> imported = [.. imports];

        Assert.Equal(Reference(store, contextId, askingPath, imported), Indexed(store, contextId, askingPath, imported));
    }

    [Fact]
    public void AModsOverlay_ReplacesTheRawFilesClasses()
    {
        LanguageStore store = Workspace();

        List<string> classes = Indexed(store, "mod:m", @"c:\mods\m\scripts\own.gsc", [@"scripts\scene"]);

        Assert.Equal(["cmodown", "cmodscene", "cscene"], classes);
    }
}
