using System.Collections.Immutable;
using GSCode.Core.Symbols;
using GSCode.Core.Text;
using GSCode.Workspace.Database;
using Xunit;

namespace GSCode.Workspace.Tests.Database;

/// <summary>
/// The vocabulary index replaces a walk of every record's references and assignments for literal
/// and field completion, so it must offer exactly what that walk offered: literals and fields in
/// visible files only, never a literal a macro body wrote, and nothing an edit or removal took away.
/// </summary>
public class VocabularyIndexTests
{
    private static readonly TextRange s_someRange = TextRange.FromCoordinates(1, 0, 1, 4);

    private static ScriptRecord Record(
        string path,
        string contextId,
        ImmutableArray<ReferenceEntry> references = default,
        ImmutableArray<AssignmentSymbol> assignments = default)
    {
        return new ScriptRecord
        {
            Path = path,
            Language = ScriptLanguage.Gsc,
            ContextId = contextId,
            ContentHash = 0,
            References = references.IsDefault ? [] : references,
            Functions =
            [
                new FunctionSymbol
                {
                    Name = "f",
                    KeyName = "f",
                    Namespace = "ns",
                    NameRange = s_someRange,
                    FullRange = s_someRange,
                    Assignments = assignments.IsDefault ? [] : assignments,
                },
            ],
        };
    }

    private static List<VocabularyName> Literals(LanguageStore store, SymbolKind kind, string contextId)
    {
        List<VocabularyName> names = [];
        store.VisibleLiterals(kind, contextId, names.Add);
        return names;
    }

    private static List<string> Names(List<VocabularyName> names)
    {
        return [.. names.Select(static name => name.Name)];
    }

    private static ReferenceEntry Literal(string text, bool fromMacro = false)
    {
        return new ReferenceEntry(new SymbolKey(null, text, SymbolKind.StringLiteral), s_someRange, ReferenceKind.Literal, fromMacro);
    }

    [Fact]
    public void Literals_AreOfferedOnlyFromVisibleFilesAndNeverFromAMacroBody()
    {
        LanguageStore store = new();
        store.Upsert(Record(@"c:\raw\a.gsc", "raw", [Literal("raw_event"), Literal("macro_event", fromMacro: true)]));
        store.Upsert(Record(@"c:\mods\m\b.gsc", "mod:m", [Literal("mod_event")]));

        List<string> fromRaw = Names(Literals(store, SymbolKind.StringLiteral, "raw"));
        List<string> fromMod = Names(Literals(store, SymbolKind.StringLiteral, "mod:m"));

        Assert.Equal(["raw_event"], fromRaw);
        Assert.Equal(["mod_event", "raw_event"], fromMod.Order());
        Assert.Empty(Literals(store, SymbolKind.HashString, "mod:m"));
    }

    [Fact]
    public void ALiteralUsedInTwoFiles_IsOfferedOnce_AndSurvivesOneOfThemBeingRemoved()
    {
        LanguageStore store = new();
        store.Upsert(Record(@"c:\raw\a.gsc", "raw", [Literal("shared_event")]));
        store.Upsert(Record(@"c:\raw\b.gsc", "raw", [Literal("shared_event")]));

        Assert.Equal(["shared_event"], Names(Literals(store, SymbolKind.StringLiteral, "raw")));

        store.Remove(@"c:\raw\a.gsc");
        Assert.Equal(["shared_event"], Names(Literals(store, SymbolKind.StringLiteral, "raw")));

        store.Upsert(Record(@"c:\raw\b.gsc", "raw", [Literal("renamed_event")]));
        Assert.Equal(["renamed_event"], Names(Literals(store, SymbolKind.StringLiteral, "raw")));
    }

    [Fact]
    public void Fields_AreScopedToTheOwnerWhenOneIsGiven_AndLocalsAreNeverFields()
    {
        LanguageStore store = new();
        store.Upsert(Record(
            @"c:\raw\a.gsc",
            "raw",
            assignments:
            [
                new AssignmentSymbol("self", "health", "health", s_someRange),
                new AssignmentSymbol("level", "round", "round", s_someRange),
                new AssignmentSymbol("", "local", "local", s_someRange),
            ]));

        store.Upsert(Record(
            @"c:\mods\m\b.gsc",
            "mod:m",
            assignments: [new AssignmentSymbol("self", "mod_only", "mod_only", s_someRange)]));

        Assert.Equal(["health"], Names(store.VisibleFieldNames("self", "raw")));
        Assert.Equal(["health", "round"], Names(store.VisibleFieldNames(null, "raw")).Order());
        Assert.Equal(["health", "mod_only"], Names(store.VisibleFieldNames("self", "mod:m")).Order());
    }

    [Fact]
    public void EachNameCarriesHowManyFilesWriteIt_AndSpellingsAreKeptApart()
    {
        // The count ranks a list, so it is of every file writing the name. Spellings stay separate
        // — `Foo` and `foo` are the same field to the engine, but choosing between them is the
        // completion list's call — and one spelling written on two owners counts both.
        LanguageStore store = new();
        store.Upsert(Record(@"c:\raw\a.gsc", "raw", [Literal("shared_event")], [new AssignmentSymbol("level", "Foo", "foo", s_someRange)]));
        store.Upsert(Record(@"c:\raw\b.gsc", "raw", [Literal("shared_event")], [new AssignmentSymbol("self", "Foo", "foo", s_someRange)]));
        store.Upsert(Record(@"c:\raw\c.gsc", "raw", assignments: [new AssignmentSymbol("level", "foo", "foo", s_someRange)]));

        Assert.Equal([new VocabularyName("shared_event", 2)], Literals(store, SymbolKind.StringLiteral, "raw"));

        List<VocabularyName> everyOwner = store.VisibleFieldNames(null, "raw");
        Assert.Equal(2, everyOwner.Single(name => name.Name == "Foo").Files);
        Assert.Equal(1, everyOwner.Single(name => name.Name == "foo").Files);

        List<VocabularyName> onLevel = store.VisibleFieldNames("level", "raw");
        Assert.Equal(1, onLevel.Single(name => name.Name == "Foo").Files);
    }
}
