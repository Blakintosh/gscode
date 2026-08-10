using System.Collections.Immutable;
using GSCode.Core;
using GSCode.Core.Text;
using GSCode.Parser;
using GSCode.Workspace.Api;
using GSCode.Workspace.Completion;
using GSCode.Workspace.Database;
using GSCode.Workspace.Indexing;
using GSCode.Workspace.Resolution;
using GSCode.Workspace.Tests.Resolution;
using Xunit;

namespace GSCode.Workspace.Tests.Completion;

public class CompletionEngineTests
{
    private const string Raw = @"C:\bo3\share\raw";
    private static string ApiDirectory => Path.Combine(AppContext.BaseDirectory, "Api");

    private static (CompletionEngine Engine, ScriptDatabase Db, PathResolver Resolver) BuildWorld(FakeFileSystem files)
    {
        RootConfig config = RootConfig.Create(true, @"C:\bo3\share\raw", @"C:\bo3\mods", [], files);
        PathResolver resolver = new(config, files);
        ScriptDatabase database = new();
        WorkspaceIndexer indexer = new(database, () => resolver, files, new NameTable());
        indexer.IndexAsync(IndexingMode.Partial, NullIndexProgressListener.Instance, CancellationToken.None).GetAwaiter().GetResult();

        CompletionEngine engine = new(database, BuiltinApiSet.Load(ApiDirectory), ObjectFields.Load(ApiDirectory));
        return (engine, database, resolver);
    }

    private static ParseResult Analyze(string path, string text)
    {
        return ScriptAnalysis.Analyze(path, ScriptAnalysis.LanguageFromPath(path), SourceText.From(text), GSCode.Parser.Preprocessing.NullInsertProvider.Instance, new NameTable());
    }

    /// <summary>
    /// Whether an entry with this label is offered. A FUNCTION's label carries its parameter list
    /// ("get_players( team )"), so naming the function still means that entry — matched on the
    /// opening parenthesis so `alpha` does not also match `alphaBeta( x )`.
    /// </summary>
    private static bool HasLabel(ImmutableArray<CompletionEntry> entries, string label)
    {
        return entries.Any(e => string.Equals(e.Label, label, StringComparison.Ordinal)
            || (e.Kind == CompletionKind.Function && e.Label.StartsWith(label + "(", StringComparison.Ordinal)));
    }

    [Fact]
    public void NamespaceQualified_OffersOnlyThatNamespacesFunctions()
    {
        FakeFileSystem files = new FakeFileSystem()
            .AddFile(@$"{Raw}\scripts\util.gsc", "#namespace util;\nfunction alpha()\n{\n}\nfunction beta()\n{\n}\n")
            .AddFile(@$"{Raw}\scripts\other.gsc", "#namespace other;\nfunction gamma()\n{\n}\n");

        (CompletionEngine engine, _, _) = BuildWorld(files);

        // "util::" — cursor right after the ::.
        string text = "#namespace game;\nfunction run()\n{\n    util::\n}\n";
        ParseResult result = Analyze(@$"{Raw}\scripts\main.gsc", text);
        Position after = new(3, 10); // just past "util::"

        ImmutableArray<CompletionEntry> entries = engine.Complete(result, "raw", after);

        Assert.True(HasLabel(entries, "alpha"));
        Assert.True(HasLabel(entries, "beta"));
        Assert.False(HasLabel(entries, "gamma"));
    }

    [Fact]
    public void NamespaceQualified_OffersPrivateFunctions_ToFilesInTheSameNamespace()
    {
        FakeFileSystem files = new FakeFileSystem()
            .AddFile(@$"{Raw}\scripts\util.gsc", "#namespace util;\nfunction private hidden()\n{\n}\nfunction shown()\n{\n}\n");

        (CompletionEngine engine, _, _) = BuildWorld(files);

        // main.gsc declares the SAME namespace, so util's private members are in scope:
        // privacy is scoped to the namespace, not the file.
        string sameNamespace = "#namespace util;\nfunction run()\n{\n    util::\n}\n";
        ParseResult inside = Analyze(@$"{Raw}\scripts\main.gsc", sameNamespace);
        ImmutableArray<CompletionEntry> insideEntries = engine.Complete(inside, "raw", new Position(3, 10));

        Assert.True(HasLabel(insideEntries, "hidden"));
        Assert.True(HasLabel(insideEntries, "shown"));

        // A file in a different namespace sees only the public one.
        string otherNamespace = "#namespace game;\nfunction run()\n{\n    util::\n}\n";
        ParseResult outside = Analyze(@$"{Raw}\scripts\other.gsc", otherNamespace);
        ImmutableArray<CompletionEntry> outsideEntries = engine.Complete(outside, "raw", new Position(3, 10));

        Assert.False(HasLabel(outsideEntries, "hidden"));
        Assert.True(HasLabel(outsideEntries, "shown"));
    }

    [Fact]
    public void Keywords_CarryDocumentation_AndAssertIsNotAKeyword()
    {
        FakeFileSystem files = new FakeFileSystem().AddFile(@$"{Raw}\scripts\dummy.gsc", "function d()\n{\n}\n");
        (CompletionEngine engine, _, _) = BuildWorld(files);

        string text = "function run()\n{\n    \n}\n";
        ParseResult result = Analyze(@$"{Raw}\scripts\main.gsc", text);
        ImmutableArray<CompletionEntry> entries = engine.Complete(result, "raw", new Position(2, 4));

        CompletionEntry isdefined = entries.First(e => e.Label == "isdefined" && e.Kind == CompletionKind.Keyword);
        Assert.Contains("undefined", isdefined.Documentation);

        // assert / assertmsg are engine builtins, not keywords — they must not appear as keyword items.
        Assert.DoesNotContain(entries, e => e.Kind == CompletionKind.Keyword && e.Label == "assert");
        Assert.DoesNotContain(entries, e => e.Kind == CompletionKind.Keyword && e.Label == "assertmsg");
    }

    [Fact]
    public void InsideStringLiteral_OffersKnownStringLiterals()
    {
        FakeFileSystem files = new FakeFileSystem()
            .AddFile(@$"{Raw}\scripts\events.gsc", "#namespace ev;\nfunction fire()\n{\n    self notify( \"player_spawned\" );\n}\n");
        (CompletionEngine engine, _, _) = BuildWorld(files);

        // main.gsc: cursor inside the empty string on line 3 (between the quotes).
        string text = "#namespace game;\nfunction run()\n{\n    x = \"\";\n}\n";
        ParseResult result = Analyze(@$"{Raw}\scripts\main.gsc", text);
        Position insideString = new(3, 9);

        ImmutableArray<CompletionEntry> entries = engine.Complete(result, "raw", insideString);

        Assert.True(HasLabel(entries, "player_spawned"));
        Assert.All(entries, e => Assert.Equal(CompletionKind.Literal, e.Kind));
    }

    // --- Concatenated message fragments are not names ---
    //
    // The reported noise: the list filled with things like "already exists. Proceeding with
    // override" and " at origin: ". Those are pieces of a message being built with '+', not
    // vocabulary anyone wants to insert.

    /// <summary>Completes inside an empty string in main.gsc, given a workspace file to harvest.</summary>
    private static ImmutableArray<CompletionEntry> LiteralsFrom(string harvestSource)
    {
        FakeFileSystem files = new FakeFileSystem().AddFile(@$"{Raw}\scripts\events.gsc", harvestSource);
        (CompletionEngine engine, _, _) = BuildWorld(files);

        ParseResult result = Analyze(
            @$"{Raw}\scripts\main.gsc", "#namespace game;\nfunction run()\n{\n    x = \"\";\n}\n");

        return engine.Complete(result, "raw", new Position(3, 9));
    }

    [Fact]
    public void ConcatenatedFragments_AreNotOffered()
    {
        ImmutableArray<CompletionEntry> entries = LiteralsFrom(
            "#namespace ev;\nfunction f( n )\n{\n    IPrintLn( \"at origin: \" + n );\n}\n");

        Assert.False(HasLabel(entries, "at origin: "));
    }

    [Fact]
    public void AFragmentOnEitherSideOfThePlus_IsExcluded()
    {
        ImmutableArray<CompletionEntry> entries = LiteralsFrom(
            "#namespace ev;\nfunction f( n )\n{\n    IPrintLn( \"left\" + n + \"right\" );\n}\n");

        Assert.False(HasLabel(entries, "left"));
        Assert.False(HasLabel(entries, "right"));
    }

    [Fact]
    public void FragmentsNestedDeeperInTheChain_AreAlsoExcluded()
    {
        // The whole chain is a message, however the parser happened to nest it.
        ImmutableArray<CompletionEntry> entries = LiteralsFrom(
            "#namespace ev;\nfunction f( a, b )\n{\n    IPrintLn( \"one\" + a + \"two\" + b + \"three\" );\n}\n");

        Assert.False(HasLabel(entries, "one"));
        Assert.False(HasLabel(entries, "two"));
        Assert.False(HasLabel(entries, "three"));
    }

    [Fact]
    public void AStandaloneArgumentIsStillOffered()
    {
        // The point of literal completion: real vocabulary must survive.
        Assert.True(HasLabel(
            LiteralsFrom("#namespace ev;\nfunction f()\n{\n    self notify( \"player_spawned\" );\n}\n"),
            "player_spawned"));
    }

    [Fact]
    public void AStandaloneLiteralElsewhereInTheSameFile_IsUnaffected()
    {
        // Being concatenated at ONE site must not blacklist the text everywhere: the same string
        // used as a real argument somewhere else is still a name.
        ImmutableArray<CompletionEntry> entries = LiteralsFrom(
            "#namespace ev;\nfunction f( n )\n{\n    self notify( \"death\" );\n    IPrintLn( \"death\" + n );\n}\n");

        Assert.True(HasLabel(entries, "death"));
    }

    // --- A name you just invented ---
    //
    // The point of the feature, and the reason the filtering above is structural rather than
    // based on how often a string is used: writing `self notify( "foobarbaz" );` and then
    // `self endon( "` on the next line has to offer foobarbaz, even though it exists nowhere
    // else in the workspace and has been written exactly once.

    [Fact]
    public void ANameJustTypedInThisFile_IsOfferedImmediately()
    {
        FakeFileSystem files = new FakeFileSystem().AddFile(@$"{Raw}\scripts\dummy.gsc", "function d()\n{\n}\n");
        (CompletionEngine engine, _, _) = BuildWorld(files);

        // The second string is still open, exactly as it is mid-keystroke.
        string text = "#namespace game;\nfunction run()\n{\n    self notify( \"foobarbaz\" );\n    self endon( \"\n}\n";
        ParseResult result = Analyze(@$"{Raw}\scripts\main.gsc", text);

        ImmutableArray<CompletionEntry> entries = engine.Complete(result, "raw", new Position(4, 17));

        Assert.True(HasLabel(entries, "foobarbaz"));
    }

    [Fact]
    public void ANameJustTypedInThisFile_IsOfferedEvenWithTheStringClosed()
    {
        FakeFileSystem files = new FakeFileSystem().AddFile(@$"{Raw}\scripts\dummy.gsc", "function d()\n{\n}\n");
        (CompletionEngine engine, _, _) = BuildWorld(files);

        string text = "#namespace game;\nfunction run()\n{\n    self notify( \"foobarbaz\" );\n    self endon( \"\" );\n}\n";
        ParseResult result = Analyze(@$"{Raw}\scripts\main.gsc", text);

        ImmutableArray<CompletionEntry> entries = engine.Complete(result, "raw", new Position(4, 17));

        Assert.True(HasLabel(entries, "foobarbaz"));
    }

    // --- Only name-shaped literals, and shown as written ---

    [Theory]
    [InlineData("already exists. Proceeding with override")]   // a message
    [InlineData("at origin: ")]                                // a fragment
    [InlineData("player spawned")]                             // a space anywhere
    [InlineData("attacker:")]                                  // punctuation
    public void TextThatIsNotNameShaped_IsNotOffered(string literal)
    {
        Assert.False(HasLabel(
            LiteralsFrom("#namespace ev;\nfunction f()\n{\n    IPrintLn( \"" + literal + "\" );\n}\n"),
            literal));
    }

    [Theory]
    [InlineData("0")]           // plain numbers
    [InlineData("-1")]
    [InlineData("0.25")]
    [InlineData("1000")]
    [InlineData(".")]           // lone punctuation
    [InlineData("/")]
    [InlineData("-")]
    public void NumbersAndPunctuation_AreNotOffered(string literal)
    {
        // Data, not names. Not one of the 2,094 literals in a name position in the stock scripts
        // lacks a letter, so requiring one costs nothing.
        Assert.False(HasLabel(
            LiteralsFrom("#namespace ev;\nfunction f()\n{\n    x = \"" + literal + "\";\n}\n"),
            literal));
    }

    [Theory]
    [InlineData("a")]
    [InlineData("tp")]
    [InlineData("_a")]          // punctuation does not count towards the length
    [InlineData("a.b")]
    public void VeryShortFragments_AreNotOffered(string literal)
    {
        Assert.False(HasLabel(
            LiteralsFrom("#namespace ev;\nfunction f()\n{\n    x = \"" + literal + "\";\n}\n"),
            literal));
    }

    [Theory]
    [InlineData("player_spawned")]
    [InlineData("p7_zm_lab_battery")]
    [InlineData("zombie/spawn_point")]      // asset paths keep their separators
    [InlineData("weapons\\ray_gun")]
    [InlineData("ai_tank.v2")]
    [InlineData("hk416")]                   // two letters, three digits — a real weapon
    [InlineData("m32")]                     // one letter, two digits
    [InlineData("pl1")]
    public void NameShapedLiterals_AreStillOffered(string literal)
    {
        Assert.True(HasLabel(
            LiteralsFrom("#namespace ev;\nfunction f()\n{\n    self notify( \"" + literal + "\" );\n}\n"),
            literal));
    }

    [Fact]
    public void LocalizedStringsKeepTheirCase()
    {
        // The reported bug: KILLSTREAK_COMBAT_ROBOT_CRATE was offered lowercased, because istring
        // keys were interned lowercase to make them match case-insensitively.
        FakeFileSystem files = new FakeFileSystem().AddFile(
            @$"{Raw}\scripts\ui.gsc",
            "#namespace ui;\nfunction f()\n{\n    x = &\"KILLSTREAK_COMBAT_ROBOT_CRATE\";\n}\n");

        (CompletionEngine engine, _, _) = BuildWorld(files);

        // Cursor inside the empty istring on line 3.
        ParseResult result = Analyze(
            @$"{Raw}\scripts\main.gsc", "#namespace game;\nfunction run()\n{\n    x = &\"\";\n}\n");

        ImmutableArray<CompletionEntry> entries = engine.Complete(result, "raw", new Position(3, 10));

        Assert.True(HasLabel(entries, "KILLSTREAK_COMBAT_ROBOT_CRATE"));
        Assert.False(HasLabel(entries, "killstreak_combat_robot_crate"));
    }

    [Fact]
    public void HashStringsKeepTheirCaseToo()
    {
        FakeFileSystem files = new FakeFileSystem().AddFile(
            @$"{Raw}\scripts\ui.gsc", "#namespace ui;\nfunction f()\n{\n    x = #\"Zombie_State\";\n}\n");

        (CompletionEngine engine, _, _) = BuildWorld(files);
        ParseResult result = Analyze(
            @$"{Raw}\scripts\main.gsc", "#namespace game;\nfunction run()\n{\n    x = #\"\";\n}\n");

        Assert.True(HasLabel(engine.Complete(result, "raw", new Position(3, 10)), "Zombie_State"));
    }

    [Fact]
    public void InsideStringLiteral_OffersNothing_WhenLiteralsDisabled()
    {
        FakeFileSystem files = new FakeFileSystem()
            .AddFile(@$"{Raw}\scripts\events.gsc", "#namespace ev;\nfunction fire()\n{\n    self notify( \"player_spawned\" );\n}\n");
        (CompletionEngine engine, _, _) = BuildWorld(files);

        string text = "#namespace game;\nfunction run()\n{\n    x = \"\";\n}\n";
        ParseResult result = Analyze(@$"{Raw}\scripts\main.gsc", text);
        Position insideString = new(3, 9);

        ImmutableArray<CompletionEntry> entries = engine.Complete(result, "raw", insideString, includeLiterals: false);

        Assert.Empty(entries);
    }

    [Fact]
    public void StatementScope_OffersKeywordsMacrosAndBuiltins()
    {
        FakeFileSystem files = new FakeFileSystem().AddFile(@$"{Raw}\scripts\dummy.gsc", "function d()\n{\n}\n");
        (CompletionEngine engine, _, _) = BuildWorld(files);

        string text = "#define CAP 5\nfunction run()\n{\n    \n}\n";
        ParseResult result = Analyze(@$"{Raw}\scripts\main.gsc", text);
        Position inside = new(3, 4);

        ImmutableArray<CompletionEntry> entries = engine.Complete(result, "raw", inside);

        Assert.True(HasLabel(entries, "if"));
        Assert.True(HasLabel(entries, "foreach"));
        Assert.True(HasLabel(entries, "CAP"));       // file-local macro
        Assert.True(HasLabel(entries, "IPrintLn") || entries.Any(e => e.Detail == "builtin"));

        // The parameter pack is NOT offered here: `run()` declares no `...`, so nothing would bind
        // it and accepting the suggestion would earn a 5024.
        Assert.False(HasLabel(entries, "vararg"));
    }

    [Fact]
    public void StatementScope_OffersTheParameterPackOnlyInsideAVarargFunction()
    {
        FakeFileSystem files = new FakeFileSystem().AddFile(@$"{Raw}\scripts\dummy.gsc", "function d()\n{\n}\n");
        (CompletionEngine engine, _, _) = BuildWorld(files);

        // `vararg` is bound by the DECLARATION, not by the dialect alone, so it is offered per
        // function rather than from the keyword list.
        string text = "function run( first, ... )\n{\n    \n}\n";
        ParseResult result = Analyze(@$"{Raw}\scripts\main.gsc", text);

        ImmutableArray<CompletionEntry> entries = engine.Complete(result, "raw", new Position(2, 4));

        CompletionEntry pack = Assert.Single(entries, entry => entry.Label == "vararg");

        // A Variable rather than a Keyword: at a use site it reads as the array it is.
        Assert.Equal(CompletionKind.Variable, pack.Kind);
        Assert.Equal("array", pack.Detail);
        Assert.Contains("...", pack.Documentation, StringComparison.Ordinal);
    }

    // --- Bare-name completion offers imported namespaces too ---
    //
    // `ns::` completion (above) already worked once the namespace was typed by hand. The gap: at
    // statement scope, typing the bare function name did not surface a function reachable through
    // a `#using` — the user had to already know and type the qualifier before anything showed up.

    [Fact]
    public void StatementScope_OffersFunctionsFromAnImportedNamespace()
    {
        FakeFileSystem files = new FakeFileSystem()
            .AddFile(@$"{Raw}\scripts\hud_message.gsc", "#namespace globallogic;\nfunction init()\n{\n}\n");
        (CompletionEngine engine, _, _) = BuildWorld(files);

        string text = "#namespace game;\n#using scripts\\hud_message;\n\nfunction run()\n{\n    \n}\n";
        ParseResult result = Analyze(@$"{Raw}\scripts\main.gsc", text);

        ImmutableArray<CompletionEntry> entries = engine.Complete(result, "raw", new Position(4, 4));

        // Offered under its bare name — that is what was typed — with the qualifier visible in
        // Detail so the two "init"s (this file's own and globallogic's) are told apart.
        // Labelled with the qualifier, which is what makes the namespace findable: the editor
        // filters on the label, so typing "globallogic" surfaces every one of its functions rather
        // than only those whose own name starts that way.
        CompletionEntry entry = entries.First(e => e.Label == "globallogic::init" && e.Kind == CompletionKind.Function);
        Assert.Equal("globallogic", entry.Namespace);

        // The function's OWN name is kept for resolve, which looks documentation up by name and
        // would find nothing under the qualified label.
        Assert.Equal("init", entry.ResolveName);

        // But INSERTED fully qualified: an unqualified call into another namespace does not
        // resolve, so the useful completion is the one that writes the qualifier for you.
        Assert.StartsWith("globallogic::init", entry.InsertText, StringComparison.Ordinal);
    }

    [Fact]
    public void StatementScope_DoesNotOfferFunctionsFromAnUnimportedNamespace()
    {
        // The mirror of the above: nothing gives a function away just for existing somewhere in
        // the workspace. Only what this file has actually `#using`'d belongs in the list.
        FakeFileSystem files = new FakeFileSystem()
            .AddFile(@$"{Raw}\scripts\hud_message.gsc", "#namespace globallogic;\nfunction init()\n{\n}\n");
        (CompletionEngine engine, _, _) = BuildWorld(files);

        string text = "#namespace game;\n\nfunction run()\n{\n    \n}\n";
        ParseResult result = Analyze(@$"{Raw}\scripts\main.gsc", text);

        ImmutableArray<CompletionEntry> entries = engine.Complete(result, "raw", new Position(3, 4));

        Assert.DoesNotContain(entries, e => e.Label is "init" or "globallogic::init");
    }

    [Fact]
    public void StatementScope_OwnNamespaceFunctions_StayUnqualifiedEvenWhenAlsoImported()
    {
        // A file may `#using` another file that shares its OWN namespace (split across files) —
        // that function is still called bare, so it must not also gain a qualified duplicate.
        FakeFileSystem files = new FakeFileSystem()
            .AddFile(@$"{Raw}\scripts\util_part2.gsc", "#namespace util;\nfunction helper()\n{\n}\n");
        (CompletionEngine engine, _, _) = BuildWorld(files);

        string text = "#namespace util;\n#using scripts\\util_part2;\n\nfunction run()\n{\n    \n}\n";
        ParseResult result = Analyze(@$"{Raw}\scripts\main.gsc", text);

        ImmutableArray<CompletionEntry> entries = engine.Complete(result, "raw", new Position(4, 4));

        CompletionEntry entry = Assert.Single(entries, e => e.Label.StartsWith("helper", StringComparison.Ordinal));
        Assert.StartsWith("helper", entry.InsertText, StringComparison.Ordinal);
        Assert.DoesNotContain("::", entry.InsertText, StringComparison.Ordinal);
    }

    [Fact]
    public void StatementScope_OffersTheImportedNamespaceItselfByName()
    {
        // The reported gap: typing the NAMESPACE's name ("util") rather than one of its members
        // ("init") found nothing, because only functions were offered — and most function names
        // share nothing with their namespace's name. The namespace itself has to be a candidate too.
        FakeFileSystem files = new FakeFileSystem()
            .AddFile(@$"{Raw}\scripts\util_shared.gsc", "#namespace util;\nfunction get_players()\n{\n}\n");
        (CompletionEngine engine, _, _) = BuildWorld(files);

        string text = "#namespace game;\n#using scripts\\util_shared;\n\nfunction run()\n{\n    \n}\n";
        ParseResult result = Analyze(@$"{Raw}\scripts\main.gsc", text);

        ImmutableArray<CompletionEntry> entries = engine.Complete(result, "raw", new Position(4, 4));

        CompletionEntry entry = Assert.Single(entries, e => e.Label == "util" && e.Kind == CompletionKind.Namespace);

        // Accepting it inserts the qualifier and reopens the list — the walk-it-down shape a
        // folder in a #using path already uses — so the next keystroke lands exactly where the
        // explicit `ns::` handler already lists util's members.
        Assert.Equal("util::", entry.InsertText);
        Assert.True(entry.RetriggerCompletion);
    }

    [Fact]
    public void StatementScope_DoesNotOfferAnUnimportedNamespaceByName()
    {
        FakeFileSystem files = new FakeFileSystem()
            .AddFile(@$"{Raw}\scripts\util_shared.gsc", "#namespace util;\nfunction get_players()\n{\n}\n");
        (CompletionEngine engine, _, _) = BuildWorld(files);

        string text = "#namespace game;\n\nfunction run()\n{\n    \n}\n";
        ParseResult result = Analyze(@$"{Raw}\scripts\main.gsc", text);

        ImmutableArray<CompletionEntry> entries = engine.Complete(result, "raw", new Position(3, 4));

        Assert.DoesNotContain(entries, e => e.Kind == CompletionKind.Namespace);
    }

    // --- The namespace set comes from the functions, not from the spans ---
    //
    // NamespaceSpan answers a POSITIONAL question ("what namespace is in effect here"), so a file
    // whose imports sit above its `#namespace` line has a leading span for the region before it,
    // named after the file. Reading the span list therefore handed back one phantom namespace per
    // imported file. The two tests below pin the pair of cases that any fix has to get right at
    // once — which is why the fix reads function.Namespace rather than filtering spans.

    [Fact]
    public void StatementScope_DoesNotOfferAPhantomNamespaceNamedAfterTheImportedFile()
    {
        // The reported bug: `#using scripts\shared\util_shared` offered BOTH `util` (real) and
        // `util_shared` (the span governing the import lines above the `#namespace` directive).
        FakeFileSystem files = new FakeFileSystem()
            .AddFile(
                @$"{Raw}\scripts\util_shared.gsc",
                "#using scripts\\other;\n#namespace util;\n\nfunction get_players()\n{\n}\n")
            .AddFile(@$"{Raw}\scripts\other.gsc", "#namespace other;\nfunction thing()\n{\n}\n");

        (CompletionEngine engine, _, _) = BuildWorld(files);

        string text = "#namespace game;\n#using scripts\\util_shared;\n\nfunction run()\n{\n    \n}\n";
        ParseResult result = Analyze(@$"{Raw}\scripts\main.gsc", text);

        ImmutableArray<CompletionEntry> entries = engine.Complete(result, "raw", new Position(4, 4));

        Assert.Contains(entries, e => e.Label == "util" && e.Kind == CompletionKind.Namespace);
        Assert.DoesNotContain(entries, e => e.Label == "util_shared");
    }

    [Fact]
    public void StatementScope_StillOffersTheFileNamedNamespace_WhenTheImportDeclaresNone()
    {
        // The other half, and the reason the phantom cannot simply be filtered out for being
        // implicit: a file with NO `#namespace` at all really does live in the namespace named
        // after it, so struct.gsc must still be offered as `struct`. Only a span governing nothing
        // is a phantom — and asking the functions distinguishes the two for free.
        FakeFileSystem files = new FakeFileSystem()
            .AddFile(@$"{Raw}\scripts\struct.gsc", "function createstruct()\n{\n}\n");

        (CompletionEngine engine, _, _) = BuildWorld(files);

        string text = "#namespace game;\n#using scripts\\struct;\n\nfunction run()\n{\n    \n}\n";
        ParseResult result = Analyze(@$"{Raw}\scripts\main.gsc", text);

        ImmutableArray<CompletionEntry> entries = engine.Complete(result, "raw", new Position(4, 4));

        Assert.Contains(entries, e => e.Label == "struct" && e.Kind == CompletionKind.Namespace);
        Assert.Contains(entries, e => e.Label == "struct::createstruct");
    }

    [Fact]
    public void TypingANamespacePrefix_SurfacesEveryOneOfItsFunctions()
    {
        // The point of labelling with the qualifier. The editor filters on the LABEL, so with bare
        // labels typing "uti" found only `util`'s own name — none of its functions, whose names
        // share nothing with the namespace holding them. Now the whole namespace comes with it.
        FakeFileSystem files = new FakeFileSystem()
            .AddFile(
                @$"{Raw}\scripts\util_shared.gsc",
                "#namespace util;\nfunction get_players()\n{\n}\nfunction wait_endon()\n{\n}\n");

        (CompletionEngine engine, _, _) = BuildWorld(files);

        string text = "#namespace game;\n#using scripts\\util_shared;\n\nfunction run()\n{\n    \n}\n";
        ParseResult result = Analyze(@$"{Raw}\scripts\main.gsc", text);

        ImmutableArray<CompletionEntry> entries = engine.Complete(result, "raw", new Position(4, 4));

        // Everything a client filtering on the typed prefix would keep.
        List<string> matching = [.. entries
            .Where(e => e.Label.StartsWith("uti", StringComparison.OrdinalIgnoreCase))
            .Select(e => e.Label)
            .Order(StringComparer.Ordinal)];

        Assert.Equal(["util", "util::get_players", "util::wait_endon"], matching);
    }

    // --- Parameter names beside the label ---
    //
    // The names alone frequently do not tell two entries apart — `on_agent_generic_damaged` and
    // `on_agent_player_damaged` differ by their arguments and nothing else. The parameters are
    // already in hand when the list is built, so unlike documentation this costs no round trip.

    /// <summary>Completes at an empty statement position in a file importing util.</summary>
    private static ImmutableArray<CompletionEntry> WithHints(bool parameterHints)
    {
        FakeFileSystem files = new FakeFileSystem()
            .AddFile(
                @$"{Raw}\scripts\util.gsc",
                "#namespace util;\nfunction get_players( team, alive )\n{\n}\nfunction now()\n{\n}\n");

        (CompletionEngine engine, _, _) = BuildWorld(files);

        ParseResult result = Analyze(
            @$"{Raw}\scripts\main.gsc",
            "#namespace game;\n#using scripts\\util;\n\nfunction run()\n{\n    \n}\n");

        return engine.Complete(
            result, "raw", new Position(4, 4), parameterHints: parameterHints);
    }

    [Fact]
    public void ParameterNamesRideBesideTheLabel()
    {
        CompletionEntry entry = Assert.Single(WithHints(true), e => e.Label == "util::get_players");

        // Beside it, never in it: the label stays exactly what the editor filters and sorts on.
        Assert.Equal("( team, alive )", entry.LabelDetail);
    }

    [Fact]
    public void ATakesNothingFunctionShowsEmptyParentheses()
    {
        Assert.Contains(WithHints(true), e => e.Label == "util::now" && e.LabelDetail == "()");
    }

    [Fact]
    public void TheLabelIsUntouchedSoFilteringNeverSeesParameterNames()
    {
        // The hazard that keeping them out of the label removes outright: the editor matches what
        // you type against the label, so a folded-in signature would let "team" surface every
        // function that happens to take a parameter of that name.
        CompletionEntry entry = Assert.Single(WithHints(true), e => e.Label == "util::get_players");

        Assert.DoesNotContain("team", entry.Label, StringComparison.Ordinal);
        Assert.StartsWith("util::get_players(", entry.InsertText, StringComparison.Ordinal);
        Assert.DoesNotContain("team", entry.InsertText, StringComparison.Ordinal);

        // Resolve looks documentation up by the function's OWN name, which the qualifier is not.
        Assert.Equal("get_players", entry.ResolveName);
    }

    [Fact]
    public void TheSettingTurnsThemOff()
    {
        Assert.Contains(WithHints(false), e => e.Label == "util::get_players" && e.LabelDetail.Length == 0);
        Assert.DoesNotContain(WithHints(false), e => e.LabelDetail.Length > 0);
    }

    [Fact]
    public void ALongParameterListIsCutShortAtASeparator()
    {
        // The editor truncates an over-long row wherever it happens to reach, and a name cut
        // mid-word reads as though it were the name. Cutting here keeps the row honest.
        FakeFileSystem files = new FakeFileSystem().AddFile(
            @$"{Raw}\scripts\util.gsc",
            "#namespace util;\nfunction many( einflictor, eattacker, idamage, idflags, smeansofdeath, sweapon, vpoint )\n{\n}\n");

        (CompletionEngine engine, _, _) = BuildWorld(files);
        ParseResult result = Analyze(
            @$"{Raw}\scripts\main.gsc",
            "#namespace game;\n#using scripts\\util;\n\nfunction run()\n{\n    \n}\n");

        CompletionEntry entry = Assert.Single(
            engine.Complete(result, "raw", new Position(4, 4)),
            e => e.Label == "util::many");

        Assert.EndsWith("… )", entry.LabelDetail, StringComparison.Ordinal);
        Assert.DoesNotContain("vpoint", entry.LabelDetail, StringComparison.Ordinal);
    }

    [Fact]
    public void BuiltinsCarryTheirParametersToo_MarkingOptionalOnes()
    {
        FakeFileSystem files = new FakeFileSystem().AddFile(@$"{Raw}\scripts\dummy.gsc", "function d()\n{\n}\n");
        (CompletionEngine engine, _, _) = BuiltinWorld(files);

        ParseResult result = Analyze(@$"{Raw}\scripts\main.gsc", "function run()\n{\n    \n}\n");
        ImmutableArray<CompletionEntry> entries = engine.Complete(result, "raw", new Position(2, 4));

        CompletionEntry builtin = entries.First(e => e.Detail == "builtin" && e.LabelDetail.Length > 0);

        // The signature is beside the label, so the label needs no repair: no parentheses in it,
        // and nothing to pin filtering or resolve back to.
        Assert.DoesNotContain("(", builtin.Label, StringComparison.Ordinal);
        Assert.StartsWith("(", builtin.LabelDetail, StringComparison.Ordinal);
        Assert.Empty(builtin.FilterText);
        Assert.Empty(builtin.ResolveName);

        // Optional parameters are marked, matching how signature help renders them.
        Assert.Contains(entries, e => e.Detail == "builtin" && e.LabelDetail.Contains('?', StringComparison.Ordinal));
    }

    private static (CompletionEngine Engine, ScriptDatabase Db, PathResolver Resolver) BuiltinWorld(FakeFileSystem files)
    {
        return BuildWorld(files);
    }

    [Fact]
    public void StatementScope_ImportedNamespaceFunctions_RespectPrivacy()
    {
        FakeFileSystem files = new FakeFileSystem()
            .AddFile(@$"{Raw}\scripts\util.gsc", "#namespace util;\nfunction private hidden()\n{\n}\nfunction shown()\n{\n}\n");
        (CompletionEngine engine, _, _) = BuildWorld(files);

        string text = "#namespace game;\n#using scripts\\util;\n\nfunction run()\n{\n    \n}\n";
        ParseResult result = Analyze(@$"{Raw}\scripts\main.gsc", text);

        ImmutableArray<CompletionEntry> entries = engine.Complete(result, "raw", new Position(4, 4));

        Assert.True(HasLabel(entries, "util::shown"));
        Assert.False(HasLabel(entries, "util::hidden"));
    }

    [Fact]
    public void TopLevel_OffersDeclarationKeywords()
    {
        FakeFileSystem files = new FakeFileSystem().AddFile(@$"{Raw}\scripts\dummy.gsc", "function d()\n{\n}\n");
        (CompletionEngine engine, _, _) = BuildWorld(files);

        string text = "#namespace game;\n\nfunction run()\n{\n}\n";
        ParseResult result = Analyze(@$"{Raw}\scripts\main.gsc", text);
        Position topLevel = new(1, 0);

        ImmutableArray<CompletionEntry> entries = engine.Complete(result, "raw", topLevel);

        Assert.True(HasLabel(entries, "function"));
        Assert.True(HasLabel(entries, "class"));
        Assert.False(HasLabel(entries, "if"));
    }

    // --- Call punctuation ---
    //
    // A completed call brings its parentheses, and closes a STATEMENT with a semicolon. Getting
    // the statement test wrong writes a ';' into the middle of an expression, so the detection is
    // a whitelist of what may precede a call in statement position rather than a blacklist.

    /// <summary>Completes at the end of `line`, inside a function body, and returns `foo`'s entry.</summary>
    private static CompletionEntry CallEntry(string line, CallPunctuation punctuation = CallPunctuation.ParensAndSemicolon)
    {
        FakeFileSystem files = new FakeFileSystem()
            .AddFile(@$"{Raw}\scripts\util.gsc", "#namespace util;\nfunction foo()\n{\n}\n");

        (CompletionEngine engine, _, _) = BuildWorld(files);

        string text = "#namespace util;\nfunction run()\n{\n    " + line + "\n}\n";
        ParseResult result = Analyze(@$"{Raw}\scripts\main.gsc", text);

        ImmutableArray<CompletionEntry> entries = engine.Complete(
            result, "raw", new Position(3, 4 + line.Length), callPunctuation: punctuation);

        // The label carries the parameter list; these tests are about the INSERT text.
        return Assert.Single(
            entries,
            e => e.Kind == CompletionKind.Function
                && (e.Label == "foo" || e.Label.StartsWith("foo(", StringComparison.Ordinal)));
    }

    [Theory]
    [InlineData("")]                    // start of a statement
    [InlineData("self ")]               // a method call on self
    [InlineData("self thread ")]        // threaded
    [InlineData("level.owner ")]        // an arbitrary object expression
    [InlineData("util::")]              // namespace-qualified
    [InlineData("x = ")]                // an assignment completes a statement too
    [InlineData("self.count += ")]      // and a compound one
    [InlineData("things[0] = ")]        // and one through an index
    public void AStatementCallGetsItsSemicolon(string line)
    {
        Assert.EndsWith("($0);", CallEntry(line).InsertText, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("if ( ")]               // a condition
    [InlineData("other( ")]             // an argument
    [InlineData("return ")]             // a returned value
    [InlineData("x = a + ")]            // an operand, not the whole right-hand side
    [InlineData("x = y = ")]            // a second assignment: too unusual to guess at
    public void AnExpressionCallDoesNot(string line)
    {
        // A semicolon here would land in the middle of the expression.
        CompletionEntry entry = CallEntry(line);

        Assert.EndsWith("($0)", entry.InsertText, StringComparison.Ordinal);
        Assert.DoesNotContain(";", entry.InsertText, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("if ( ready ) ")]           // an unbraced if body
    [InlineData("while ( ready ) ")]
    [InlineData("for ( i = 0; i < 3; i++ ) ")]
    [InlineData("foreach ( p in players ) ")]
    [InlineData("else ")]
    [InlineData("things[0] ")]              // a call on an indexed element
    public void AnUnbracedBodyIsAStatementToo(string line)
    {
        // The reported miss: the semicolon vanished on exactly the lines whose body has no
        // braces, because a ')' or an `else` was not in the accepted set.
        Assert.EndsWith("($0);", CallEntry(line).InsertText, StringComparison.Ordinal);
    }

    [Fact]
    public void ACallsClosingParenIsStillAnExpression()
    {
        // The ')' of `get_ready()` must not be mistaken for a control-flow header's, or every
        // chained expression would gain a semicolon.
        Assert.EndsWith("($0)", CallEntry("x = get_ready() + ").InsertText, StringComparison.Ordinal);
    }

    // --- Call-shaped keywords ---
    //
    // The distinction is expression-versus-statement, not keyword-versus-function: `isdefined` is
    // only ever a condition, `notify` is a statement, and `wait` takes no parentheses at all.

    private static CompletionEntry KeywordEntry(
        string line, string keyword, CallPunctuation punctuation = CallPunctuation.ParensAndSemicolon)
    {
        FakeFileSystem files = new FakeFileSystem().AddFile(@$"{Raw}\scripts\dummy.gsc", "function d()\n{\n}\n");
        (CompletionEngine engine, _, _) = BuildWorld(files);

        ParseResult result = Analyze(
            @$"{Raw}\scripts\main.gsc", "function run()\n{\n    " + line + "\n}\n");

        ImmutableArray<CompletionEntry> entries = engine.Complete(
            result, "raw", new Position(2, 4 + line.Length), callPunctuation: punctuation);

        return Assert.Single(entries, e => e.Label == keyword && e.Kind == CompletionKind.Keyword);
    }

    [Theory]
    [InlineData("notify")]
    [InlineData("endon")]
    [InlineData("waittill")]
    public void StatementKeywordsTakeParensAndASemicolon(string keyword)
    {
        Assert.Equal(keyword + "($0);", KeywordEntry("self ", keyword).InsertText);
    }

    [Fact]
    public void IsdefinedFollowsTheSameStatementRule()
    {
        // No special case needed: `x = isdefined( f )` is an assignment STATEMENT and takes a
        // semicolon, while `if ( isdefined( f ) )` is not a statement position and does not.
        Assert.Equal("isdefined($0);", KeywordEntry("x = ", "isdefined").InsertText);
        Assert.Equal("isdefined($0)", KeywordEntry("if ( ", "isdefined").InsertText);
    }

    [Fact]
    public void StatementKeywordsInAnExpressionKeepBareParens()
    {
        Assert.Equal("waittill($0)", KeywordEntry("if ( self ", "waittill").InsertText);
    }

    [Theory]
    [InlineData("wait")]
    [InlineData("waitrealtime")]
    public void WaitIsParenthesised(string keyword)
    {
        // Both `wait 0.5;` and `wait( 0.5 );` are legal; the parenthesised form is used, being
        // the one that reads the same as everything around it.
        Assert.Equal(keyword + "($0);", KeywordEntry("", keyword).InsertText);
    }

    [Theory]
    [InlineData("waittillframeend")]
    [InlineData("break")]
    [InlineData("continue")]
    public void StatementsTakingNoValueBringTheirTerminator(string keyword)
    {
        // Nothing can follow these on the line, so unlike a call their semicolon is never in doubt.
        Assert.Equal(keyword + ";", KeywordEntry("", keyword).InsertText);
    }

    [Fact]
    public void ReturnPutsTheCaretBeforeItsTerminator()
    {
        // `return;` and `return 5;` are both whole statements, so the caret goes before the
        // semicolon: typing nothing leaves the first, typing a value leaves the second, and
        // neither needs a correction afterwards.
        Assert.Equal("return$0;", KeywordEntry("", "return").InsertText);
    }

    [Theory]
    [InlineData("break")]
    [InlineData("continue")]
    [InlineData("return")]
    public void JumpKeywordsRespectPunctuationBeingOff(string keyword)
    {
        // The setting governs these on the same terms as everything else.
        Assert.Equal("", KeywordEntry("", keyword, CallPunctuation.Off).InsertText);
    }

    [Theory]
    [InlineData("else")]
    [InlineData("true")]
    [InlineData("if")]
    public void PlainWordsAreUnchanged(string keyword)
    {
        // Empty insert text means "insert the label". The BRANCHING control-flow keywords are left
        // alone because completing `if` usefully means a body too, which is a different job. The
        // jumps are not in that category and are covered above.
        Assert.Equal("", KeywordEntry("", keyword).InsertText);
    }

    [Fact]
    public void TurningPunctuationOffLeavesKeywordsBare()
    {
        Assert.Equal("", KeywordEntry("self ", "notify", CallPunctuation.Off).InsertText);
    }

    [Fact]
    public void ParensOnlyNeverAddsASemicolon()
    {
        Assert.Equal("foo($0)", CallEntry("self ", CallPunctuation.Parens).InsertText);
    }

    [Fact]
    public void OffInsertsTheBareName()
    {
        Assert.Equal("foo", CallEntry("self ", CallPunctuation.Off).InsertText);
    }

    [Fact]
    public void BuiltinsFollowTheSameRule()
    {
        FakeFileSystem files = new FakeFileSystem().AddFile(@$"{Raw}\scripts\dummy.gsc", "function d()\n{\n}\n");
        (CompletionEngine engine, _, _) = BuildWorld(files);

        ParseResult result = Analyze(@$"{Raw}\scripts\main.gsc", "function run()\n{\n    self \n}\n");
        ImmutableArray<CompletionEntry> entries = engine.Complete(
            result, "raw", new Position(2, 9), callPunctuation: CallPunctuation.ParensAndSemicolon);

        CompletionEntry builtin = entries.First(e => e.Detail == "builtin");
        Assert.EndsWith("($0);", builtin.InsertText, StringComparison.Ordinal);
    }

    // --- Contexts are gated on where the cursor actually is ---
    //
    // Every context is detected by scanning BACKWARD for a trigger character, which answers "what
    // did the user just type" but never "is this construct legal here". The directive family is
    // top-level only, so inside a function body the scan found a '#' and offered #using, #insert
    // and #namespace in the middle of a call.

    /// <summary>Completes at the end of `line`, placed inside a function body.</summary>
    private static ImmutableArray<CompletionEntry> CompleteInsideFunction(string line, FakeFileSystem? files = null)
    {
        files ??= new FakeFileSystem().AddFile(@$"{Raw}\scripts\dummy.gsc", "function d()\n{\n}\n");
        (CompletionEngine engine, _, _) = BuildWorld(files);

        string text = "#namespace game;\nfunction run()\n{\n    " + line + "\n}\n";
        ParseResult result = Analyze(@$"{Raw}\scripts\main.gsc", text);

        return engine.Complete(result, "raw", new Position(3, 4 + line.Length));
    }

    [Theory]
    [InlineData("switch ( x ) { case 1:")]
    [InlineData("switch ( x ) { case \"name\":")]
    [InlineData("switch ( x ) { default:")]
    public void ACaseLabelColonSuggestsNothing(string line)
    {
        // ':' is a completion trigger because of `ns::`, and a lone colon is a different token, so
        // this position fell through to statement scope and popped the whole list over a label the
        // user had just finished typing.
        Assert.Empty(CompleteInsideFunction(line));
    }

    [Theory]
    [InlineData("switch ( x ) { case 1: lev")]
    [InlineData("switch ( x ) { case 1: self not")]
    [InlineData("switch ( x ) { default: g")]
    public void TypingInsideACaseBodyStillSuggests(string line)
    {
        // The trigger token stays the case colon for everything up to the end of the first
        // statement, so suppressing on it alone silenced the whole case body. A word under the
        // cursor means a statement is being written, and that wants the ordinary list.
        Assert.NotEmpty(CompleteInsideFunction(line));
    }

    // --- A lone colon after a name is half of a `::` ---

    [Fact]
    public void AHalfTypedQualifierSuggestsNothing()
    {
        // ':' is the trigger character, so the list opens on the FIRST colon — where the only
        // thing that can legally follow is the second one. Statement scope there is a list of
        // things none of which can be written, popped over a qualifier mid-keystroke.
        Assert.Empty(CompleteInsideFunction("util:"));
    }

    [Fact]
    public void TheSecondColonOpensTheNamespacesList()
    {
        // What the first colon was on the way to. Suppressing the half-typed form must not cost
        // the completed one.
        FakeFileSystem files = new FakeFileSystem()
            .AddFile(@$"{Raw}\scripts\util.gsc", "#namespace util;\nfunction alpha()\n{\n}\n");

        Assert.True(HasLabel(CompleteInsideFunction("util::", files), "alpha"));
    }

    [Theory]
    [InlineData("x = a ? b : ")]    // the spaced ternary, kept out by adjacency
    [InlineData("x = a?b:")]        // and the unspaced one, kept out by the '?' scan
    public void ATernaryColonIsNotAHalfTypedQualifier(string line)
    {
        // `a?b:` has the colon hard against an identifier, exactly like `util:`, so adjacency alone
        // would silence it. The '?' is what tells them apart.
        Assert.NotEmpty(CompleteInsideFunction(line));
    }

    [Fact]
    public void ASpacedColonAfterANameIsNotAQualifier()
    {
        // A qualifier is written hard against its name, so `util :` is not one being typed.
        Assert.NotEmpty(CompleteInsideFunction("x = a ? util : "));
    }

    [Fact]
    public void ATernaryColonStillSuggests()
    {
        // The opposite case, and why every colon is not simply suppressed: `a ? b : <here>` begins
        // an expression and genuinely wants the list.
        Assert.NotEmpty(CompleteInsideFunction("x = a ? b :"));
    }

    [Fact]
    public void ATernaryInsideACaseIsStillATernary()
    {
        // Both are in play in the same statement, so the NEAREST one decides. Matching `case`
        // anywhere behind the cursor would suppress this one wrongly.
        Assert.NotEmpty(CompleteInsideFunction("switch ( x ) { case 1: y = a ? b :"));
    }

    [Theory]
    [InlineData("self notify(#")]
    [InlineData("x = #")]
    [InlineData("#")]
    public void TopLevelDirectivesAreNotOfferedInsideAFunctionBody(string line)
    {
        // The reported bug: `self notify(#` listed all 11 directives. The ones that are top level
        // only stay out; the preprocessor family below is a separate question.
        ImmutableArray<CompletionEntry> entries = CompleteInsideFunction(line);

        Assert.DoesNotContain(entries, e => e.Label is "#using" or "#include" or "#namespace"
            or "#precache" or "#using_animtree" or "#animtree");
    }

    [Theory]
    [InlineData("#")]
    [InlineData("#i")]
    [InlineData("x = #")]
    [InlineData("self notify(#")]
    public void ThePreprocessorFamilyIsOfferedInsideAFunctionBody(string line)
    {
        // The preprocessor walks a flat token stream (Preprocessor.ProcessRange), so #if, #define
        // and #insert are dispatched wherever they appear — a function body included. Treating the
        // '#' there as a hash string and nothing else lost the whole family.
        ImmutableArray<CompletionEntry> entries = CompleteInsideFunction(line);

        foreach ( string directive in new[] { "#if", "#elif", "#else", "#endif", "#define", "#insert" } )
        {
            Assert.Contains(entries, e => e.Label == directive);
        }
    }

    [Fact]
    public void AHashInsideAFunctionBodyOffersHashStringsToo()
    {
        // The other thing a '#' can begin on a dialect that has them. The quotes come with it,
        // since only the '#' has been typed and the cursor is not inside a string yet.
        FakeFileSystem files = new FakeFileSystem()
            .AddFile(@$"{Raw}\scripts\ui.gsc", "#namespace ui;\nfunction f()\n{\n    x = #\"zombie_state\";\n}\n");

        ImmutableArray<CompletionEntry> entries = CompleteInsideFunction("self notify(#", files);

        CompletionEntry hash = Assert.Single(entries, e => e.Label == "zombie_state");
        Assert.Equal("\"zombie_state\"", hash.InsertText);

        // Both readings at once — the directives do not displace the literals.
        Assert.Contains(entries, e => e.Label == "#if");
    }

    [Fact]
    public void PathCompletionIsNotOfferedInsideAFunctionBody()
    {
        // `#using` is top level too, so a stray one on the line must not summon script paths.
        Assert.DoesNotContain(
            CompleteInsideFunction(@"#using scripts\"),
            e => e.Kind is CompletionKind.PathSegment or CompletionKind.PathFile);
    }

    [Fact]
    public void AssetTypesAreNotOfferedInsideAFunctionBody()
    {
        Assert.DoesNotContain(
            CompleteInsideFunction("#precache("),
            e => e.Kind == CompletionKind.AssetType);
    }

    [Fact]
    public void ExpressionContextsStillWorkInsideAFunctionBody()
    {
        // The guard covers the three TOP-LEVEL contexts only; `ns::` and `owner.` are legal
        // wherever an expression is and must be untouched by it.
        FakeFileSystem files = new FakeFileSystem()
            .AddFile(@$"{Raw}\scripts\util.gsc", "#namespace util;\nfunction alpha()\n{\n}\n");

        Assert.True(HasLabel(CompleteInsideFunction("util::", files), "alpha"));
    }

    [Fact]
    public void DirectivesAreStillOfferedAtTopLevel()
    {
        // The guard must not silence the context it was protecting.
        Assert.True(HasLabel(CompleteAfter("#"), "#using"));
    }

    // --- Class visibility ---
    //
    // Classes are named without a namespace qualifier, so there is nothing to narrow them by
    // except the file's imports. Offering every class in the workspace meant typing "anim"
    // suggested AnimationAdjustmentInfoXY from a file the caller never #using'd.

    private const string ClassFile = "#namespace vehicles;\nclass cVehicle\n{\n}\n";

    /// <summary>Completes inside a function body in main.gsc, given its full text.</summary>
    private static ImmutableArray<CompletionEntry> CompleteInMain(string text, Position position)
    {
        FakeFileSystem files = new FakeFileSystem()
            .AddFile(@$"{Raw}\scripts\vehicles.gsc", ClassFile);

        (CompletionEngine engine, _, _) = BuildWorld(files);

        return engine.Complete(Analyze(@$"{Raw}\scripts\main.gsc", text), "raw", position);
    }

    [Fact]
    public void ClassInAnUnimportedFile_IsNotOffered()
    {
        // The reported bug.
        ImmutableArray<CompletionEntry> entries = CompleteInMain("function run()\n{\n    \n}\n", new Position(2, 4));

        Assert.False(HasLabel(entries, "cVehicle"));
    }

    [Fact]
    public void ClassInAnImportedFile_IsOffered()
    {
        string text = "#using scripts\\vehicles;\n\nfunction run()\n{\n    \n}\n";

        Assert.True(HasLabel(CompleteInMain(text, new Position(4, 4)), "cVehicle"));
    }

    [Fact]
    public void ClassDeclaredInThisFile_IsOfferedWithoutAnImport()
    {
        // From the live extraction, so it completes before the record is reindexed.
        string text = "class cLocal\n{\n}\n\nfunction run()\n{\n    \n}\n";

        Assert.True(HasLabel(CompleteInMain(text, new Position(6, 4)), "cLocal"));
    }

    [Fact]
    public void ImportedClass_IsOfferedOnlyOnce()
    {
        // The file's own extraction and the store both contribute; the union must dedupe.
        string text = "#using scripts\\vehicles;\n\nfunction run()\n{\n    \n}\n";
        ImmutableArray<CompletionEntry> entries = CompleteInMain(text, new Position(4, 4));

        Assert.Single(entries, e => e.Label == "cVehicle");
    }

    // --- Directives ---
    //
    // The client's word pattern excludes '#', so once it is typed the editor's current word is
    // only the letters after it. Labels therefore keep the '#' (for readability) while filtering
    // and insertion drop it — otherwise "#p" filters out "#precache" and leaves "private".

    private static CompletionEntry Entry(ImmutableArray<CompletionEntry> entries, string label)
    {
        return Assert.Single(entries, e => e.Label == label);
    }

    /// <summary>Completes at the end of `line`, placed on its own line above a function.</summary>
    private static ImmutableArray<CompletionEntry> CompleteAfter(string line)
    {
        FakeFileSystem files = new FakeFileSystem().AddFile(@$"{Raw}\scripts\dummy.gsc", "function d()\n{\n}\n");
        (CompletionEngine engine, _, _) = BuildWorld(files);

        ParseResult result = Analyze(@$"{Raw}\scripts\main.gsc", line + "\n\nfunction run()\n{\n}\n");

        return engine.Complete(result, "raw", new Position(0, line.Length));
    }

    [Theory]
    [InlineData("function ")]
    [InlineData("function private ")]
    [InlineData("function autoexec ")]
    public void AfterTheFunctionKeywordOnlyDeclarableThingsAreOffered(string line)
    {
        // A declaration takes a NAME, so nothing callable can follow the keyword. The list used to
        // be everything statement scope offers: builtins, macros, globals, control-flow keywords.
        ImmutableArray<CompletionEntry> entries = CompleteAfter(line);

        // Only the two modifiers and declared script functions survive. Asserting the SHAPE rather
        // than naming absent builtins keeps this from depending on what the API data happens to
        // hold, which is the thing most likely to change underneath it.
        Assert.All(entries, entry => Assert.True(
            entry.Kind is CompletionKind.Keyword or CompletionKind.Function,
            $"{entry.Label} ({entry.Kind}) cannot follow the function keyword"));

        Assert.DoesNotContain(entries, e => e.Label is "if" or "return" or "level" or "self");
    }

    [Fact]
    public void TopLevelOffersAWholeFunctionDeclaration()
    {
        // The punctuation is identical every time — parentheses, braces, brace on its own line —
        // so the snippet writes it and leaves the caret on the name, the only part that varies.
        CompletionEntry snippet = Entry(CompleteAfter(""), "function");

        Assert.Equal("function ${1:name}()\n{\n\t$0\n}", snippet.InsertText);
    }

    [Fact]
    public void TheDeclarationSnippetFollowsTheFormattersLayout()
    {
        // Allman and a tab, measured at 51,048 Allman against 37 same-line across the stock
        // scripts. A snippet that had to be reformatted the moment it landed would be odd to ship.
        CompletionEntry snippet = Entry(CompleteAfter(""), "function");

        Assert.Contains("()\n{\n", snippet.InsertText, StringComparison.Ordinal);
        Assert.Contains("\n\t$0", snippet.InsertText, StringComparison.Ordinal);
    }

    [Fact]
    public void AfterTheFunctionKeywordTheModifiersAreOffered()
    {
        // `function private foo()` is how a private declaration is written, so these belong here
        // even though nothing else does.
        ImmutableArray<CompletionEntry> entries = CompleteAfter("function ");

        Assert.Contains(entries, e => e.Label == "private");
        Assert.Contains(entries, e => e.Label == "autoexec");
    }

    [Fact]
    public void AfterTheFunctionKeywordExistingScriptFunctionsAreOffered()
    {
        // Worth seeing: an override has to land on the right name, and a collision is better
        // spotted before it is written than after.
        FakeFileSystem files = new FakeFileSystem().AddFile(@$"{Raw}\scripts\dummy.gsc", "function d()\n{\n}\n");
        (CompletionEngine engine, _, _) = BuildWorld(files);

        ParseResult result = Analyze(
            @$"{Raw}\scripts\main.gsc", "#namespace game;\nfunction alreadyHere()\n{\n}\nfunction ");

        ImmutableArray<CompletionEntry> entries = engine.Complete(result, "raw", new Position(4, 9));

        Assert.Contains(entries, e => e.Label == "alreadyHere");
    }

    [Fact]
    public void PartialDirective_OffersDirectivesFilteredWithoutTheHash()
    {
        // The reported bug: "#p" showed `private` and not `#precache`.
        CompletionEntry precache = Entry(CompleteAfter("#p"), "#precache");

        // Filtering drops the '#' (the editor's word does too); the label keeps it.
        Assert.Equal("precache", precache.FilterText);
        Assert.StartsWith("precache", precache.InsertText, StringComparison.Ordinal);
        Assert.DoesNotContain("#", precache.InsertText, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("#precache", "precache( \"$1\", \"$2\" );$0")]
    [InlineData("#using_animtree", "using_animtree( \"$1\" );$0")]
    [InlineData("#using", @"using scripts\\$1;$0")]
    [InlineData("#insert", @"insert scripts\\$1;$0")]
    [InlineData("#namespace", "namespace $1;$0")]
    public void AcceptingADirective_InsertsItsWholeForm(string directive, string expected)
    {
        // Inserting the bare word left something that does not parse until the parentheses and
        // semicolon are typed by hand, and a half-written directive reddens every line below it.
        Assert.Equal(expected, Entry(CompleteAfter("#"), directive).InsertText);
    }

    [Theory]
    [InlineData("#precache")]   // asset types
    [InlineData("#using")]      // script paths
    [InlineData("#insert")]     // script paths
    public void DirectivesWithAVocabulary_ReopenTheSuggestionList(string directive)
    {
        // The snippet lands the cursor between the quotes of the first argument, but nothing
        // reopens the list there — the user had to delete the quotes and retype one just to fire
        // the '"' trigger character again.
        Assert.True(Entry(CompleteAfter("#"), directive).RetriggerCompletion);
    }

    [Theory]
    [InlineData("#define")]     // the macro name is being invented
    [InlineData("#namespace")]  // so is the namespace
    [InlineData("#endif")]      // takes no argument at all
    public void DirectivesWithoutAVocabulary_DoNotReopenIt(string directive)
    {
        // Popping a list over a name the user is typing would be worse than not popping one.
        Assert.False(Entry(CompleteAfter("#"), directive).RetriggerCompletion);
    }

    // --- #using / #insert path completion ---
    //
    // One segment at a time, like a folder picker. Whole relative paths did not work: the
    // client's word pattern excludes '\', so at `scripts\mp\` the editor's current word is empty
    // and it cannot filter `scripts\mp\_arena` against anything typed — the list stayed
    // unfiltered and highlighted whatever came first.

    private static readonly FakeFileSystem PathWorld = new FakeFileSystem()
        .AddFile(@$"{Raw}\scripts\mp\_arena.gsc", "function a()\n{\n}\n")
        .AddFile(@$"{Raw}\scripts\mp\_armor.gsc", "function b()\n{\n}\n")
        .AddFile(@$"{Raw}\scripts\mp\gametypes\tdm.gsc", "function c()\n{\n}\n")
        .AddFile(@$"{Raw}\scripts\codescripts\struct.gsc", "function d()\n{\n}\n")
        .AddFile(@$"{Raw}\scripts\mp\_arena.csc", "function e()\n{\n}\n")
        .AddFile(@$"{Raw}\scripts\shared\shared.gsh", "#define X 1\n")
        .AddFile(@$"{Raw}\scripts\mp\mp.gsh", "#define Y 2\n");

    /// <summary>Completes at the end of a directive line in a file of the given extension.</summary>
    private static ImmutableArray<CompletionEntry> CompletePath(string line, string extension = "gsc")
    {
        (CompletionEngine engine, _, _) = BuildWorld(PathWorld);
        ParseResult result = Analyze(@$"{Raw}\scripts\main.{extension}", line + "\n\nfunction run()\n{\n}\n");

        return engine.Complete(result, "raw", new Position(0, line.Length));
    }

    [Fact]
    public void PathCompletion_StartsAtTheTopLevel()
    {
        ImmutableArray<CompletionEntry> entries = CompletePath("#using ");

        // One entry, "scripts", not every path in the workspace.
        Assert.Equal("scripts", Assert.Single(entries).Label);
    }

    [Fact]
    public void PathCompletion_DescendsOneSegmentAtATime()
    {
        ImmutableArray<CompletionEntry> entries = CompletePath(@"#using scripts\");

        Assert.True(HasLabel(entries, "mp"));
        Assert.True(HasLabel(entries, "codescripts"));
        Assert.False(HasLabel(entries, @"scripts\mp"));   // never a whole path
    }

    [Fact]
    public void PathCompletion_OffersFilesAndFoldersAtTheSameLevel()
    {
        ImmutableArray<CompletionEntry> entries = CompletePath(@"#using scripts\mp\");

        Assert.Equal(CompletionKind.PathFile, Entry(entries, "_arena").Kind);
        Assert.Equal(CompletionKind.PathSegment, Entry(entries, "gametypes").Kind);
    }

    [Fact]
    public void FoldersInsertASeparatorAndReopenTheList()
    {
        // So a path is walked down rather than typed out.
        CompletionEntry folder = Entry(CompletePath(@"#using scripts\"), "mp");

        Assert.Equal(@"mp\", folder.InsertText);
        Assert.True(folder.RetriggerCompletion);
    }

    [Fact]
    public void FilesInsertBareAndDoNotReopen()
    {
        CompletionEntry file = Entry(CompletePath(@"#using scripts\mp\"), "_arena");

        Assert.Equal("_arena", file.InsertText);
        Assert.False(file.RetriggerCompletion);
    }

    [Fact]
    public void APartiallyTypedSegmentDoesNotNarrowTheList()
    {
        // The editor filters on the partial word itself, so narrowing here as well would fight
        // its fuzzy matching. Both _arena and _armor must still be offered.
        ImmutableArray<CompletionEntry> entries = CompletePath(@"#using scripts\mp\_ar");

        Assert.True(HasLabel(entries, "_arena"));
        Assert.True(HasLabel(entries, "_armor"));
    }

    [Fact]
    public void Insert_OffersHeadersOnly()
    {
        // Headers live in the shared GSH store rather than either language store, so serving both
        // directives from one store offered #insert the .gsc files it can never include.
        ImmutableArray<CompletionEntry> entries = CompletePath(@"#insert scripts\shared\");

        // With the .gsh, because #insert writes the extension and #using does not.
        Assert.Equal("shared.gsh", Assert.Single(entries).Label);
    }

    [Fact]
    public void Insert_KeepsTheExtension_WhileUsingDropsIt()
    {
        // An asymmetry of the language, not of this code, and unanimous across the stock
        // scripts: all 2,137 #inserts end in .gsh and all 7,738 #usings are bare.
        Assert.Equal("shared.gsh", Entry(CompletePath(@"#insert scripts\shared\"), "shared.gsh").InsertText);
        Assert.Equal("_arena", Entry(CompletePath(@"#using scripts\mp\"), "_arena").InsertText);
    }

    /// <summary>
    /// The text an editor puts in the buffer for a snippet: '\' escapes the next character, and
    /// $0/$1/… are tab stops that contribute nothing.
    ///
    /// Comparing raw snippet strings is what let the escaping bug through — the assertion simply
    /// restated whatever the code produced. Decoding first tests the thing that matters.
    /// </summary>
    private static string ExpandSnippet(string snippet)
    {
        System.Text.StringBuilder buffer = new();

        for ( int index = 0; index < snippet.Length; index++ )
        {
            char c = snippet[index];

            if ( c == '\\' && index + 1 < snippet.Length )
            {
                buffer.Append(snippet[index + 1]);
                index++;
                continue;
            }

            if ( c == '$' && index + 1 < snippet.Length && char.IsAsciiDigit(snippet[index + 1]) )
            {
                while ( index + 1 < snippet.Length && char.IsAsciiDigit(snippet[index + 1]) )
                {
                    index++;
                }

                continue;
            }

            buffer.Append(c);
        }

        return buffer.ToString();
    }

    [Theory]
    [InlineData("#using", @"using scripts\;")]
    [InlineData("#insert", @"insert scripts\;")]
    public void PathDirectives_PreFillTheScriptsRoot(string directive, string expected)
    {
        // Every one of the 9,875 path directives in the stock scripts starts at `scripts\`, so
        // typing it is pure ceremony. Asserted through the expander because the separator has to
        // survive snippet escaping: unescaped, '\' swallowed the tab stop after it and the buffer
        // read `#using scripts$1;`.
        Assert.Equal(expected, ExpandSnippet(Entry(CompleteAfter("#"), directive).InsertText));
    }

    [Fact]
    public void PrecacheSnippet_ExpandsToItsRealForm()
    {
        Assert.Equal("precache( \"\", \"\" );", ExpandSnippet(Entry(CompleteAfter("#"), "#precache").InsertText));
    }

    [Fact]
    public void ThePreFilledRootLandsOnANonEmptyList()
    {
        // Where the snippet leaves the cursor must be a folder that actually has contents,
        // otherwise the retrigger opens an empty list.
        Assert.NotEmpty(CompletePath(@"#using scripts\"));
        Assert.NotEmpty(CompletePath(@"#insert scripts\"));
    }

    [Fact]
    public void Insert_DoesNotOfferScripts()
    {
        Assert.False(HasLabel(CompletePath(@"#insert scripts\mp\"), "_arena"));
    }

    [Fact]
    public void Using_DoesNotOfferHeaders()
    {
        // The mirror of the above: a #using takes a script, never a header.
        Assert.False(HasLabel(CompletePath(@"#using scripts\mp\"), "mp"));
    }

    [Fact]
    public void Using_StaysInTheAskingFilesLanguage()
    {
        // _arena exists as both .gsc and .csc; a .csc must not be offered a .gsc path, and the
        // segment names are identical, so this is really asserting the store choice.
        Assert.True(HasLabel(CompletePath(@"#using scripts\mp\", extension: "csc"), "_arena"));
        Assert.False(HasLabel(CompletePath(@"#using scripts\mp\", extension: "csc"), "_armor"));
    }

    [Fact]
    public void DefineTakesNoSemicolon()
    {
        // A #define runs to the end of the line rather than to a terminator.
        Assert.DoesNotContain(";", Entry(CompleteAfter("#"), "#define").InsertText, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("#if")]
    [InlineData("#else")]
    [InlineData("#endif")]
    public void ConditionalDirectivesAreInsertedPlain(string directive)
    {
        // These take no punctuation, so a snippet would only add noise to undo.
        Assert.Equal(directive[1..], Entry(CompleteAfter("#"), directive).InsertText);
    }

    // --- Asset types inside #precache's first argument ---

    /// <summary>Completes with the cursor between the quotes of the given line.</summary>
    private static ImmutableArray<CompletionEntry> CompleteInsideQuotes(string line, int quoteIndex)
    {
        FakeFileSystem files = new FakeFileSystem()
            .AddFile(@$"{Raw}\scripts\dummy.gsc", "function d()\n{\n    s = \"some free text\";\n}\n");

        (CompletionEngine engine, _, _) = BuildWorld(files);
        ParseResult result = Analyze(@$"{Raw}\scripts\main.gsc", line + "\n\nfunction run()\n{\n}\n");

        return engine.Complete(result, "raw", new Position(0, quoteIndex + 1));
    }

    [Fact]
    public void FirstPrecacheArgument_OffersAssetTypes()
    {
        // The reported bug. '"' is a completion trigger, so by the time this fires the cursor is
        // already inside a string token and generic literal completion used to win — offering
        // every string in the workspace where a closed vocabulary belongs.
        ImmutableArray<CompletionEntry> entries = CompleteInsideQuotes("#precache( \"\" );", 11);

        Assert.True(HasLabel(entries, "model"));
        Assert.True(HasLabel(entries, "xmodel"));
        Assert.All(entries, e => Assert.Equal(CompletionKind.AssetType, e.Kind));
    }

    [Fact]
    public void AssetTypesInsideQuotes_AreInsertedWithoutQuotes()
    {
        // The cursor is already between quotes, so inserting them again yields ""model"".
        ImmutableArray<CompletionEntry> entries = CompleteInsideQuotes("#precache( \"\" );", 11);

        Assert.Equal("model", Entry(entries, "model").InsertText);
    }

    [Fact]
    public void SecondPrecacheArgument_IsNotAnAssetType()
    {
        // That slot is the asset's own name — free text with no vocabulary to offer.
        ImmutableArray<CompletionEntry> entries = CompleteInsideQuotes("#precache( \"model\", \"\" );", 20);

        Assert.DoesNotContain(entries, e => e.Kind == CompletionKind.AssetType);
    }

    [Fact]
    public void AStringOutsidePrecache_StillOffersLiterals()
    {
        // The asset-type check must not swallow ordinary literal completion.
        ImmutableArray<CompletionEntry> entries = CompleteInsideQuotes("#namespace game;", 15);

        Assert.DoesNotContain(entries, e => e.Kind == CompletionKind.AssetType);
    }

    [Theory]
    [InlineData("#p")]
    [InlineData("#pr")]
    [InlineData("#pre")]
    [InlineData("#preca")]
    [InlineData("#precache")]
    public void PartialDirective_WorksAtEveryPrefixLength(string typed)
    {
        // The lexer emits a half-typed directive as a single Error token and a bare '#' as Hash,
        // so detection walks characters instead — which must hold at every length.
        Assert.True(HasLabel(CompleteAfter(typed), "#precache"));
    }

    [Fact]
    public void PartialDirective_DoesNotOfferPlainKeywords()
    {
        // A '#' has been typed, so `private` cannot be what is meant.
        ImmutableArray<CompletionEntry> entries = CompleteAfter("#p");

        Assert.False(HasLabel(entries, "private"));
        Assert.False(HasLabel(entries, "function"));
    }

    [Fact]
    public void BareHash_OffersEveryDirective()
    {
        ImmutableArray<CompletionEntry> entries = CompleteAfter("#");

        Assert.All(entries, e => Assert.StartsWith("#", e.Label));
        Assert.True(HasLabel(entries, "#using"));
        Assert.True(HasLabel(entries, "#namespace"));
    }

    [Theory]
    [InlineData("#animtree")]
    [InlineData("#if")]
    [InlineData("#elif")]
    [InlineData("#else")]
    [InlineData("#endif")]
    public void PreviouslyUnofferedDirectives_AreNowOffered(string directive)
    {
        // These five are documented in KeywordDocs and hover on them, but were absent from
        // TopLevelKeywords, so they could never be completed.
        Assert.True(HasLabel(CompleteAfter("#"), directive));
    }

    [Fact]
    public void TopLevelWithoutAHash_StillOffersPlainKeywordsWithTheirHashesIntact()
    {
        // The directive path must not leak into ordinary top-level completion: entries there are
        // inserted verbatim, so "#using" must still carry its '#'.
        ImmutableArray<CompletionEntry> entries = CompleteAfter("");

        Assert.True(HasLabel(entries, "function"));
        Assert.Equal("", Entry(entries, "#using").FilterText);
    }

    [Fact]
    public void HashInsideAStringLiteral_IsNotADirectiveContext()
    {
        // Literal completion owns this position; a '#' inside quotes is just a character.
        FakeFileSystem files = new FakeFileSystem().AddFile(@$"{Raw}\scripts\dummy.gsc", "function d()\n{\n}\n");
        (CompletionEngine engine, _, _) = BuildWorld(files);

        string text = "function run()\n{\n    x = \"#p\";\n}\n";
        ParseResult result = Analyze(@$"{Raw}\scripts\main.gsc", text);

        ImmutableArray<CompletionEntry> entries = engine.Complete(result, "raw", new Position(2, 12));

        Assert.False(HasLabel(entries, "#precache"));
    }

    [Fact]
    public void MemberAccess_OffersFieldsAndSize()
    {
        FakeFileSystem files = new FakeFileSystem().AddFile(@$"{Raw}\scripts\dummy.gsc", "function d()\n{\n}\n");
        (CompletionEngine engine, _, _) = BuildWorld(files);

        string text = "function run()\n{\n    self.health = 1;\n    x = self.\n}\n";
        ParseResult result = Analyze(@$"{Raw}\scripts\main.gsc", text);
        Position afterDot = new(3, 13); // just past "self."

        ImmutableArray<CompletionEntry> entries = engine.Complete(result, "raw", afterDot);

        Assert.True(HasLabel(entries, "health"));
        Assert.True(HasLabel(entries, "size"));
    }

    [Fact]
    public void MemberAccess_ScopesAssignedFieldsToTheOwner()
    {
        FakeFileSystem files = new FakeFileSystem().AddFile(@$"{Raw}\scripts\dummy.gsc", "function d()\n{\n}\n");
        (CompletionEngine engine, _, _) = BuildWorld(files);

        string text = "function run()\n{\n    level.round_number = 1;\n    self.player_score = 0;\n    x = level.\n}\n";
        ParseResult result = Analyze(@$"{Raw}\scripts\main.gsc", text);
        Position afterLevelDot = new(4, 14); // just past "level."

        ImmutableArray<CompletionEntry> entries = engine.Complete(result, "raw", afterLevelDot);

        Assert.True(HasLabel(entries, "round_number"));
        Assert.False(HasLabel(entries, "player_score"));
    }

    [Fact]
    public void MemberAccess_AllScope_OffersFieldsFromEveryOwner()
    {
        FakeFileSystem files = new FakeFileSystem().AddFile(@$"{Raw}\scripts\dummy.gsc", "function d()\n{\n}\n");
        (CompletionEngine engine, _, _) = BuildWorld(files);

        string text = "function run()\n{\n    level.round_number = 1;\n    self.player_score = 0;\n    x = level.\n}\n";
        ParseResult result = Analyze(@$"{Raw}\scripts\main.gsc", text);

        ImmutableArray<CompletionEntry> entries = engine.Complete(
            result, "raw", new Position(4, 14), includeLiterals: true, fieldScope: FieldScope.All);

        Assert.True(HasLabel(entries, "round_number"));
        Assert.True(HasLabel(entries, "player_score"));
    }

    [Fact]
    public void MemberAccess_AggregatesOwnerFieldsAcrossFiles()
    {
        // The GlobalObjectOwners scenario: a field assigned on `level` in one file is offered
        // when completing `level.` in another.
        FakeFileSystem files = new FakeFileSystem()
            .AddFile(@$"{Raw}\scripts\other.gsc", "function setup()\n{\n    level.spawned_from_elsewhere = 1;\n}\n");

        (CompletionEngine engine, _, _) = BuildWorld(files);

        string text = "function run()\n{\n    x = level.\n}\n";
        ParseResult result = Analyze(@$"{Raw}\scripts\main.gsc", text);

        ImmutableArray<CompletionEntry> entries = engine.Complete(result, "raw", new Position(2, 14));

        Assert.True(HasLabel(entries, "spawned_from_elsewhere"));
    }

    [Fact]
    public void MemberAccess_UnknownOwner_WidensRatherThanNarrows()
    {
        // `players[0].` has no owner name to scope by; offering nothing would be worse than
        // offering everything, so the scope quietly widens.
        FakeFileSystem files = new FakeFileSystem().AddFile(@$"{Raw}\scripts\dummy.gsc", "function d()\n{\n}\n");
        (CompletionEngine engine, _, _) = BuildWorld(files);

        string text = "function run()\n{\n    self.player_score = 0;\n    x = players[0].\n}\n";
        ParseResult result = Analyze(@$"{Raw}\scripts\main.gsc", text);

        ImmutableArray<CompletionEntry> entries = engine.Complete(result, "raw", new Position(3, 19));

        Assert.True(HasLabel(entries, "player_score"));
    }

    [Fact]
    public void MemberAccess_OffersEngineFieldsAndRadiantMapKeys()
    {
        FakeFileSystem files = new FakeFileSystem().AddFile(@$"{Raw}\scripts\dummy.gsc", "function d()\n{\n}\n");
        (CompletionEngine engine, _, _) = BuildWorld(files);

        string text = "function run()\n{\n    x = self.\n}\n";
        ParseResult result = Analyze(@$"{Raw}\scripts\main.gsc", text);
        Position afterDot = new(2, 13); // just past "self."

        ImmutableArray<CompletionEntry> entries = engine.Complete(result, "raw", afterDot);

        // An engine object field.
        Assert.True(HasLabel(entries, "origin"));

        // "ambient" exists only as a radiant KVP, so it proves the map keys reach completion,
        // and its keys.txt comment becomes the item's documentation.
        CompletionEntry ambient = entries.First(e => e.Label == "ambient");
        Assert.Equal(CompletionKind.Field, ambient.Kind);
        Assert.Contains("map key", ambient.Detail);
        Assert.NotEmpty(ambient.Documentation);
    }

    [Fact]
    public void MemberAccess_KeepsRadiantDocumentation_WhenANameIsAlsoAnEngineField()
    {
        FakeFileSystem files = new FakeFileSystem().AddFile(@$"{Raw}\scripts\dummy.gsc", "function d()\n{\n}\n");
        (CompletionEngine engine, _, _) = BuildWorld(files);

        string text = "function run()\n{\n    x = self.\n}\n";
        ParseResult result = Analyze(@$"{Raw}\scripts\main.gsc", text);

        ImmutableArray<CompletionEntry> entries = engine.Complete(result, "raw", new Position(2, 13));

        // script_noteworthy is both an engine field and a documented radiant key; the single
        // de-duplicated entry must still carry the key's comment.
        CompletionEntry noteworthy = entries.First(e => e.Label == "script_noteworthy");
        Assert.NotEmpty(noteworthy.Documentation);
    }

    [Fact]
    public void PrecacheArgument_OffersAssetTypes()
    {
        FakeFileSystem files = new FakeFileSystem().AddFile(@$"{Raw}\scripts\dummy.gsc", "function d()\n{\n}\n");
        (CompletionEngine engine, _, _) = BuildWorld(files);

        string text = "#precache( \n";
        ParseResult result = Analyze(@$"{Raw}\scripts\main.gsc", text);
        Position afterParen = new(0, 11);

        ImmutableArray<CompletionEntry> entries = engine.Complete(result, "raw", afterParen);

        Assert.Contains(entries, e => e.Kind == CompletionKind.AssetType && e.Label == "model");
        Assert.Contains(entries, e => e.Kind == CompletionKind.AssetType && e.Label == "string");
    }
}
