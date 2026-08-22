using System.Collections.Immutable;
using GSCode.Core.Symbols;

namespace GSCode.Workspace.Database;

/// <summary>
/// Which files declare which classes, who inherits from whom, and which classes declare a given
/// method name. One instance per <see cref="LanguageStore"/>, so GSC/CSC isolation stays structural
/// — <c>class cScene</c> in <c>scene_shared.gsc</c> and in <c>scene_shared.csc</c> live in separate
/// graphs and cannot see each other.
///
/// Exists because every class question used to be a full linear scan of every record in the store:
/// <c>LookupClasses</c> per parent link, <c>AllVisibleClasses</c> per keystroke, and
/// <c>NamespaceUsageLint</c>'s class-name set once PER FILE LINTED, which is a store scan per file
/// across the whole workspace. Method resolution walks parent chains constantly, so it would have
/// multiplied that; instead it makes those four queries dictionary hits.
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
/// </summary>
public sealed class ClassGraph
{
    private readonly Dictionary<string, ImmutableArray<ClassSymbol>> _classesByPath = new(StringComparer.Ordinal);
    private readonly Dictionary<string, HashSet<string>> _pathsByClassName = new(StringComparer.Ordinal);
    private readonly Dictionary<string, HashSet<string>> _pathsByParentName = new(StringComparer.Ordinal);
    private readonly Dictionary<string, HashSet<string>> _pathsByMethodName = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();

    /// <summary>
    /// Replaces everything one file contributes. The previous contribution is read from the graph
    /// itself rather than passed in, so the index cannot drift out of step with a caller that
    /// supplied the wrong "before" — and <see cref="Remove"/> is just an empty contribution.
    /// </summary>
    public void Apply(string path, ImmutableArray<ClassSymbol> classes)
    {
        lock ( _gate )
        {
            if ( _classesByPath.TryGetValue(path, out ImmutableArray<ClassSymbol> previous) )
            {
                foreach ( ClassSymbol classSymbol in previous )
                {
                    Detach(_pathsByClassName, classSymbol.KeyName, path);

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
                Attach(_pathsByClassName, classSymbol.KeyName, path);

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

    /// <summary>Drops everything a file contributed (it was deleted from disk).</summary>
    public void Remove(string path)
    {
        Apply(path, []);
    }

    /// <summary>Paths of files declaring a class of this name (snapshot).</summary>
    public ImmutableArray<string> PathsDeclaring(string classKeyName)
    {
        lock ( _gate )
        {
            if ( !_pathsByClassName.TryGetValue(classKeyName, out HashSet<string>? paths) )
            {
                return [];
            }

            return [.. paths];
        }
    }

    /// <summary>
    /// Names of classes directly inheriting from this one — one level, not the transitive set.
    /// Deduplicated, because an overlay and the raw script it shadows both declare the same child.
    /// </summary>
    public ImmutableArray<string> DirectChildren(string classKeyName)
    {
        lock ( _gate )
        {
            if ( !_pathsByParentName.TryGetValue(classKeyName, out HashSet<string>? paths) )
            {
                return [];
            }

            HashSet<string> names = new(StringComparer.Ordinal);
            foreach ( string path in paths )
            {
                if ( !_classesByPath.TryGetValue(path, out ImmutableArray<ClassSymbol> classes) )
                {
                    continue;
                }

                foreach ( ClassSymbol classSymbol in classes )
                {
                    if ( string.Equals(classSymbol.ParentKeyName, classKeyName, StringComparison.Ordinal) )
                    {
                        names.Add(classSymbol.KeyName);
                    }
                }
            }

            return [.. names];
        }
    }

    /// <summary>
    /// Names of classes declaring a method of this name. This is what resolves an arrow call whose
    /// receiver has no known class — <c>[[o_scene]]-&gt;play()</c>, which is 155 of the 159 arrow
    /// calls in the stock scripts. One answer means the call can be navigated; several means the
    /// candidates get offered and nothing is diagnosed.
    /// </summary>
    public ImmutableArray<string> ClassesDeclaringMethod(string methodKeyName)
    {
        lock ( _gate )
        {
            if ( !_pathsByMethodName.TryGetValue(methodKeyName, out HashSet<string>? paths) )
            {
                return [];
            }

            HashSet<string> names = new(StringComparer.Ordinal);
            foreach ( string path in paths )
            {
                if ( !_classesByPath.TryGetValue(path, out ImmutableArray<ClassSymbol> classes) )
                {
                    continue;
                }

                foreach ( ClassSymbol classSymbol in classes )
                {
                    foreach ( FunctionSymbol method in classSymbol.Methods )
                    {
                        if ( string.Equals(method.KeyName, methodKeyName, StringComparison.Ordinal) )
                        {
                            names.Add(classSymbol.KeyName);
                            break;
                        }
                    }
                }
            }

            return [.. names];
        }
    }

    /// <summary>Every class name in this language world (snapshot).</summary>
    public ImmutableArray<string> AllClassNames()
    {
        lock ( _gate )
        {
            return [.. _pathsByClassName.Keys];
        }
    }

    /// <summary>
    /// Paths of every file declaring at least one class (snapshot). Lets a caller that wants all
    /// visible classes iterate the ~20 files that have one instead of every record in the store.
    /// </summary>
    public ImmutableArray<string> AllDeclaringPaths()
    {
        lock ( _gate )
        {
            return [.. _classesByPath.Keys];
        }
    }

    private static void Attach(Dictionary<string, HashSet<string>> index, string key, string path)
    {
        if ( !index.TryGetValue(key, out HashSet<string>? paths) )
        {
            paths = new HashSet<string>(StringComparer.Ordinal);
            index[key] = paths;
        }

        paths.Add(path);
    }

    private static void Detach(Dictionary<string, HashSet<string>> index, string key, string path)
    {
        if ( !index.TryGetValue(key, out HashSet<string>? paths) )
        {
            return;
        }

        paths.Remove(path);
        if ( paths.Count == 0 )
        {
            index.Remove(key);
        }
    }
}
