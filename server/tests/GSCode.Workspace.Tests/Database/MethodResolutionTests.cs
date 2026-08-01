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
/// Mapping a written call key onto the key its declaration uses.
///
/// The case worth reading first is <c>QualifiedCall_PrefersTheNamespace...</c>. BO3's
/// <c>phalanx.gsc</c> declares both <c>#namespace Phalanx</c> and <c>class Phalanx</c>, and its
/// <c>Phalanx::_PruneDead()</c> — written inside the class — means the top-level function.
/// <c>throttle_shared.gsc</c> does the same. Resolving the class first looks obviously right and
/// breaks 22 shipping call sites.
/// </summary>
public class MethodResolutionTests
{
    private const string Raw = @"C:\bo3\share\raw";

    private static LanguageStore Store(params (string Name, string Source)[] files)
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

        return database.Gsc;
    }

    private static SymbolKey Canonical(LanguageStore store, SymbolKey key, ReferenceKind kind = ReferenceKind.Call)
    {
        return MethodResolution.Canonicalize(store, "raw", key, kind);
    }

    [Fact]
    public void MethodOnTheClassItself_ResolvesToThatClass()
    {
        LanguageStore store = Store(("a", "class cScene\n{\n    function play()\n    {\n    }\n}\n"));

        SymbolKey resolved = Canonical(store, new SymbolKey(null, "play", SymbolKind.Function, "cscene"));

        Assert.Equal("cscene", resolved.OwnerClass);
    }

    [Fact]
    public void InheritedMethod_ResolvesToTheDeclaringAncestor()
    {
        LanguageStore store = Store(
            ("a", "class cScene\n{\n    function play()\n    {\n    }\n}\nclass cAwarenessScene : cScene\n{\n}\n"));

        SymbolKey resolved = Canonical(
            store, new SymbolKey(null, "play", SymbolKind.Function, "cawarenessscene"));

        Assert.Equal("cscene", resolved.OwnerClass);
    }

    [Fact]
    public void ParentInAnotherFile_Resolves()
    {
        // 4 of the 37 stock BO3 classes inherit across a file boundary, scene_shared.gsc among them.
        LanguageStore store = Store(
            ("base", "class cScriptBundleBase\n{\n    function init()\n    {\n    }\n}\n"),
            ("a", "class cScene : cScriptBundleBase\n{\n}\n"));

        SymbolKey resolved = Canonical(store, new SymbolKey(null, "init", SymbolKind.Function, "cscene"));

        Assert.Equal("cscriptbundlebase", resolved.OwnerClass);
    }

    [Fact]
    public void OverriddenMethod_ResolvesToTheMostDerived()
    {
        LanguageStore store = Store(
            ("a", "class cScene\n{\n    function play()\n    {\n    }\n}\n"
                + "class cAwarenessScene : cScene\n{\n    function play()\n    {\n    }\n}\n"));

        SymbolKey resolved = Canonical(
            store, new SymbolKey(null, "play", SymbolKind.Function, "cawarenessscene"));

        Assert.Equal("cawarenessscene", resolved.OwnerClass);
    }

    [Fact]
    public void GrandparentMethod_ResolvesThroughTwoLevels()
    {
        LanguageStore store = Store(
            ("a", "class A\n{\n    function play()\n    {\n    }\n}\nclass B : A\n{\n}\nclass C : B\n{\n}\n"));

        Assert.Equal("a", Canonical(store, new SymbolKey(null, "play", SymbolKind.Function, "c")).OwnerClass);
    }

    [Fact]
    public void QualifiedCall_PrefersTheNamespaceWhenBothANamespaceAndAClassMatch()
    {
        // phalanx.gsc, reduced to its shape: one file, one name, both a namespace and a class.
        LanguageStore store = Store(
            ("phalanx",
                "#namespace Phalanx;\nfunction private _PruneDead( t )\n{\n}\n"
                + "class Phalanx\n{\n    function _Update()\n    {\n    }\n}\n"));

        SymbolKey resolved = Canonical(store, new SymbolKey("phalanx", "_prunedead", SymbolKind.Function));

        Assert.Null(resolved.OwnerClass);
        Assert.Equal("phalanx", resolved.Namespace);
    }

    [Fact]
    public void QualifiedCall_FallsBackToTheClassWhenNoNamespaceFunctionMatches()
    {
        LanguageStore store = Store(
            ("phalanx",
                "#namespace Phalanx;\nfunction private _PruneDead( t )\n{\n}\n"
                + "class Phalanx\n{\n    function _Update()\n    {\n    }\n}\n"));

        SymbolKey resolved = Canonical(store, new SymbolKey("phalanx", "_update", SymbolKind.Function));

        Assert.Equal("phalanx", resolved.OwnerClass);
        Assert.Null(resolved.Namespace);
    }

    [Fact]
    public void CrossClassQualifiedCall_ResolvesWithoutRequiringAncestry()
    {
        // scene_shared.gsc:1019 — `cscene::_stop_camera_anim_on_player` written inside cSceneObject,
        // which does not inherit from cScene at all.
        LanguageStore store = Store(
            ("a", "class cSceneObject\n{\n}\nclass cScene\n{\n    function stop_camera()\n    {\n    }\n}\n"));

        SymbolKey resolved = Canonical(store, new SymbolKey("cscene", "stop_camera", SymbolKind.Function));

        Assert.Equal("cscene", resolved.OwnerClass);
    }

    [Fact]
    public void BareCallInAClassNamingNoMethod_FallsBackToTheNamespace()
    {
        // No stock BO3 script does this — all 525 in-class bare calls name a method — but a mod can,
        // and resolving it to the namespace is the difference between working navigation and a false
        // "not found".
        LanguageStore store = Store(("a", "#namespace scene;\nclass cScene\n{\n}\n"));

        SymbolKey resolved = MethodResolution.Canonicalize(
            store, "raw", new SymbolKey(null, "helper", SymbolKind.Function, "cscene"),
            ReferenceKind.Call, fileNamespace: "scene");

        Assert.Null(resolved.OwnerClass);
        Assert.Equal("scene", resolved.Namespace);
    }

    [Fact]
    public void ArrowCallNamingNoMethod_DoesNotFallBackToTheNamespace()
    {
        // The arrow form is guaranteed to be a method call, so there is nothing to fall back to.
        LanguageStore store = Store(("a", "#namespace scene;\nclass cScene\n{\n}\n"));

        SymbolKey resolved = MethodResolution.Canonicalize(
            store, "raw", new SymbolKey(null, "helper", SymbolKind.Function, "cscene"),
            ReferenceKind.MethodCall, fileNamespace: "scene");

        Assert.Equal("cscene", resolved.OwnerClass);
    }

    [Fact]
    public void UnknownReceiverArrowCall_ResolvesWhenExactlyOneClassDeclaresTheMethod()
    {
        LanguageStore store = Store(
            ("a", "class cScene\n{\n    function skip_scene()\n    {\n    }\n}\nclass cOther\n{\n}\n"));

        SymbolKey resolved = Canonical(
            store, new SymbolKey(null, "skip_scene", SymbolKind.Function), ReferenceKind.MethodCall);

        Assert.Equal("cscene", resolved.OwnerClass);
    }

    [Fact]
    public void UnknownReceiverArrowCall_StaysOpenWhenSeveralClassesDeclareIt()
    {
        LanguageStore store = Store(
            ("a", "class cScene\n{\n    function play()\n    {\n    }\n}\nclass cOther\n{\n    function play()\n    {\n    }\n}\n"));

        SymbolKey resolved = Canonical(
            store, new SymbolKey(null, "play", SymbolKind.Function), ReferenceKind.MethodCall);

        Assert.Null(resolved.OwnerClass);
    }

    [Fact]
    public void PlainUnqualifiedCall_IsNotTreatedAsAMethod()
    {
        // Kind == Call with no owner is an ordinary call or a sys:: builtin, never an arrow call, so
        // it must not pick up a class even when one happens to declare the name.
        LanguageStore store = Store(("a", "class cScene\n{\n    function play()\n    {\n    }\n}\n"));

        SymbolKey resolved = Canonical(store, new SymbolKey(null, "play", SymbolKind.Function));

        Assert.Null(resolved.OwnerClass);
    }

    [Fact]
    public void InheritanceCycle_TerminatesInsteadOfSpinning()
    {
        LanguageStore store = Store(("a", "class A : B\n{\n}\nclass B : A\n{\n}\n"));

        Assert.Null(MethodResolution.FindDeclaringClass(store, "raw", "a", "play"));
    }

    [Fact]
    public void MethodsOf_IncludesInheritedMethods()
    {
        LanguageStore store = Store(
            ("a", "class cScene\n{\n    function play()\n    {\n    }\n    function stop()\n    {\n    }\n}\n"
                + "class cAwarenessScene : cScene\n{\n    function alert()\n    {\n    }\n}\n"));

        ImmutableArray<ClassMethod> methods = MethodResolution.MethodsOf(store, "raw", "cawarenessscene");

        Assert.Equal(["alert", "play", "stop"], methods.Select(m => m.Method.KeyName).Order().ToArray());
    }

    [Fact]
    public void MethodsOf_OffersAnOverrideOnlyOnce()
    {
        LanguageStore store = Store(
            ("a", "class cScene\n{\n    function play()\n    {\n    }\n}\n"
                + "class cAwarenessScene : cScene\n{\n    function play()\n    {\n    }\n}\n"));

        ImmutableArray<ClassMethod> methods = MethodResolution.MethodsOf(store, "raw", "cawarenessscene");

        ClassMethod only = Assert.Single(methods);
        Assert.Equal("cawarenessscene", only.OwnerClass.KeyName);
    }

    [Fact]
    public void Descendants_AreTransitiveAndExcludeTheClassItself()
    {
        LanguageStore store = Store(
            ("a", "class A\n{\n}\nclass B : A\n{\n}\nclass C : B\n{\n}\n"));

        Assert.Equal(["b", "c"], MethodResolution.Descendants(store, "a").Order().ToArray());
    }

    [Fact]
    public void Descendants_TerminateOnACycle()
    {
        LanguageStore store = Store(("a", "class A : B\n{\n}\nclass B : A\n{\n}\n"));

        Assert.Equal(["b"], MethodResolution.Descendants(store, "a").ToArray());
    }

    // --- An arrow call must not fall through to a namespace function ---
    //
    // `thread [[o_obj]]->play( state )` in scene_shared.gsc. FOUR classes declare `play`, so no
    // single one can be canonical — and the fallback lookup passes a null namespace, which means
    // "any namespace", so it matched `animation::play`: a top-level function the arrow syntax cannot
    // reach at all. Hover, go-to-definition and signature help all landed there.

    private const string PlayWorld =
        "class cSceneObject\n{\n    function play()\n    {\n    }\n}\n"
        + "class cScene\n{\n    function play( str_state )\n    {\n    }\n}\n";

    private const string PlayNamespace = "#namespace animation;\nfunction play( anim, ent )\n{\n}\n";

    [Fact]
    public void ArrowCallWithSeveralDeclarers_ResolvesToTheClassMethodsNotANamespaceFunction()
    {
        LanguageStore store = Store(("a", PlayWorld), ("animation", PlayNamespace));

        ImmutableArray<ResolvedFunction> resolved = MethodResolution.ResolveCall(
            store, "raw", askingPath: "", new SymbolKey(null, "play", SymbolKind.Function),
            ReferenceKind.MethodCall);

        Assert.Equal(2, resolved.Length);
        Assert.All(resolved, r => Assert.NotNull(r.OwnerClass));
        Assert.Equal(["cscene", "csceneobject"], resolved.Select(r => r.OwnerClass!.KeyName).Order().ToArray());
    }

    [Fact]
    public void ArrowCallNamingNoClassMethod_StillReachesAFunctionPointerTarget()
    {
        // gameobjects_shared.gsc writes `[[self.classObj]]->onBeginUse( player )`, and onBeginUse is
        // a top-level function that gametypes assign to that field with &onBeginUse. The fallback
        // exists for exactly that, so narrowing it must not remove it.
        LanguageStore store = Store(
            ("a", PlayWorld),
            ("dom", "#namespace dom;\nfunction onBeginUse( player )\n{\n}\n"));

        ImmutableArray<ResolvedFunction> resolved = MethodResolution.ResolveCall(
            store, "raw", askingPath: "", new SymbolKey(null, "onbeginuse", SymbolKind.Function),
            ReferenceKind.MethodCall);

        Assert.Single(resolved);
        Assert.Null(resolved[0].OwnerClass);
    }

    [Fact]
    public void PlainCallWithTheSameName_StillReachesTheNamespaceFunction()
    {
        // The narrowing is scoped to the arrow kind. An ordinary call must keep resolving by
        // namespace exactly as before, or every unqualified call in the workspace changes meaning.
        LanguageStore store = Store(("a", PlayWorld), ("animation", PlayNamespace));

        ImmutableArray<ResolvedFunction> resolved = MethodResolution.ResolveCall(
            store, "raw", askingPath: "", new SymbolKey("animation", "play", SymbolKind.Function),
            ReferenceKind.Call);

        Assert.Single(resolved);
        Assert.Null(resolved[0].OwnerClass);
    }

    [Fact]
    public void SelfArrowCall_PrefersTheEnclosingClassOverOtherDeclarers()
    {
        LanguageStore store = Store(("a", PlayWorld), ("animation", PlayNamespace));

        ImmutableArray<ResolvedFunction> resolved = MethodResolution.ResolveCall(
            store, "raw", askingPath: "", new SymbolKey(null, "play", SymbolKind.Function, "cscene"),
            ReferenceKind.MethodCall);

        Assert.Single(resolved);
        Assert.Equal("cscene", resolved[0].OwnerClass!.KeyName);
    }
}
