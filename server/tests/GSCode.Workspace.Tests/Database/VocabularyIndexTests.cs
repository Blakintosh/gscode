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
    private static readonly TextRange SomeRange = TextRange.FromCoordinates(1, 0, 1, 4);

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
                    NameRange = SomeRange,
                    FullRange = SomeRange,
                    Assignments = assignments.IsDefault ? [] : assignments,
                },
            ],
        };
    }

    private static ReferenceEntry Literal(string text, bool fromMacro = false)
    {
        return new ReferenceEntry(new SymbolKey(null, text, SymbolKind.StringLiteral), SomeRange, ReferenceKind.Literal, fromMacro);
    }

    [Fact]
    public void Literals_AreOfferedOnlyFromVisibleFilesAndNeverFromAMacroBody()
    {
        LanguageStore store = new();
        store.Upsert(Record(@"c:\raw\a.gsc", "raw", [Literal("raw_event"), Literal("macro_event", fromMacro: true)]));
        store.Upsert(Record(@"c:\mods\m\b.gsc", "mod:m", [Literal("mod_event")]));

        List<string> fromRaw = store.VisibleLiterals(SymbolKind.StringLiteral, "raw");
        List<string> fromMod = store.VisibleLiterals(SymbolKind.StringLiteral, "mod:m");

        Assert.Equal(["raw_event"], fromRaw);
        Assert.Equal(["mod_event", "raw_event"], fromMod.Order());
        Assert.Empty(store.VisibleLiterals(SymbolKind.HashString, "mod:m"));
    }

    [Fact]
    public void ALiteralUsedInTwoFiles_IsOfferedOnce_AndSurvivesOneOfThemBeingRemoved()
    {
        LanguageStore store = new();
        store.Upsert(Record(@"c:\raw\a.gsc", "raw", [Literal("shared_event")]));
        store.Upsert(Record(@"c:\raw\b.gsc", "raw", [Literal("shared_event")]));

        Assert.Equal(["shared_event"], store.VisibleLiterals(SymbolKind.StringLiteral, "raw"));

        store.Remove(@"c:\raw\a.gsc");
        Assert.Equal(["shared_event"], store.VisibleLiterals(SymbolKind.StringLiteral, "raw"));

        store.Upsert(Record(@"c:\raw\b.gsc", "raw", [Literal("renamed_event")]));
        Assert.Equal(["renamed_event"], store.VisibleLiterals(SymbolKind.StringLiteral, "raw"));
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
                new AssignmentSymbol("self", "health", "health", SomeRange),
                new AssignmentSymbol("level", "round", "round", SomeRange),
                new AssignmentSymbol("", "local", "local", SomeRange),
            ]));

        store.Upsert(Record(
            @"c:\mods\m\b.gsc",
            "mod:m",
            assignments: [new AssignmentSymbol("self", "mod_only", "mod_only", SomeRange)]));

        Assert.Equal(["health"], store.VisibleFieldNames("self", "raw"));
        Assert.Equal(["health", "round"], store.VisibleFieldNames(null, "raw").Order());
        Assert.Equal(["health", "mod_only"], store.VisibleFieldNames("self", "mod:m").Order());
    }
}
