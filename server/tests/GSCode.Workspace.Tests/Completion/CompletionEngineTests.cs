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
        RootConfig config = RootConfig.Create(true, null, null, @"C:\bo3", [], files);
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

    private static bool HasLabel(ImmutableArray<CompletionEntry> entries, string label)
    {
        return entries.Any(e => string.Equals(e.Label, label, StringComparison.Ordinal));
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
