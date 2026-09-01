using System.Collections.Immutable;
using GSCode.Core;
using GSCode.Core.Symbols;
using GSCode.Parser;
using GSCode.Workspace.Api;
using GSCode.Workspace.Database;
using GSCode.Workspace.Indexing;
using GSCode.Workspace.Resolution;
using Xunit;
using Xunit.Abstractions;

namespace GSCode.Server.Tests.Corpus;

/// <summary>
/// Class-method resolution measured against the shipped BO3 scripts, which are known-good: they are
/// what the game loads. Anything here that fails to resolve is our defect, not theirs.
///
/// The unit tests pin the rules on sources written to exercise them. This pins the rules against
/// what the game actually contains — 37 classes, an inheritance chain up to two deep, 4 classes whose
/// parent lives in another file, and <c>scene_shared.gsc</c>, which alone holds 4 classes and 110
/// methods and was the file that made the gap obvious.
///
/// Every lookup below passes the context of the record the symbol came FROM, never a literal.
/// Resolution is scoped by context because that is what makes a mod's copy of a file shadow raw's,
/// and the corpus fixture indexes the mods folder beside the raw one — deliberately, since
/// <c>RootConfig</c> finds mods above a configured raw root so that setting one path does not
/// silently cost mod shadowing. Every query here asked for <c>"raw"</c> while the walk above it
/// collected from every context, so a class declared in ANY installed mod was looked for in raw,
/// not found, and reported as our defect. It read as correct on a machine with no BO3 mods
/// installed, which is every machine this had run on.
/// </summary>
[Trait("Category", "Corpus")]
[Collection(GameProfileCollection.Name)]
public class ClassResolutionCorpusTests
{
    private readonly ITestOutputHelper _output;

    public ClassResolutionCorpusTests(ITestOutputHelper output)
    {
        _output = output;
    }

    /// <summary>An indexed BO3 workspace, or null when no corpus is configured on this machine.</summary>
    private static async Task<(ScriptDatabase Database, PathResolver Resolver, NameTable Names)?> IndexAsync()
    {
        if ( !CorpusFixture.Available )
        {
            return null;
        }

        PathResolver resolver = CorpusFixture.Resolver();
        NameTable names = new();
        ScriptDatabase database = new();
        WorkspaceIndexer indexer = new(database, () => resolver, new PhysicalFileSystem(), names);
        await indexer.IndexAsync(IndexingMode.Full, NullIndexProgressListener.Instance, CancellationToken.None);

        return (database, resolver, names);
    }

    /// <summary>
    /// Every class the corpus declares, across both language worlds, each carrying the context of
    /// the record that declared it — which is the only context a lookup for it can succeed in.
    /// </summary>
    private static List<(LanguageStore Store, string ContextId, ClassSymbol Class)> AllClasses(ScriptDatabase database)
    {
        List<(LanguageStore, string, ClassSymbol)> classes = [];
        foreach ( LanguageStore store in (LanguageStore[])[database.Gsc, database.Csc] )
        {
            foreach ( ScriptRecord record in store.AllRecords )
            {
                foreach ( ClassSymbol classSymbol in record.Classes )
                {
                    classes.Add((store, record.ContextId, classSymbol));
                }
            }
        }

        return classes;
    }

    [Fact]
    public async Task EveryDeclaredParentResolves()
    {
        // Includes the 4 classes whose parent is declared in a different file — the case a
        // file-local walk gets wrong and only a workspace-wide one gets right.
        (ScriptDatabase Database, PathResolver Resolver, NameTable Names)? world = await IndexAsync();
        if ( world is null )
        {
            _output.WriteLine("SKIPPED: no BO3 corpus configured (set GSCODE_CORPUS_BO3).");
            return;
        }

        List<string> unresolved = [];
        foreach ( (LanguageStore store, string contextId, ClassSymbol classSymbol) in AllClasses(world.Value.Database) )
        {
            if ( classSymbol.ParentKeyName is null )
            {
                continue;
            }

            if ( DatabaseQueries.LookupClasses(store, contextId, namespaceName: null, classSymbol.ParentKeyName).Length == 0 )
            {
                unresolved.Add($"{classSymbol.Name} : {classSymbol.ParentKeyName}");
            }
        }

        _output.WriteLine($"classes: {AllClasses(world.Value.Database).Count}, unresolved parents: {unresolved.Count}");
        Assert.Empty(unresolved);
    }

    [Fact]
    public async Task EveryBareCallInsideAClassBodyIsExplained()
    {
        // A bare name inside a class body can mean three things, and all three occur: a method of
        // the class or an ancestor, an engine builtin (`playsoundatposition`, `moveto`, `hide` — the
        // majority, and the reason method-first must not become method-only), or a function in the
        // file's own namespace. What must never happen is a fourth: a call nothing explains.
        //
        // The method half is the part that used to be impossible. These calls were keyed under the
        // FILE's namespace, where no method declaration has ever lived, so every one of them was a
        // guaranteed key miss and the resolution lint needed a blanket suppression to hide it.
        (ScriptDatabase Database, PathResolver Resolver, NameTable Names)? world = await IndexAsync();
        if ( world is null )
        {
            _output.WriteLine("SKIPPED: no BO3 corpus configured (set GSCODE_CORPUS_BO3).");
            return;
        }

        BuiltinApiSet builtins = BuiltinApiSet.Load(Path.Combine(AppContext.BaseDirectory, "Api"));

        int asMethod = 0;
        int asBuiltin = 0;
        int asNamespaceFunction = 0;
        List<string> unexplained = [];

        foreach ( (LanguageStore store, ScriptLanguage language) in
            (( LanguageStore, ScriptLanguage )[])[(world.Value.Database.Gsc, ScriptLanguage.Gsc), (world.Value.Database.Csc, ScriptLanguage.Csc)] )
        {
            foreach ( ScriptRecord record in store.AllRecords )
            {
                foreach ( ReferenceEntry entry in record.References )
                {
                    if ( entry.Key.OwnerClass is null
                        || entry.Key.Kind != SymbolKind.Function
                        || entry.Kind != ReferenceKind.Call )
                    {
                        continue;
                    }

                    if ( MethodResolution.FindDeclaringClass(store, record.ContextId, entry.Key.OwnerClass, entry.Key.Name) is not null )
                    {
                        asMethod++;
                        continue;
                    }

                    if ( builtins.For(language).Find(entry.Key.Name) is not null )
                    {
                        asBuiltin++;
                        continue;
                    }

                    bool inOwnNamespace = false;
                    foreach ( string declared in record.DeclaredNamespaces )
                    {
                        if ( DatabaseQueries.LookupFunctions(
                            store, record.ContextId, record.Path, declared, entry.Key.Name, includePrivate: true).Length > 0 )
                        {
                            inOwnNamespace = true;
                            break;
                        }
                    }

                    if ( inOwnNamespace )
                    {
                        asNamespaceFunction++;
                        continue;
                    }

                    unexplained.Add($"{Path.GetFileName(record.Path)}({entry.Range.Start.Line + 1}): {entry.Key.OwnerClass}::{entry.Key.Name}");
                }
            }
        }

        _output.WriteLine(
            $"in-class bare calls: {asMethod} methods, {asBuiltin} builtins, {asNamespaceFunction} namespace functions, {unexplained.Count} unexplained");

        foreach ( string miss in unexplained.Take(20) )
        {
            _output.WriteLine("  " + miss);
        }

        // The method count is the load-bearing one: if resolution regresses, these fall through to
        // the builtin library and this drops toward zero while the total stays put.
        Assert.True(asMethod > 400, $"expected 400+ in-class calls to resolve as methods, got {asMethod}");
        Assert.Empty(unexplained);
    }

    [Fact]
    public async Task EveryCallWhoseQualifierNamesAClassIsExplained()
    {
        // `A::b()` where A is also a class name. Three outcomes occur in the stock scripts and all
        // three are correct:
        //
        // * a class method — `cSceneObject::_prepare()`, `cScene::init()`, and the cross-class
        //   `cscene::_stop_camera_anim_on_player()` written inside cSceneObject;
        // * a NAMESPACE function — phalanx.gsc and throttle_shared.gsc each declare a namespace and
        //   a class of the same name, and `Phalanx::_PruneDead()` means the top-level function.
        //   This is why resolution tries the namespace FIRST;
        // * a builtin. An UNQUALIFIED call inside a file whose #namespace happens to equal a class
        //   name keys identically to a qualified one — `isarray( x )` in phalanx.gsc keys as
        //   ("phalanx", "isarray") — so the key alone cannot tell the two apart, and does not need
        //   to: both resolve to the same place.
        (ScriptDatabase Database, PathResolver Resolver, NameTable Names)? world = await IndexAsync();
        if ( world is null )
        {
            _output.WriteLine("SKIPPED: no BO3 corpus configured (set GSCODE_CORPUS_BO3).");
            return;
        }

        BuiltinApiSet builtins = BuiltinApiSet.Load(Path.Combine(AppContext.BaseDirectory, "Api"));

        int asMethod = 0;
        int asNamespace = 0;
        int asBuiltin = 0;
        List<string> unexplained = [];

        foreach ( (LanguageStore store, ScriptLanguage language) in
            (( LanguageStore, ScriptLanguage )[])[(world.Value.Database.Gsc, ScriptLanguage.Gsc), (world.Value.Database.Csc, ScriptLanguage.Csc)] )
        {
            HashSet<string> classNames = [.. store.Classes.AllClassNames()];

            foreach ( ScriptRecord record in store.AllRecords )
            {
                foreach ( ReferenceEntry entry in record.References )
                {
                    if ( entry.Key.Namespace is null
                        || entry.Key.Kind != SymbolKind.Function
                        || entry.Kind != ReferenceKind.Call
                        || !classNames.Contains(entry.Key.Namespace) )
                    {
                        continue;
                    }

                    SymbolKey canonical = MethodResolution.Canonicalize(store, record.ContextId, entry.Key, entry.Kind);
                    if ( canonical.OwnerClass is not null )
                    {
                        asMethod++;
                        continue;
                    }

                    if ( DatabaseQueries.LookupFunctions(
                        store, record.ContextId, record.Path, entry.Key.Namespace, entry.Key.Name, includePrivate: true).Length > 0 )
                    {
                        asNamespace++;
                        continue;
                    }

                    if ( builtins.For(language).Find(entry.Key.Name) is not null )
                    {
                        asBuiltin++;
                        continue;
                    }

                    unexplained.Add($"{Path.GetFileName(record.Path)}({entry.Range.Start.Line + 1}): {entry.Key.Namespace}::{entry.Key.Name}");
                }
            }
        }

        _output.WriteLine(
            $"class-named qualifiers: {asMethod} methods, {asNamespace} namespace functions, {asBuiltin} builtins, {unexplained.Count} unexplained");

        foreach ( string miss in unexplained.Take(20) )
        {
            _output.WriteLine("  " + miss);
        }

        // The method count is what regresses first if the class half of resolution breaks.
        Assert.True(asMethod >= 20, $"expected 20+ class-qualified calls to resolve as methods, got {asMethod}");
        Assert.Empty(unexplained);
    }

    [Fact]
    public async Task NoNameIsBothANamespaceFunctionAndAMethodOfTheSameNamedClass()
    {
        // The guard on the namespace-first preference. Where a name is both a namespace and a class
        // — phalanx.gsc and throttle_shared.gsc — `A::b()` is resolved as a namespace call first,
        // which is right for all 22 shipping sites. That preference is only unambiguous while the
        // two never declare the SAME member name. If one ever does, this fails loudly rather than
        // resolving half the call sites to the wrong thing in silence.
        (ScriptDatabase Database, PathResolver Resolver, NameTable Names)? world = await IndexAsync();
        if ( world is null )
        {
            _output.WriteLine("SKIPPED: no BO3 corpus configured (set GSCODE_CORPUS_BO3).");
            return;
        }

        // Walked off the declaring records rather than the store's class-name index, which is what
        // gives each class the context to ask in. A name declared in two contexts is simply asked
        // about twice; there are forty classes, so nothing is saved by de-duplicating.
        List<string> collisions = [];
        foreach ( (LanguageStore store, string contextId, ClassSymbol classSymbol) in AllClasses(world.Value.Database) )
        {
            foreach ( ClassMethod method in MethodResolution.MethodsOf(store, contextId, classSymbol.KeyName) )
            {
                if ( DatabaseQueries.LookupFunctions(
                    store, contextId, askingPath: "", classSymbol.KeyName, method.Method.KeyName, includePrivate: true).Length > 0 )
                {
                    collisions.Add($"{classSymbol.KeyName}::{method.Method.KeyName}");
                }
            }
        }

        _output.WriteLine($"namespace/method collisions: {collisions.Count}");
        foreach ( string collision in collisions )
        {
            _output.WriteLine("  " + collision);
        }

        Assert.Empty(collisions);
    }

    [Fact]
    public async Task EveryMethodIsReachableFromItsOwnDeclaration()
    {
        // The end-to-end property behind go-to-definition and the CodeLens count: resolving a
        // method's own declaration key must find that declaration back. It failed for every method
        // in the corpus while a definition was keyed ("", name) and nothing else ever was.
        (ScriptDatabase Database, PathResolver Resolver, NameTable Names)? world = await IndexAsync();
        if ( world is null )
        {
            _output.WriteLine("SKIPPED: no BO3 corpus configured (set GSCODE_CORPUS_BO3).");
            return;
        }

        int methods = 0;
        List<string> unreachable = [];

        foreach ( (LanguageStore store, string contextId, ClassSymbol classSymbol) in AllClasses(world.Value.Database) )
        {
            foreach ( FunctionSymbol method in classSymbol.Methods )
            {
                methods++;
                ImmutableArray<ResolvedFunction> found = MethodResolution.LookupMethods(
                    store, contextId, classSymbol.KeyName, method.KeyName);

                if ( found.Length == 0 )
                {
                    unreachable.Add($"{classSymbol.Name}::{method.Name}");
                }
            }
        }

        _output.WriteLine($"methods: {methods}, unreachable from their own class: {unreachable.Count}");
        Assert.Empty(unreachable);
    }

    [Fact]
    public async Task EveryArrowCallNamesSomethingThatExists()
    {
        // The arrow form is ALMOST always a class method, and the resolution lint reports one that
        // resolves to nothing — a decision that rests on this measurement.
        //
        // "Almost", because two shipping sites are not. gameobjects_shared.gsc writes
        // `[[self.classObj]]->onBeginUse( player )`, and onBeginUse is a top-level FUNCTION that
        // gametypes assign to a field as a pointer (`domFlag.onBeginUse = &onBeginUse` in dom.gsc,
        // koth.gsc and sd.gsc). So `->` is not exclusively a class-method syntax in this dialect: it
        // also dispatches through a field holding a function pointer.
        //
        // That is why the lint falls back to a plain function lookup before reporting, and why this
        // asserts "names something that exists" rather than "names a method". Tightening it to
        // methods only would put an Error on two lines of code that ship and work.
        (ScriptDatabase Database, PathResolver Resolver, NameTable Names)? world = await IndexAsync();
        if ( world is null )
        {
            _output.WriteLine("SKIPPED: no BO3 corpus configured (set GSCODE_CORPUS_BO3).");
            return;
        }

        int arrows = 0;
        int asMethod = 0;
        int asFunctionPointer = 0;
        List<string> unresolved = [];

        foreach ( LanguageStore store in (LanguageStore[])[world.Value.Database.Gsc, world.Value.Database.Csc] )
        {
            foreach ( ScriptRecord record in store.AllRecords )
            {
                foreach ( ReferenceEntry entry in record.References )
                {
                    if ( entry.Kind != ReferenceKind.MethodCall )
                    {
                        continue;
                    }

                    arrows++;

                    bool isMethod = entry.Key.OwnerClass is not null
                        ? MethodResolution.FindDeclaringClass(store, record.ContextId, entry.Key.OwnerClass, entry.Key.Name) is not null
                        : store.Classes.ClassesDeclaringMethod(entry.Key.Name).Length > 0;

                    if ( isMethod )
                    {
                        asMethod++;
                        continue;
                    }

                    if ( DatabaseQueries.LookupFunctions(
                        store, record.ContextId, record.Path, namespaceName: null, entry.Key.Name, includePrivate: true).Length > 0 )
                    {
                        asFunctionPointer++;
                        continue;
                    }

                    unresolved.Add($"{Path.GetFileName(record.Path)}({entry.Range.Start.Line + 1}): ->{entry.Key.Name}");
                }
            }
        }

        _output.WriteLine(
            $"arrow calls: {arrows} total, {asMethod} class methods, {asFunctionPointer} function pointers, {unresolved.Count} unresolved");

        foreach ( string miss in unresolved.Take(20) )
        {
            _output.WriteLine("  " + miss);
        }

        Assert.Empty(unresolved);
    }
}
