using System.Collections.Immutable;
using GSCode.Core.Symbols;
using GSCode.Core.Text;

namespace GSCode.Workspace.Database;

/// <summary>
/// One method reachable on a class, with the class that declares it.
///
/// <c>Record</c> is null when the declaring class came from the parse in hand rather than from the
/// store — the file being edited is ahead of the last index, so it genuinely has no indexed record
/// yet. That is why this is not a <see cref="ResolvedFunction"/>, whose Record is non-nullable and
/// which callers may dereference for a location.
/// </summary>
public sealed record ClassMethod(FunctionSymbol Method, ClassSymbol OwnerClass, ScriptRecord? Record);

/// <summary>
/// Maps the key a call SITE was written with onto the key its DEFINITION uses.
///
/// Extraction is per-file and cannot see another file's classes, so the key it writes is purely
/// lexical — the enclosing class for a bare call, the written qualifier for <c>A::b()</c>, nothing at
/// all for an arrow call on an untyped receiver. Inheritance breaks every one of those as an
/// identity: a bare <c>_prepare()</c> inside <c>cAwarenessSceneObject</c> is keyed to that class,
/// while the declaration it reaches is in <c>cSceneObject</c>. Since
/// <see cref="DatabaseQueries.FindReferences"/> and <see cref="SymbolAtPosition"/> are pure key
/// equality, something has to close that gap, and it can only be done where the whole workspace is
/// visible. That is this.
///
/// The rule that is easy to get backwards is <c>A::b()</c>. It looks like a class-method call and
/// usually is — but a dialect may declare a namespace and a class with the SAME name, and BO3 does:
/// <c>phalanx.gsc</c> has both <c>#namespace Phalanx</c> and <c>class Phalanx</c>, and its
/// <c>Phalanx::_PruneDead()</c> — written inside the class — targets a top-level function.
/// <c>throttle_shared.gsc</c> is the same. Resolving the class first would break 22 shipping call
/// sites, so the NAMESPACE is tried first and the class only when no namespace function matches.
/// </summary>
public static class MethodResolution
{
    /// <summary>
    /// Bound on any chain walk. Matches <see cref="Analysis.ClassCycleLint"/>, so a cycle that
    /// slipped past the lint still cannot spin here.
    /// </summary>
    private const int MaxDepth = 64;

    /// <summary>Bound on the descendant set, so a pathological hierarchy cannot fan out unboundedly.</summary>
    private const int MaxDescendants = 64;

    /// <summary>
    /// The key the definition of this reference is stored under, or the key unchanged when nothing
    /// better can be said.
    ///
    /// <paramref name="fileNamespace"/> is the namespace in effect at the call site, used only for
    /// the one fallback: a bare call inside a class body that turns out to name no method at all.
    /// Across the stock BO3 scripts no such call exists — all 525 name a method — but a mod may
    /// write one, and resolving it to the namespace is both correct and the difference between a
    /// working go-to-definition and a false "not found".
    /// </summary>
    public static SymbolKey Canonicalize(
        LanguageStore store,
        string askingContextId,
        SymbolKey key,
        ReferenceKind referenceKind,
        string fileNamespace = "")
    {
        if ( key.Kind != SymbolKind.Function )
        {
            return key;
        }

        // Written with no qualifier inside a class: a method of that class or of an ancestor.
        if ( key.OwnerClass is not null )
        {
            string? declaring = FindDeclaringClass(store, askingContextId, key.OwnerClass, key.Name);
            if ( declaring is not null )
            {
                return key with { OwnerClass = declaring };
            }

            // An arrow call is guaranteed to be a method, so there is nothing to fall back TO; a
            // bare call could always have meant the namespace instead.
            if ( referenceKind == ReferenceKind.MethodCall )
            {
                return key;
            }

            return new SymbolKey(fileNamespace.Length == 0 ? null : fileNamespace, key.Name, SymbolKind.Function);
        }

        // Written A::b(). Namespace first — see the Phalanx note above.
        if ( key.Namespace is not null )
        {
            ImmutableArray<ResolvedFunction> asNamespace = DatabaseQueries.LookupFunctions(
                store, askingContextId, askingPath: "", key.Namespace, key.Name, includePrivate: true);

            if ( asNamespace.Length > 0 )
            {
                return key;
            }

            // Not restricted to the enclosing class's ancestors on purpose. scene_shared.gsc:1019
            // writes `_o_bundle thread cscene::_stop_camera_anim_on_player(...)` inside cSceneObject,
            // and cScene is not one of its ancestors — the qualifier names whatever class it likes.
            string? declaring = FindDeclaringClass(store, askingContextId, key.Namespace, key.Name);
            if ( declaring is not null )
            {
                return new SymbolKey(null, key.Name, SymbolKind.Function, declaring);
            }

            return key;
        }

        // An arrow call on a receiver whose class is unknown. Resolvable only when exactly one class
        // in the workspace declares the name; otherwise the candidates get offered and nothing is
        // claimed.
        if ( referenceKind == ReferenceKind.MethodCall )
        {
            ImmutableArray<string> declarers = store.Classes.ClassesDeclaringMethod(key.Name);
            if ( declarers.Length == 1 )
            {
                return new SymbolKey(null, key.Name, SymbolKind.Function, declarers[0]);
            }
        }

        return key;
    }

    /// <summary>
    /// What a call site reaches — methods and namespace functions alike, so a caller asking "what
    /// does this name resolve to" never has to know which it was. The single routing facade.
    ///
    /// <see cref="DatabaseQueries.LookupFunctions"/> is deliberately NOT widened to do this instead.
    /// It treats a null namespace as "any namespace", which is right for a merge dialect but would
    /// make an unqualified <c>init()</c> match every unrelated <c>init</c> method in the workspace
    /// the moment methods became visible to it. Routing explicitly keeps that meaning intact.
    /// </summary>
    public static ImmutableArray<ResolvedFunction> ResolveCall(
        LanguageStore store,
        string askingContextId,
        string askingPath,
        SymbolKey key,
        ReferenceKind referenceKind,
        ImmutableArray<string> askingNamespaces = default,
        string fileNamespace = "")
    {
        SymbolKey canonical = Canonicalize(store, askingContextId, key, referenceKind, fileNamespace);

        if ( canonical.OwnerClass is not null )
        {
            return LookupMethods(store, askingContextId, canonical.OwnerClass, canonical.Name);
        }

        // An arrow call whose receiver's class is unknown, where SEVERAL classes declare the name so
        // Canonicalize could not pick one. Every one of them is a candidate; a namespace function is
        // not, and this is where saying so matters.
        //
        // The fallback below passes a NULL namespace, which means "any namespace" — right for a
        // merge dialect, catastrophic here. `thread [[o_obj]]->play( state )` in scene_shared.gsc has
        // four classes declaring `play`, so it fell through and matched `animation::play`, an
        // unrelated top-level function the arrow syntax cannot even reach. Hover, go-to-definition
        // and signature help all landed there.
        if ( referenceKind == ReferenceKind.MethodCall )
        {
            ImmutableArray<ResolvedFunction> candidates = MethodsNamed(store, askingContextId, canonical.Name);
            if ( candidates.Length > 0 )
            {
                return candidates;
            }
        }

        // No class declares the name at all. For an arrow call that means the field-holding-a
        // -function-pointer idiom — `[[self.classObj]]->onBeginUse( player )` in
        // gameobjects_shared.gsc — which really does reach a top-level function.
        return DatabaseQueries.LookupFunctions(
            store, askingContextId, askingPath, canonical.Namespace, canonical.Name,
            includePrivate: false, askingNamespaces: askingNamespaces);
    }

    /// <summary>
    /// Every method of this name declared by any visible class, most-derived-first within each
    /// class's own chain. The candidate set for an arrow call on a receiver whose class is unknown —
    /// 155 of the 159 arrow calls in the stock scripts.
    /// </summary>
    public static ImmutableArray<ResolvedFunction> MethodsNamed(
        LanguageStore store, string askingContextId, string methodKeyName)
    {
        ImmutableArray<ResolvedFunction>.Builder matches = ImmutableArray.CreateBuilder<ResolvedFunction>();

        foreach ( string className in store.Classes.ClassesDeclaringMethod(methodKeyName) )
        {
            matches.AddRange(LookupMethods(store, askingContextId, className, methodKeyName));
        }

        return matches.ToImmutable();
    }

    /// <summary>
    /// The nearest class at or above <paramref name="classKeyName"/> declaring
    /// <paramref name="methodKeyName"/>, or null. Walking upward from the most derived is what makes
    /// an override win over the method it overrides.
    /// </summary>
    public static string? FindDeclaringClass(
        LanguageStore store, string askingContextId, string classKeyName, string methodKeyName)
    {
        HashSet<string> visited = new(StringComparer.Ordinal);
        string? current = classKeyName;

        for ( int depth = 0; depth < MaxDepth; depth++ )
        {
            if ( current is null || !visited.Add(current) )
            {
                return null;
            }

            ImmutableArray<ResolvedClass> classes = DatabaseQueries.LookupClasses(
                store, askingContextId, namespaceName: null, current);

            if ( classes.Length == 0 )
            {
                return null;
            }

            foreach ( FunctionSymbol method in classes[0].Class.Methods )
            {
                if ( string.Equals(method.KeyName, methodKeyName, StringComparison.Ordinal) )
                {
                    return current;
                }
            }

            current = classes[0].Class.ParentKeyName;
        }

        return null;
    }

    /// <summary>
    /// Every method reachable on a class, own and inherited, keyed by name with the most derived
    /// declaration winning. The one place completion, signature help and hover get a class's method
    /// surface, so an override is never offered twice.
    /// </summary>
    /// <param name="localClasses">
    /// Classes from the parse in hand, which WIN over the store's copy of the same name.
    ///
    /// The store holds the last INDEXED version, and the file being edited is by definition ahead of
    /// it — so without this, completing inside a class you are currently writing walks a chain that
    /// starts at a class the store has never heard of and returns nothing at all, which is the one
    /// moment the help is most wanted.
    /// </param>
    public static ImmutableArray<ClassMethod> MethodsOf(
        LanguageStore store,
        string askingContextId,
        string classKeyName,
        ImmutableArray<ClassSymbol> localClasses = default)
    {
        Dictionary<string, ClassMethod> byName = new(StringComparer.Ordinal);
        HashSet<string> visited = new(StringComparer.Ordinal);
        string? current = classKeyName;

        for ( int depth = 0; depth < MaxDepth; depth++ )
        {
            if ( current is null || !visited.Add(current) )
            {
                break;
            }

            ClassSymbol? resolved = FindLocal(localClasses, current);
            ScriptRecord? record = null;

            if ( resolved is null )
            {
                ImmutableArray<ResolvedClass> classes = DatabaseQueries.LookupClasses(
                    store, askingContextId, namespaceName: null, current);

                if ( classes.Length == 0 )
                {
                    break;
                }

                resolved = classes[0].Class;
                record = classes[0].Record;
            }

            foreach ( FunctionSymbol method in resolved.Methods )
            {
                // TryAdd, not assignment: the walk starts at the most derived class, so the first
                // declaration of a name seen is the one that wins — which is what makes an override
                // shadow the method it overrides instead of being offered beside it.
                byName.TryAdd(method.KeyName, new ClassMethod(method, resolved, record));
            }

            current = resolved.ParentKeyName;
        }

        return [.. byName.Values];
    }

    private static ClassSymbol? FindLocal(ImmutableArray<ClassSymbol> localClasses, string classKeyName)
    {
        if ( localClasses.IsDefault )
        {
            return null;
        }

        foreach ( ClassSymbol classSymbol in localClasses )
        {
            if ( string.Equals(classSymbol.KeyName, classKeyName, StringComparison.Ordinal) )
            {
                return classSymbol;
            }
        }

        return null;
    }

    /// <summary>
    /// The declarations a call resolves to, given its written key and kind. Methods and namespace
    /// functions both come back as <see cref="ResolvedFunction"/>, so a caller that only wants "what
    /// does this name reach" does not have to know which it was.
    /// </summary>
    public static ImmutableArray<ResolvedFunction> LookupMethods(
        LanguageStore store, string askingContextId, string classKeyName, string methodKeyName)
    {
        string? declaring = FindDeclaringClass(store, askingContextId, classKeyName, methodKeyName);
        if ( declaring is null )
        {
            return [];
        }

        ImmutableArray<ResolvedFunction>.Builder matches = ImmutableArray.CreateBuilder<ResolvedFunction>();
        foreach ( ResolvedClass resolved in DatabaseQueries.LookupClasses(
            store, askingContextId, namespaceName: null, declaring) )
        {
            foreach ( FunctionSymbol method in resolved.Class.Methods )
            {
                if ( string.Equals(method.KeyName, methodKeyName, StringComparison.Ordinal) )
                {
                    matches.Add(new ResolvedFunction(method, resolved.Record) { OwnerClass = resolved.Class });
                }
            }
        }

        return matches.ToImmutable();
    }

    /// <summary>
    /// Every reference to whatever the site under the cursor names, when that might be a method.
    /// Returns an empty array when it is not one, leaving the caller to run its ordinary key query.
    ///
    /// The second branch is what go-to-definition needed. When several classes declare the name, no
    /// single key is canonical — and a plain index lookup then finds only the other untyped arrow
    /// calls, never a declaration, so <c>[[o_obj]]-&gt;play()</c> navigated to nothing while hover,
    /// which resolves by candidate rather than by key, answered correctly.
    /// </summary>
    public static ImmutableArray<(ScriptRecord Record, ReferenceEntry Entry)> FindReferencesForCall(
        ScriptDatabase database,
        ImmutableArray<LanguageStore> stores,
        LanguageStore store,
        string askingContextId,
        SymbolKey key,
        ReferenceKind referenceKind)
    {
        if ( key.Kind != SymbolKind.Function )
        {
            return [];
        }

        SymbolKey canonical = Canonicalize(store, askingContextId, key, referenceKind, key.Namespace ?? "");
        if ( canonical.OwnerClass is not null )
        {
            return FindMethodReferences(database, stores, store, askingContextId, canonical);
        }

        if ( referenceKind != ReferenceKind.MethodCall )
        {
            return [];
        }

        // Every declaring class is offered. The protocol takes a list, and several honest candidates
        // beat one confident guess about a receiver whose class is not knowable.
        ImmutableArray<string> declarers = store.Classes.ClassesDeclaringMethod(key.Name);
        if ( declarers.Length == 0 )
        {
            return [];
        }

        Dictionary<(string, TextRange), (ScriptRecord, ReferenceEntry)> union = [];
        foreach ( string declarer in declarers )
        {
            foreach ( (ScriptRecord Record, ReferenceEntry Entry) hit in FindMethodReferences(
                database, stores, store, askingContextId,
                new SymbolKey(null, key.Name, SymbolKind.Function, declarer)) )
            {
                union[(hit.Record.Path, hit.Entry.Range)] = hit;
            }
        }

        return [.. union.Values];
    }

    /// <summary>
    /// Every reference to a class method, given the CANONICAL key of its declaration.
    ///
    /// A method is not reachable under one key the way a function is, so this unions the four ways a
    /// call site can name it. All four are needed for the CodeLens count and the peek list to be
    /// right — and, because they run through here together, for the two to agree.
    /// </summary>
    public static ImmutableArray<(ScriptRecord Record, ReferenceEntry Entry)> FindMethodReferences(
        ScriptDatabase database,
        ImmutableArray<LanguageStore> stores,
        LanguageStore store,
        string askingContextId,
        SymbolKey canonical)
    {
        // Keyed by SITE, not by key: the four collections below overlap, and one call must not be
        // counted twice because it was reachable two ways.
        Dictionary<(string, TextRange), (ScriptRecord, ReferenceEntry)> found = [];

        void Collect(SymbolKey key, Func<ReferenceEntry, bool> accept)
        {
            foreach ( (ScriptRecord Record, ReferenceEntry Entry) hit in
                DatabaseQueries.FindAllReferences(database, stores, askingContextId, key) )
            {
                if ( accept(hit.Entry) )
                {
                    found[(hit.Record.Path, hit.Entry.Range)] = hit;
                }
            }
        }

        string owner = canonical.OwnerClass!;

        // 1. The declaration, plus the bare calls and [[self]]-> calls inside the declaring class,
        //    which extraction already keyed to it.
        Collect(canonical, static _ => true);

        // 2. Subclasses that do NOT override it call it under their own name, so those sites carry a
        //    different owner and would otherwise be invisible. An override ends that branch — its
        //    call sites belong to the override, not to this declaration.
        List<string> inheritors = [];
        foreach ( string descendant in Descendants(store, owner) )
        {
            if ( FindDeclaringClass(store, askingContextId, descendant, canonical.Name) != owner )
            {
                continue;
            }

            inheritors.Add(descendant);
            Collect(canonical with { OwnerClass = descendant }, static _ => true);
        }

        // 3. The written-qualifier form, Class::method(). Skipped for any name that is ALSO a
        //    namespace declaring that function: BO3's phalanx.gsc and throttle_shared.gsc each
        //    declare a namespace and a class of the same name, and the 22 shipping sites written
        //    that way mean the namespace. Counting them here would attribute them to a method.
        foreach ( string qualifier in (List<string>)[owner, .. inheritors] )
        {
            if ( DatabaseQueries.LookupFunctions(
                store, askingContextId, askingPath: "", qualifier, canonical.Name,
                includePrivate: true).Length > 0 )
            {
                continue;
            }

            Collect(
                new SymbolKey(qualifier, canonical.Name, SymbolKind.Function),
                static entry => entry.Kind == ReferenceKind.Call);
        }

        // 4. Arrow calls on a receiver whose class is unknown — 155 of the 159 in the stock scripts.
        //    A deliberate over-approximation: it counts every [[x]]->name() in the workspace, and
        //    without it those sites reference nothing at all. It cannot pull in sys::name() or an
        //    unqualified call, because both of those carry Kind == Call.
        Collect(
            new SymbolKey(null, canonical.Name, SymbolKind.Function),
            static entry => entry.Kind == ReferenceKind.MethodCall);

        return [.. found.Values];
    }

    /// <summary>
    /// Every class below this one, transitively. Used by find-all-references: a subclass that does
    /// not override a method calls it under its OWN name, so those call sites are references to the
    /// ancestor's declaration and are keyed somewhere the declaration's own key never reaches.
    /// </summary>
    public static ImmutableArray<string> Descendants(LanguageStore store, string classKeyName)
    {
        HashSet<string> seen = new(StringComparer.Ordinal) { classKeyName };
        Queue<string> pending = new();
        pending.Enqueue(classKeyName);

        ImmutableArray<string>.Builder found = ImmutableArray.CreateBuilder<string>();

        while ( pending.Count > 0 && found.Count < MaxDescendants )
        {
            string current = pending.Dequeue();

            foreach ( string child in store.Classes.DirectChildren(current) )
            {
                if ( !seen.Add(child) )
                {
                    continue;
                }

                found.Add(child);
                pending.Enqueue(child);
            }
        }

        return found.ToImmutable();
    }
}
