using System.Collections.Immutable;
using GSCode.Core;
using GSCode.Core.Symbols;
using GSCode.Workspace.Database;
using GSCode.Workspace.Indexing;
using GSCode.Workspace.Resolution;
using GSCode.Workspace.Tests.Resolution;
using Xunit;

namespace GSCode.Workspace.Tests.Database;

/// <summary>
/// Find-all-references for a class method.
///
/// A function is reachable under exactly one key, so its references are one index lookup. A method
/// is not: inheritance means a subclass calls it under its OWN name, the <c>Class::method()</c> form
/// keys under the qualifier, and an arrow call on an untyped receiver keys under nothing at all.
/// Missing any of those makes the CodeLens count and the peek list disagree, which is the specific
/// failure this repo has already been bitten by once.
/// </summary>
public class MethodReferenceTests
{
    private const string Raw = @"C:\bo3\share\raw";

    private static (ScriptDatabase Database, LanguageStore Store) Build(params (string Name, string Source)[] files)
    {
        FakeFileSystem system = new();
        foreach ( (string Name, string Source) file in files )
        {
            system.AddFile(@$"{Raw}\scripts\{file.Name}.gsc", file.Source);
        }

        RootConfig config = RootConfig.Create(true, Raw, @"C:\bo3\mods", [], system);
        PathResolver resolver = new(config, system);
        ScriptDatabase database = new();
        WorkspaceIndexer indexer = new(database, () => resolver, system, new NameTable());
        indexer.IndexAsync(IndexingMode.Partial, NullIndexProgressListener.Instance, CancellationToken.None)
            .GetAwaiter().GetResult();

        return (database, database.Gsc);
    }

    /// <summary>References to the method <paramref name="name"/> declared on <paramref name="owner"/>.</summary>
    private static ImmutableArray<(ScriptRecord Record, ReferenceEntry Entry)> References(
        ScriptDatabase database, LanguageStore store, string owner, string name)
    {
        return MethodResolution.FindMethodReferences(
            database, [store], store, "raw", new SymbolKey(null, name, SymbolKind.Function, owner));
    }

    private static int CountOf(ImmutableArray<(ScriptRecord Record, ReferenceEntry Entry)> references, ReferenceKind kind)
    {
        return references.Count(hit => hit.Entry.Kind == kind);
    }

    [Fact]
    public void References_IncludeTheDefinitionAndBareCallsInTheDeclaringClass()
    {
        (ScriptDatabase database, LanguageStore store) = Build(
            ("a", "class cScene\n{\n    function play()\n    {\n    }\n    function run()\n    {\n        play();\n        play();\n    }\n}\n"));

        ImmutableArray<(ScriptRecord Record, ReferenceEntry Entry)> references =
            References(database, store, "cscene", "play");

        Assert.Equal(1, CountOf(references, ReferenceKind.Definition));
        Assert.Equal(2, CountOf(references, ReferenceKind.Call));
    }

    [Fact]
    public void References_IncludeSelfArrowCallsInTheDeclaringClass()
    {
        (ScriptDatabase database, LanguageStore store) = Build(
            ("a", "class cScene\n{\n    function play()\n    {\n    }\n    function run()\n    {\n        [[self]]->play();\n    }\n}\n"));

        Assert.Equal(1, CountOf(References(database, store, "cscene", "play"), ReferenceKind.MethodCall));
    }

    [Fact]
    public void References_IncludeInheritingSubclassesThatDoNotOverride()
    {
        // The subclass calls it under its OWN name, so extraction keyed the site to cAwarenessScene
        // while the declaration is keyed to cScene. Without the descendant walk this call is a
        // reference to nothing.
        (ScriptDatabase database, LanguageStore store) = Build(
            ("a", "class cScene\n{\n    function play()\n    {\n    }\n}\n"),
            ("b", "class cAwarenessScene : cScene\n{\n    function alert()\n    {\n        play();\n    }\n}\n"));

        Assert.Equal(1, CountOf(References(database, store, "cscene", "play"), ReferenceKind.Call));
    }

    [Fact]
    public void References_ExcludeSubclassesThatOverride()
    {
        // An override ends that branch: the subclass's call sites reach its own declaration, not the
        // ancestor's, so counting them here would credit the base with calls that never reach it.
        (ScriptDatabase database, LanguageStore store) = Build(
            ("a", "class cScene\n{\n    function play()\n    {\n    }\n}\n"),
            ("b", "class cAwarenessScene : cScene\n{\n    function play()\n    {\n    }\n    function alert()\n    {\n        play();\n    }\n}\n"));

        ImmutableArray<(ScriptRecord Record, ReferenceEntry Entry)> baseReferences =
            References(database, store, "cscene", "play");

        Assert.Equal(0, CountOf(baseReferences, ReferenceKind.Call));
        Assert.Equal(1, CountOf(References(database, store, "cawarenessscene", "play"), ReferenceKind.Call));
    }

    [Fact]
    public void References_IncludeTheWrittenQualifierForm()
    {
        (ScriptDatabase database, LanguageStore store) = Build(
            ("a", "class cScene\n{\n    function play()\n    {\n    }\n}\n"),
            ("b", "#namespace other;\nfunction run()\n{\n    o cScene::play();\n}\n"));

        Assert.Equal(1, CountOf(References(database, store, "cscene", "play"), ReferenceKind.Call));
    }

    [Fact]
    public void References_ExcludeANamespaceCallThatSharesTheClassName()
    {
        // phalanx.gsc: one name that is both a namespace and a class. Phalanx::_PruneDead() means the
        // top-level function, and crediting it to the class would misattribute 22 shipping sites.
        (ScriptDatabase database, LanguageStore store) = Build(
            ("phalanx",
                "#namespace Phalanx;\nfunction _PruneDead( t )\n{\n}\n"
                + "class Phalanx\n{\n    function _Update()\n    {\n        Phalanx::_PruneDead( 1 );\n    }\n}\n"));

        // The class does not declare _PruneDead, so asking for it as a method finds nothing at all.
        Assert.Empty(References(database, store, "phalanx", "_prunedead"));
    }

    [Fact]
    public void References_IncludeArrowCallsOnAnUntypedReceiver()
    {
        // 155 of the 159 arrow calls in the stock scripts are this shape, so without them a method's
        // reference count is near-zero on the code that uses classes most.
        (ScriptDatabase database, LanguageStore store) = Build(
            ("a", "class cScene\n{\n    function skip_scene()\n    {\n    }\n}\n"),
            ("b", "#namespace other;\nfunction run()\n{\n    [[o_scene]]->skip_scene();\n}\n"));

        Assert.Equal(1, CountOf(References(database, store, "cscene", "skip_scene"), ReferenceKind.MethodCall));
    }

    [Fact]
    public void References_CountEachSiteOnce()
    {
        // The four collections overlap — a [[self]]-> call inside the declaring class is reachable
        // both as the owner's own key and as an untyped arrow call.
        (ScriptDatabase database, LanguageStore store) = Build(
            ("a", "class cScene\n{\n    function play()\n    {\n    }\n    function run()\n    {\n        [[self]]->play();\n    }\n}\n"));

        ImmutableArray<(ScriptRecord Record, ReferenceEntry Entry)> references =
            References(database, store, "cscene", "play");

        Assert.Equal(references.Length, references.Select(hit => (hit.Record.Path, hit.Entry.Range)).Distinct().Count());
    }

    [Fact]
    public void References_DoNotIncludeAnUnqualifiedCallOutsideAnyClass()
    {
        // A plain call keys with no owner and Kind == Call, the same shape as sys::play(). Only the
        // arrow kind may be swept up by the untyped-receiver rule.
        (ScriptDatabase database, LanguageStore store) = Build(
            ("a", "class cScene\n{\n    function play()\n    {\n    }\n}\n"),
            ("b", "#namespace other;\nfunction run()\n{\n    play();\n}\n"));

        Assert.Equal(0, CountOf(References(database, store, "cscene", "play"), ReferenceKind.Call));
    }

    /// <summary>What the cursor at a call site resolves to — the query go-to-definition runs.</summary>
    private static ImmutableArray<(ScriptRecord Record, ReferenceEntry Entry)> FromCallSite(
        ScriptDatabase database, LanguageStore store, SymbolKey key, ReferenceKind kind)
    {
        return MethodResolution.FindReferencesForCall(database, [store], store, "raw", key, kind);
    }

    [Fact]
    public void Definition_OfAnUntypedArrowCall_ReachesEveryDeclaringClass()
    {
        // `thread [[o_obj]]->play( state )`. Four classes declare `play` in scene_shared.gsc, so no
        // single key is canonical — and a plain index lookup then finds only the other untyped arrow
        // calls, never a declaration. Go-to-definition landed on nothing while hover was correct.
        (ScriptDatabase database, LanguageStore store) = Build(
            ("a", "class cSceneObject\n{\n    function play()\n    {\n    }\n}\n"),
            ("b", "class cScene\n{\n    function play( str_state )\n    {\n    }\n}\n"),
            ("c", "#namespace game;\nfunction run()\n{\n    thread [[o_obj]]->play( 1 );\n}\n"));

        ImmutableArray<(ScriptRecord Record, ReferenceEntry Entry)> definitions =
        [
            .. FromCallSite(database, store, new SymbolKey(null, "play", SymbolKind.Function), ReferenceKind.MethodCall)
                .Where(hit => hit.Entry.Kind == ReferenceKind.Definition)
        ];

        Assert.Equal(2, definitions.Length);
    }

    [Fact]
    public void Definition_OfAnUntypedArrowCall_DoesNotReachASameNamedNamespaceFunction()
    {
        // The whole point of the narrowing: `animation::play` is a top-level function the arrow
        // syntax cannot reach, and it used to be the single answer go-to-definition gave.
        (ScriptDatabase database, LanguageStore store) = Build(
            ("a", "class cScene\n{\n    function play( str_state )\n    {\n    }\n}\n"),
            ("animation", "#namespace animation;\nfunction play( anim, ent )\n{\n}\n"));

        ImmutableArray<(ScriptRecord Record, ReferenceEntry Entry)> definitions =
        [
            .. FromCallSite(database, store, new SymbolKey(null, "play", SymbolKind.Function), ReferenceKind.MethodCall)
                .Where(hit => hit.Entry.Kind == ReferenceKind.Definition)
        ];

        (ScriptRecord Record, ReferenceEntry Entry) only = Assert.Single(definitions);
        Assert.EndsWith(@"a.gsc", only.Record.Path, StringComparison.Ordinal);
    }

    [Fact]
    public void APlainCall_IsLeftToTheOrdinaryKeyQuery()
    {
        // Kind == Call with no owner is an unqualified call or a sys:: builtin, never an arrow call,
        // so the method query must decline it and let the normal path run.
        (ScriptDatabase database, LanguageStore store) = Build(
            ("a", "class cScene\n{\n    function play()\n    {\n    }\n}\n"));

        Assert.Empty(FromCallSite(
            database, store, new SymbolKey(null, "play", SymbolKind.Function), ReferenceKind.Call));
    }

    [Fact]
    public void NestedArrowChain_ResolvesTheInnerCallPreciselyAndTheOuterByName()
    {
        // scene_shared.gsc:1926 — `add_object( [[ [[self]]->new_object() ]]->first_init( s_obj, self ) )`.
        //
        // The inner receiver is `self`, so new_object pins to the enclosing class exactly. The OUTER
        // receiver is the inner call's return value, whose class would need return-type inference —
        // so first_init resolves by name across the classes that declare it. Both are answered; only
        // the second is an over-approximation.
        (ScriptDatabase database, LanguageStore store) = Build(
            ("a",
                "class cScene\n{\n    function new_object()\n    {\n    }\n"
                + "    function build()\n    {\n        add_object( [[ [[self]]->new_object() ]]->first_init( s_obj, self ) );\n    }\n"
                + "    function add_object( o )\n    {\n    }\n}\n"),
            ("b", "class cSceneObject\n{\n    function first_init( s, o )\n    {\n    }\n}\n"));

        // Inner: keyed to cScene by extraction, so it resolves without any by-name guessing.
        ImmutableArray<(ScriptRecord Record, ReferenceEntry Entry)> inner =
            References(database, store, "cscene", "new_object");

        Assert.Equal(1, inner.Count(hit => hit.Entry.Kind == ReferenceKind.Definition));
        Assert.Equal(1, inner.Count(hit => hit.Entry.Kind == ReferenceKind.MethodCall));

        // Outer: no owner on the key, resolved through the one class declaring the name.
        ImmutableArray<(ScriptRecord Record, ReferenceEntry Entry)> outer = FromCallSite(
            database, store, new SymbolKey(null, "first_init", SymbolKind.Function), ReferenceKind.MethodCall);

        Assert.Equal(1, outer.Count(hit => hit.Entry.Kind == ReferenceKind.Definition));
        Assert.Equal(1, outer.Count(hit => hit.Entry.Kind == ReferenceKind.MethodCall));
    }

    [Fact]
    public void MethodLensKey_CountsItsCallSites()
    {
        // The key CodeLensHandler builds for a method declaration: no namespace, owner = the
        // declaring class. Methods carried no lens at all until this existed, because the handler
        // walked Extraction.Functions, which holds only top-level functions.
        //
        // Counted the way the lens counts — everything that is not the declaration — and taken from
        // the same query the peek list runs, which is what keeps the number and the list agreeing.
        (ScriptDatabase database, LanguageStore store) = Build(
            ("a", "class cScene\n{\n    function play()\n    {\n    }\n    function run()\n    {\n        play();\n        [[self]]->play();\n    }\n}\n"),
            ("b", "#namespace game;\nfunction go()\n{\n    [[o_scene]]->play();\n}\n"));

        ImmutableArray<(ScriptRecord Record, ReferenceEntry Entry)> peek = FromCallSite(
            database, store, new SymbolKey(null, "play", SymbolKind.Function, "cscene"), ReferenceKind.Call);

        int lensCount = peek.Count(hit => hit.Entry.Kind != ReferenceKind.Definition);

        // The bare call, the self-arrow, and the untyped arrow in the other file.
        Assert.Equal(3, lensCount);
        Assert.Equal(1, peek.Count(hit => hit.Entry.Kind == ReferenceKind.Definition));
    }

    [Fact]
    public void MethodLensKey_OfAnInheritedMethod_CountsTheSubclassesCallSites()
    {
        // A subclass that does not override calls it under its OWN name, so the base's lens has to
        // reach across the inheritance edge or it under-reports.
        (ScriptDatabase database, LanguageStore store) = Build(
            ("a", "class cScene\n{\n    function play()\n    {\n    }\n}\n"),
            ("b", "class cAwarenessScene : cScene\n{\n    function alert()\n    {\n        play();\n    }\n}\n"));

        ImmutableArray<(ScriptRecord Record, ReferenceEntry Entry)> peek = FromCallSite(
            database, store, new SymbolKey(null, "play", SymbolKind.Function, "cscene"), ReferenceKind.Call);

        Assert.Equal(1, peek.Count(hit => hit.Entry.Kind != ReferenceKind.Definition));
    }

    [Fact]
    public void References_ReachThroughTwoLevelsOfInheritance()
    {
        (ScriptDatabase database, LanguageStore store) = Build(
            ("a", "class A\n{\n    function play()\n    {\n    }\n}\n"),
            ("b", "class B : A\n{\n}\n"),
            ("c", "class C : B\n{\n    function run()\n    {\n        play();\n    }\n}\n"));

        Assert.Equal(1, CountOf(References(database, store, "a", "play"), ReferenceKind.Call));
    }
}
