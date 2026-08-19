using System.Collections.Immutable;
using GSCode.Core;
using GSCode.Core.Text;
using GSCode.Parser;
using GSCode.Parser.Lexing;
using GSCode.Parser.Preprocessing;
using GSCode.Workspace.Api;
using GSCode.Workspace.Completion;
using GSCode.Workspace.Database;
using GSCode.Workspace.Indexing;
using GSCode.Workspace.Resolution;
using GSCode.Workspace.Tests.Resolution;
using Xunit;

namespace GSCode.Workspace.Tests.Completion;

/// <summary>
/// The names bound inside the function being edited — its parameters and its locals — and the
/// macros an <c>#insert</c>ed header supplies.
///
/// None of the three was ever offered. Statement scope was built entirely from workspace-wide
/// lists, so the variable three lines up was the one thing completion could not produce, and the
/// macro loop asked for <c>SourceFile is null</c> — the root file only — which is the opposite of
/// where a shared constant lives.
///
/// These are regression tests in the strict sense: each asserts a name that a real edit typed and
/// the list did not contain.
/// </summary>
public class LocalScopeCompletionTests
{
    private const string Raw = @"C:\bo3\share\raw";
    private static string ApiDirectory => Path.Combine(AppContext.BaseDirectory, "Api");

    /// <summary>Serves one header's text to <c>#insert</c>, so a macro can come from outside the file.</summary>
    private sealed class FakeInserts : IInsertProvider
    {
        private readonly Dictionary<string, InsertedFile> _files = new(StringComparer.OrdinalIgnoreCase);

        public FakeInserts Add(string rawPath, string content)
        {
            SourceText text = SourceText.From(content);
            _files[rawPath] = new InsertedFile(rawPath.ToLowerInvariant(), text, Lexer.Lex(text).Tokens);
            return this;
        }

        public bool TryGetInsert(string rawInsertPath, out InsertedFile inserted)
        {
            return _files.TryGetValue(rawInsertPath, out inserted!);
        }

        public bool TryResolveInsertPath(string rawInsertPath, out string resolvedPath)
        {
            if ( _files.TryGetValue(rawInsertPath, out InsertedFile? file) )
            {
                resolvedPath = file.Path;
                return true;
            }

            resolvedPath = "";
            return false;
        }
    }

    private static CompletionEngine BuildWorld(FakeFileSystem files)
    {
        RootConfig config = RootConfig.Create(true, Raw, @"C:\bo3\mods", [], files);
        PathResolver resolver = new(config, files);
        ScriptDatabase database = new();
        WorkspaceIndexer indexer = new(database, () => resolver, files, new NameTable());
        indexer.IndexAsync(IndexingMode.Partial, NullIndexProgressListener.Instance, CancellationToken.None)
            .GetAwaiter().GetResult();

        return new CompletionEngine(database, BuiltinApiSet.Load(ApiDirectory), ObjectFields.Load(ApiDirectory));
    }

    /// <param name="profile">
    /// The dialect to PARSE with, which has to be the one completion is then asked for: a merge
    /// dialect's `main( x ) { }` is not a declaration under BO3's grammar, so a mismatch here would
    /// leave extraction with no function and the test measuring the mismatch rather than the scope.
    /// </param>
    private static ParseResult Analyze(
        string path, string text, IInsertProvider? inserts = null, GameProfile? profile = null)
    {
        return ScriptAnalysis.Analyze(
            path,
            ScriptAnalysis.LanguageFromPath(path),
            SourceText.From(text),
            inserts ?? NullInsertProvider.Instance,
            new NameTable(),
            profile);
    }

    private static CompletionEntry? Entry(ImmutableArray<CompletionEntry> entries, string label)
    {
        return entries.FirstOrDefault(e => string.Equals(e.Label, label, StringComparison.Ordinal));
    }

    private static bool HasVariable(ImmutableArray<CompletionEntry> entries, string label)
    {
        return entries.Any(e => e.Kind == CompletionKind.Variable
            && string.Equals(e.Label, label, StringComparison.Ordinal));
    }

    /// <summary>The list at a cursor written as `|` in the source, which is removed before parsing.</summary>
    private static ImmutableArray<CompletionEntry> CompleteAtCaret(
        CompletionEngine engine,
        string path,
        string marked,
        IInsertProvider? inserts = null,
        GameProfile? profile = null)
    {
        int index = marked.IndexOf('|', StringComparison.Ordinal);
        Assert.True(index >= 0, "the source must mark the cursor with '|'");

        string text = marked.Remove(index, 1);
        SourceText source = SourceText.From(text);

        return engine.Complete(
            Analyze(path, text, inserts, profile), "raw", source.GetPosition(index), profile: profile);
    }

    [Fact]
    public void StatementScope_OffersTheEnclosingFunctionsParameters()
    {
        CompletionEngine engine = BuildWorld(new FakeFileSystem());

        ImmutableArray<CompletionEntry> entries = CompleteAtCaret(
            engine,
            @$"{Raw}\scripts\main.gsc",
            "#namespace game;\nfunction register( str_pool_name, n_bits )\n{\n    RegisterClientField( str|\n}\n");

        Assert.True(HasVariable(entries, "str_pool_name"));
        Assert.True(HasVariable(entries, "n_bits"));
        Assert.Equal("parameter", Entry(entries, "str_pool_name")!.Detail);
    }

    [Fact]
    public void StatementScope_MarksAByRefParameter()
    {
        CompletionEngine engine = BuildWorld(new FakeFileSystem());

        ImmutableArray<CompletionEntry> entries = CompleteAtCaret(
            engine,
            @$"{Raw}\scripts\main.gsc",
            "#namespace game;\nfunction fill( &a_out )\n{\n    |\n}\n");

        Assert.Equal("parameter (by ref)", Entry(entries, "a_out")!.Detail);
    }

    [Fact]
    public void StatementScope_OffersLocalsAssignedAboveTheCursor()
    {
        CompletionEngine engine = BuildWorld(new FakeFileSystem());

        ImmutableArray<CompletionEntry> entries = CompleteAtCaret(
            engine,
            @$"{Raw}\scripts\main.gsc",
            "#namespace game;\nfunction run()\n{\n    n_count = 3;\n    |\n}\n");

        Assert.True(HasVariable(entries, "n_count"));
        Assert.Equal("local", Entry(entries, "n_count")!.Detail);
    }

    /// <summary>
    /// A name assigned further down does not exist yet at the cursor, and reading one earns a 5016
    /// — the rule <c>vararg</c> is held to: a list that leads to a diagnostic is worse than one
    /// entry short.
    /// </summary>
    [Fact]
    public void StatementScope_DoesNotOfferALocalAssignedBelowTheCursor()
    {
        CompletionEngine engine = BuildWorld(new FakeFileSystem());

        ImmutableArray<CompletionEntry> entries = CompleteAtCaret(
            engine,
            @$"{Raw}\scripts\main.gsc",
            "#namespace game;\nfunction run()\n{\n    |\n    n_later = 3;\n}\n");

        Assert.False(HasVariable(entries, "n_later"));
    }

    [Fact]
    public void StatementScope_OffersAForeachVariableInsideTheLoop()
    {
        CompletionEngine engine = BuildWorld(new FakeFileSystem());

        ImmutableArray<CompletionEntry> entries = CompleteAtCaret(
            engine,
            @$"{Raw}\scripts\main.gsc",
            "#namespace game;\nfunction run( a_players )\n{\n    foreach ( e_player in a_players )\n    {\n        |\n    }\n}\n");

        Assert.True(HasVariable(entries, "e_player"));
        Assert.Equal("loop variable", Entry(entries, "e_player")!.Detail);
    }

    /// <summary>
    /// <c>self.count = 1</c> writes a field on something that outlives the call, so a bare
    /// <c>count</c> here does not reach it — the same exclusion go-to-definition makes.
    /// </summary>
    [Fact]
    public void StatementScope_DoesNotOfferAFieldWriteAsALocal()
    {
        CompletionEngine engine = BuildWorld(new FakeFileSystem());

        ImmutableArray<CompletionEntry> entries = CompleteAtCaret(
            engine,
            @$"{Raw}\scripts\main.gsc",
            "#namespace game;\nfunction run()\n{\n    self.n_health = 100;\n    |\n}\n");

        Assert.False(HasVariable(entries, "n_health"));
    }

    [Fact]
    public void StatementScope_DoesNotOfferAnotherFunctionsLocals()
    {
        CompletionEngine engine = BuildWorld(new FakeFileSystem());

        ImmutableArray<CompletionEntry> entries = CompleteAtCaret(
            engine,
            @$"{Raw}\scripts\main.gsc",
            "#namespace game;\nfunction first( str_theirs )\n{\n    n_theirs = 1;\n}\nfunction second()\n{\n    |\n}\n");

        Assert.False(HasVariable(entries, "n_theirs"));
        Assert.False(HasVariable(entries, "str_theirs"));
    }

    /// <summary>
    /// A method body is a function body for this purpose too — the reason <c>EnclosingFunction</c>
    /// looks inside classes at all.
    /// </summary>
    [Fact]
    public void StatementScope_OffersLocalsInsideAClassMethod()
    {
        CompletionEngine engine = BuildWorld(new FakeFileSystem());

        ImmutableArray<CompletionEntry> entries = CompleteAtCaret(
            engine,
            @$"{Raw}\scripts\scene.gsc",
            "class cScene\n{\n    function play( n_speed )\n    {\n        n_frame = 0;\n        |\n    }\n}\n");

        Assert.True(HasVariable(entries, "n_speed"));
        Assert.True(HasVariable(entries, "n_frame"));
    }

    /// <summary>
    /// A merge dialect declares with a bare name and no <c>function</c> keyword, so the enclosing
    /// declaration is found by a different shape of parse. The scope is the same question there.
    /// </summary>
    [Fact]
    public void AMergeDialect_OffersItsParametersAndLocalsToo()
    {
        CompletionEngine engine = BuildWorld(new FakeFileSystem());

        ImmutableArray<CompletionEntry> entries = CompleteAtCaret(
            engine,
            @$"{Raw}\scripts\main.gsc",
            "main( str_name )\n{\n    n_count = 0;\n    |\n}\n",
            inserts: null,
            profile: GameProfile.ByName("cod4")!);

        Assert.True(HasVariable(entries, "str_name"));
        Assert.True(HasVariable(entries, "n_count"));
    }

    [Fact]
    public void TopLevel_OffersNoLocals()
    {
        CompletionEngine engine = BuildWorld(new FakeFileSystem());

        ImmutableArray<CompletionEntry> entries = CompleteAtCaret(
            engine,
            @$"{Raw}\scripts\main.gsc",
            "#namespace game;\nfunction run( str_theirs )\n{\n}\n|\n");

        Assert.False(HasVariable(entries, "str_theirs"));
    }

    /// <summary>
    /// A class's <c>var</c> members are read as BARE NAMES in a class body — BO3's
    /// <c>AnimationAdjustmentInfoZ</c> constructor writes <c>adjustMentStarted = false;</c> — so they
    /// belong in statement scope beside the parameters and locals.
    /// </summary>
    [Fact]
    public void StatementScope_OffersTheEnclosingClassesMembers()
    {
        CompletionEngine engine = BuildWorld(new FakeFileSystem());

        ImmutableArray<CompletionEntry> entries = CompleteAtCaret(
            engine,
            @$"{Raw}\scripts\fury.gsc",
            "class AnimationInfo\n{\n    var startTime;\n    var stopTime;\n\n    function tick()\n    {\n        |\n    }\n}\n");

        Assert.NotNull(Entry(entries, "startTime"));
        Assert.Equal("member of AnimationInfo", Entry(entries, "stopTime")!.Detail);
    }

    [Fact]
    public void StatementScope_OffersAnInheritedMember()
    {
        FakeFileSystem files = new FakeFileSystem()
            .AddFile(@$"{Raw}\scripts\base.gsc", "class cBase\n{\n    var n_base_health;\n}\n");

        CompletionEngine engine = BuildWorld(files);

        ImmutableArray<CompletionEntry> entries = CompleteAtCaret(
            engine,
            @$"{Raw}\scripts\derived.gsc",
            "#using scripts\\base;\nclass cDerived : cBase\n{\n    function tick()\n    {\n        |\n    }\n}\n");

        Assert.Equal("member of cBase", Entry(entries, "n_base_health")!.Detail);
    }

    /// <summary>
    /// A constructor assigning a member bare is recorded as a local by extraction, so both readings
    /// produce the same name. One row, and it says which the name really is.
    /// </summary>
    [Fact]
    public void AMemberAssignedBare_IsOfferedOnceAndAsAMember()
    {
        CompletionEngine engine = BuildWorld(new FakeFileSystem());

        ImmutableArray<CompletionEntry> entries = CompleteAtCaret(
            engine,
            @$"{Raw}\scripts\fury.gsc",
            "class AnimationInfo\n{\n    var b_started;\n\n    constructor()\n    {\n        b_started = false;\n        |\n    }\n}\n");

        Assert.Single(entries.Where(e => string.Equals(e.Label, "b_started", StringComparison.Ordinal)));
        Assert.Equal("member of AnimationInfo", Entry(entries, "b_started")!.Detail);
    }

    [Fact]
    public void OutsideAClass_NoMembersAreOffered()
    {
        FakeFileSystem files = new FakeFileSystem()
            .AddFile(@$"{Raw}\scripts\info.gsc", "class AnimationInfo\n{\n    var startTime;\n}\n");

        CompletionEngine engine = BuildWorld(files);

        ImmutableArray<CompletionEntry> entries = CompleteAtCaret(
            engine,
            @$"{Raw}\scripts\main.gsc",
            "#using scripts\\info;\n#namespace game;\nfunction run()\n{\n    |\n}\n");

        Assert.Null(Entry(entries, "startTime"));
    }

    /// <summary>The reported case: every constant lives in a header, and none of them was offered.</summary>
    [Fact]
    public void StatementScope_OffersMacrosFromAnInsertedHeader()
    {
        CompletionEngine engine = BuildWorld(new FakeFileSystem());
        FakeInserts inserts = new FakeInserts()
            .Add(@"scripts\shared\shared.gsh", "#define MAX_PLAYERS 18\n#define ABS( x ) ( x )\n");

        ImmutableArray<CompletionEntry> entries = CompleteAtCaret(
            engine,
            @$"{Raw}\scripts\main.gsc",
            "#insert scripts\\shared\\shared.gsh;\n#namespace game;\nfunction run()\n{\n    |\n}\n",
            inserts);

        CompletionEntry? constant = Entry(entries, "MAX_PLAYERS");
        Assert.NotNull(constant);
        Assert.Equal(CompletionKind.Macro, constant!.Kind);

        // The header is named, because "where does this come from" is the question asked about a
        // macro that is not in the file being read.
        Assert.Equal("macro (shared.gsh)", constant.Detail);
    }

    /// <summary>A function-like macro IS a call at the use site, so it completes like one.</summary>
    [Fact]
    public void StatementScope_CompletesAFunctionLikeMacroAsACall()
    {
        CompletionEngine engine = BuildWorld(new FakeFileSystem());

        ImmutableArray<CompletionEntry> entries = CompleteAtCaret(
            engine,
            @$"{Raw}\scripts\main.gsc",
            "#define ABS( n_value ) ( n_value )\n#namespace game;\nfunction run()\n{\n    n_x = |\n}\n");

        CompletionEntry? macro = Entry(entries, "ABS");
        Assert.NotNull(macro);
        Assert.Equal("macro", macro!.Detail);
        Assert.Equal("ABS($0)", macro.InsertText);
        Assert.Equal("( n_value )", macro.LabelDetail);
    }

    [Fact]
    public void StatementScope_StillOffersThisFilesOwnMacros()
    {
        CompletionEngine engine = BuildWorld(new FakeFileSystem());

        ImmutableArray<CompletionEntry> entries = CompleteAtCaret(
            engine,
            @$"{Raw}\scripts\main.gsc",
            "#define LOCAL_CAP 5\n#namespace game;\nfunction run()\n{\n    |\n}\n");

        Assert.NotNull(Entry(entries, "LOCAL_CAP"));
    }

    // --- File scope ---
    //
    // Outside every function body the list used to be a static set of words: no macro, no function,
    // no class reached it, because everything workspace-derived sat below a `!insideFunction`
    // return. REGISTER_SYSTEM is written at column 0 in 477 of the shipped BO3 scripts and was
    // never offered once.

    /// <summary>The reported case, at the position it was reported from.</summary>
    [Fact]
    public void FileScope_OffersMacrosFromAnInsertedHeader()
    {
        CompletionEngine engine = BuildWorld(new FakeFileSystem());
        FakeInserts inserts = new FakeInserts()
            .Add(
                @"scripts\shared\shared.gsh",
                "#define REGISTER_SYSTEM( sys, func, reqs ) function autoexec __init__sytem__() { }\n#define MAX_PLAYERS 18\n");

        ImmutableArray<CompletionEntry> entries = CompleteAtCaret(
            engine,
            @$"{Raw}\scripts\main.gsc",
            "#insert scripts\\shared\\shared.gsh;\n#namespace game;\n|\nfunction run()\n{\n}\n",
            inserts);

        CompletionEntry? macro = Entry(entries, "REGISTER_SYSTEM");
        Assert.NotNull(macro);
        Assert.Equal(CompletionKind.Macro, macro!.Kind);
        Assert.Equal("macro (shared.gsh)", macro.Detail);

        // No terminator: the expansion is a declaration, and none of the 447 corpus uses carries one.
        Assert.Equal("REGISTER_SYSTEM($0)", macro.InsertText);

        // The object-like ones come too. File scope gets the list a body gets, rather than a
        // narrowed one whose rule would have to grow an exception per construct.
        Assert.NotNull(Entry(entries, "MAX_PLAYERS"));
    }

    /// <summary>
    /// What a macro invocation's ARGUMENTS need. `REGISTER_SYSTEM( "aat", &amp;__init__, undefined )`
    /// is a call, so file scope is an expression position too: the shipped BO3 scripts write 510
    /// function pointers and 467 `undefined`s there, and neither was reachable.
    /// </summary>
    [Fact]
    public void FileScope_OffersTheFunctionsInScopeAndTheExpressionAtoms()
    {
        CompletionEngine engine = BuildWorld(new FakeFileSystem()
            .AddFile(@$"{Raw}\scripts\system.gsc", "#namespace game;\nfunction __init__()\n{\n}\n"));

        ImmutableArray<CompletionEntry> entries = CompleteAtCaret(
            engine,
            @$"{Raw}\scripts\main.gsc",
            "#namespace game;\n|\nfunction run()\n{\n}\n");

        Assert.Contains(
            entries,
            e => e.Kind == CompletionKind.Function
                && e.Label.StartsWith("__init__", StringComparison.Ordinal));

        Assert.NotNull(Entry(entries, "undefined"));
    }

    /// <summary>
    /// The one category deliberately held back. The engine globals are <c>CompletionKind.Variable</c>,
    /// which sorts FIRST, so offering them at file scope would put `self` and `level` at the head of
    /// every list at a position where nothing can be sent on them.
    /// </summary>
    [Fact]
    public void FileScope_DoesNotOfferTheEngineGlobals()
    {
        CompletionEngine engine = BuildWorld(new FakeFileSystem());

        ImmutableArray<CompletionEntry> entries = CompleteAtCaret(
            engine,
            @$"{Raw}\scripts\main.gsc",
            "#namespace game;\n|\nfunction run()\n{\n}\n");

        Assert.False(HasVariable(entries, "self"));
        Assert.False(HasVariable(entries, "level"));
    }
}
