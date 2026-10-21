using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Runtime.InteropServices;
using GSCode.Core.Symbols;

namespace GSCode.Workspace.Database;

/// <summary>
/// Which files declare which classes, who inherits from whom, and which classes declare a given
/// method name. One instance per <see cref="LanguageStore"/>, so GSC/CSC isolation stays structural
/// — <c>class cScene</c> in <c>scene_shared.gsc</c> and in <c>scene_shared.csc</c> live in separate
/// graphs and cannot see each other.
///
/// It turns every class question into a dictionary hit rather than a scan of every record in the
/// store: <c>LookupClasses</c> per parent link, <c>AllVisibleClasses</c> per keystroke,
/// <c>NamespaceUsageLint</c>'s class-name set per file linted, and the parent-chain walks method
/// resolution makes constantly.
///
/// The reverse maps are all PATH-valued, never name-valued. That is what makes replacing one file's
/// contribution exact: a file is removed from precisely the buckets its previous contribution
/// touched, and two files declaring the same class name — an overlay shadowing a raw script — can
/// never corrupt each other's removal. Deriving names instead would require reference counting to
/// know when the last declarer of a name went away.
///
/// Queries return class NAMES and file PATHS, never symbols. Callers resolve those against
/// <see cref="LanguageStore.TryGet"/> and apply <see cref="ScriptDatabase.CanSee"/> and overlay
/// shadowing themselves, so visibility keeps exactly one implementation and the graph never becomes
/// a second, subtly different, answer to "what can this file see".
///
/// READS TAKE NO LOCK. Every answer is cached on its bucket and cleared by the write that changes it;
/// a read takes the gate only to rebuild a cleared answer. Method resolution asks this graph for
/// every qualified call and every arrow call in every file, from every lint thread at once, and with
/// one lock around every read the full-mode sweep of 25,000 heavy BO3 files spent half its time in
/// <see cref="PathsDeclaring(string)"/> — 88% of sampled thread time blocked, on buckets holding one
/// path each — and the per-read copy made it quadratic as soon as a name had many declarers (PERF.md,
/// the scale section).
/// </summary>
public sealed class ClassGraph
{
    /// <summary>
    /// The paths behind one key, and the answers last handed out for them. The set is written only
    /// under <see cref="_gate"/>; <see cref="Paths"/> and <see cref="Names"/> are published whole and
    /// cleared by every write to the set, so a reader sees either a complete answer or none.
    /// </summary>
    private sealed class Bucket
    {
        public readonly HashSet<string> Set = new(StringComparer.Ordinal);

        public volatile string[]? Paths;

        /// <summary>The class names a name-valued query derived from <see cref="Set"/>, where one does.</summary>
        public volatile string[]? Names;
    }

    private readonly Dictionary<string, ImmutableArray<ClassSymbol>> _classesByPath = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Bucket> _pathsByClassName = new(StringComparer.Ordinal);

    /// <summary>
    /// The same as <see cref="_pathsByClassName"/>, keyed by namespace as well. A qualified lookup
    /// read the bare-name list and dropped every other namespace's declarer; see
    /// <see cref="PathsDeclaring(string, string)"/>.
    /// </summary>
    private readonly ConcurrentDictionary<(string Namespace, string KeyName), Bucket> _pathsByQualifiedName = new();
    private readonly ConcurrentDictionary<string, Bucket> _pathsByParentName = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Bucket> _pathsByMethodName = new(StringComparer.Ordinal);

    /// <summary>Every class name, published whole and cleared when a name appears or disappears.</summary>
    private volatile HashSet<string>? _classNames;

    private readonly Lock _gate = new();

    /// <summary>
    /// Replaces everything one file contributes. The previous contribution is read from the graph
    /// itself rather than passed in, so the index cannot drift out of step with a caller that
    /// supplied the wrong "before".
    ///
    /// REMOVAL IS AN EMPTY CONTRIBUTION — <c>Apply(path, [])</c> — and there is no separate method
    /// for it, because a second entry point would be one more place for the buckets to be emptied
    /// differently. <c>LanguageStore.ApplyIndexes</c> removes a file exactly this way, passing the
    /// gone record's empty class list, which is what every other index here means by an empty new
    /// set.
    /// </summary>
    public void Apply(string path, ImmutableArray<ClassSymbol> classes)
    {
        lock ( _gate )
        {
            if ( _classesByPath.TryGetValue(path, out ImmutableArray<ClassSymbol> previous) )
            {
                foreach ( ClassSymbol classSymbol in previous )
                {
                    if ( Detach(_pathsByClassName, classSymbol.KeyName, path) )
                    {
                        _classNames = null;
                    }

                    Detach(_pathsByQualifiedName, (classSymbol.Namespace, classSymbol.KeyName), path);

                    if ( classSymbol.ParentKeyName is not null )
                    {
                        Detach(_pathsByParentName, classSymbol.ParentKeyName, path);
                    }

                    foreach ( FunctionSymbol method in classSymbol.Methods )
                    {
                        Detach(_pathsByMethodName, method.KeyName, path);
                    }
                }
            }

            if ( classes.Length == 0 )
            {
                _classesByPath.Remove(path);
                return;
            }

            _classesByPath[path] = classes;

            foreach ( ClassSymbol classSymbol in classes )
            {
                if ( Attach(_pathsByClassName, classSymbol.KeyName, path) )
                {
                    _classNames = null;
                }

                Attach(_pathsByQualifiedName, (classSymbol.Namespace, classSymbol.KeyName), path);

                if ( classSymbol.ParentKeyName is not null )
                {
                    Attach(_pathsByParentName, classSymbol.ParentKeyName, path);
                }

                foreach ( FunctionSymbol method in classSymbol.Methods )
                {
                    Attach(_pathsByMethodName, method.KeyName, path);
                }
            }
        }
    }

    /// <summary>Paths of files declaring a class of this name (snapshot).</summary>
    public ImmutableArray<string> PathsDeclaring(string classKeyName)
    {
        return _pathsByClassName.TryGetValue(classKeyName, out Bucket? bucket) ? PathsOf(bucket) : [];
    }

    /// <summary>
    /// Paths of files declaring a class of this name INTO this namespace (snapshot) — the subset of
    /// <see cref="PathsDeclaring(string)"/> a namespace filter would keep. In a workspace where many
    /// copies of a class-declaring file each carry their own namespace, the bare-name list holds
    /// every copy, and a qualified parent link or <c>ns::Class</c> read all of them to keep one.
    /// </summary>
    public ImmutableArray<string> PathsDeclaring(string namespaceName, string classKeyName)
    {
        return _pathsByQualifiedName.TryGetValue((namespaceName, classKeyName), out Bucket? bucket) ? PathsOf(bucket) : [];
    }

    /// <summary>
    /// Names of classes directly inheriting from this one — one level, not the transitive set.
    /// Deduplicated, because an overlay and the raw script it shadows both declare the same child.
    /// </summary>
    public ImmutableArray<string> DirectChildren(string classKeyName)
    {
        if ( !_pathsByParentName.TryGetValue(classKeyName, out Bucket? bucket) )
        {
            return [];
        }

        return NamesOf(bucket, classKeyName, static (classSymbol, parent) =>
            string.Equals(classSymbol.ParentKeyName, parent, StringComparison.Ordinal));
    }

    /// <summary>
    /// Names of classes declaring a method of this name. This is what resolves an arrow call whose
    /// receiver has no known class — <c>[[o_scene]]-&gt;play()</c>, which is 155 of the 159 arrow
    /// calls in the stock scripts. One answer means the call can be navigated; several means the
    /// candidates get offered and nothing is diagnosed.
    /// </summary>
    public ImmutableArray<string> ClassesDeclaringMethod(string methodKeyName)
    {
        if ( !_pathsByMethodName.TryGetValue(methodKeyName, out Bucket? bucket) )
        {
            return [];
        }

        return NamesOf(bucket, methodKeyName, static (classSymbol, method) => DeclaresMethod(classSymbol, method));
    }

    /// <summary>Every class name in this language world (snapshot).</summary>
    public ImmutableArray<string> AllClassNames()
    {
        return [.. ClassNameSet()];
    }

    /// <summary>
    /// Every class name in this language world, as a set to test against — the same answer as
    /// <see cref="AllClassNames"/> without a copy. Built once per change to the set of names, so a
    /// caller asking once per file linted or per completion pays a lookup, not a pass over every
    /// class in the workspace. Never modified once handed out.
    /// </summary>
    public IReadOnlySet<string> ClassNameSet()
    {
        HashSet<string>? names = _classNames;
        if ( names is not null )
        {
            return names;
        }

        lock ( _gate )
        {
            names = _classNames;
            if ( names is null )
            {
                names = new HashSet<string>(_pathsByClassName.Keys, StringComparer.Ordinal);
                _classNames = names;
            }

            return names;
        }
    }

    /// <summary>
    /// Paths of every file declaring at least one class (snapshot). No production caller:
    /// <c>AllVisibleClasses</c> reads the asking file's imports by path instead, and the corpus and
    /// scale tests keep this as the walk that answer is checked against.
    /// </summary>
    public ImmutableArray<string> AllDeclaringPaths()
    {
        lock ( _gate )
        {
            return [.. _classesByPath.Keys];
        }
    }

    private ImmutableArray<string> PathsOf(Bucket bucket)
    {
        string[]? paths = bucket.Paths;
        if ( paths is null )
        {
            lock ( _gate )
            {
                paths = bucket.Paths;
                if ( paths is null )
                {
                    paths = [.. bucket.Set];
                    bucket.Paths = paths;
                }
            }
        }

        return ImmutableCollectionsMarshal.AsImmutableArray(paths);
    }

    /// <summary>
    /// The distinct names of the classes in a bucket's paths that <paramref name="matches"/> accepts,
    /// cached on the bucket. Deduplicated, because an overlay and the raw script it shadows declare
    /// the same class. Every write that could change the answer changes this bucket's set — a file's
    /// classes are detached from and attached to exactly the buckets they name — so clearing on a
    /// set change is enough.
    /// </summary>
    private ImmutableArray<string> NamesOf(Bucket bucket, string key, Func<ClassSymbol, string, bool> matches)
    {
        string[]? names = bucket.Names;
        if ( names is null )
        {
            lock ( _gate )
            {
                names = bucket.Names;
                if ( names is null )
                {
                    HashSet<string> found = new(StringComparer.Ordinal);
                    foreach ( string path in bucket.Set )
                    {
                        if ( !_classesByPath.TryGetValue(path, out ImmutableArray<ClassSymbol> classes) )
                        {
                            continue;
                        }

                        foreach ( ClassSymbol classSymbol in classes )
                        {
                            if ( matches(classSymbol, key) )
                            {
                                found.Add(classSymbol.KeyName);
                            }
                        }
                    }

                    names = [.. found];
                    bucket.Names = names;
                }
            }
        }

        return ImmutableCollectionsMarshal.AsImmutableArray(names);
    }

    /// <summary>Adds a path under a key, under <see cref="_gate"/>. True when the key is new.</summary>
    private static bool Attach<TKey>(ConcurrentDictionary<TKey, Bucket> index, TKey key, string path)
        where TKey : notnull
    {
        bool created = false;
        if ( !index.TryGetValue(key, out Bucket? bucket) )
        {
            bucket = new Bucket();
            index[key] = bucket;
            created = true;
        }

        if ( bucket.Set.Add(path) )
        {
            bucket.Paths = null;
            bucket.Names = null;
        }

        return created;
    }

    /// <summary>Removes a path from a key, under <see cref="_gate"/>. True when the key is gone.</summary>
    private static bool Detach<TKey>(ConcurrentDictionary<TKey, Bucket> index, TKey key, string path)
        where TKey : notnull
    {
        if ( !index.TryGetValue(key, out Bucket? bucket) || !bucket.Set.Remove(path) )
        {
            return false;
        }

        bucket.Paths = null;
        bucket.Names = null;
        if ( bucket.Set.Count > 0 )
        {
            return false;
        }

        index.TryRemove(key, out _);
        return true;
    }

    private static bool DeclaresMethod(ClassSymbol classSymbol, string methodKeyName)
    {
        foreach ( FunctionSymbol method in classSymbol.Methods )
        {
            if ( string.Equals(method.KeyName, methodKeyName, StringComparison.Ordinal) )
            {
                return true;
            }
        }

        return false;
    }
}
