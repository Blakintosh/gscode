using System.Collections.Immutable;
using GSCode.Core.Diagnostics;
using GSCode.Core.Symbols;
using GSCode.Parser;
using GSCode.Workspace.Database;

namespace GSCode.Workspace.Analysis;

/// <summary>
/// Reports a class that inherits from itself, directly or through a chain.
///
/// <c>class A : B</c> with <c>class B : A</c> has no valid layout, so nothing downstream can resolve
/// it — and every consumer that walks a parent chain (completion, type hierarchy, member lookup)
/// has to defend against the loop separately. Naming it once at the declaration is the only place
/// the author can act on it.
///
/// Only reported on a class declared in THIS file, so the same cycle is reported once at each end
/// rather than once per file that can see either class.
/// </summary>
public static class ClassCycleLint
{
    /// <summary>Bound on the chain walk, so a cycle the resolver missed still cannot spin here.</summary>
    private const int MaxDepth = 64;

    public static ImmutableArray<Diagnostic> Analyze(
        ParseResult result, LanguageStore store, string contextId)
    {
        if ( result.Extraction.Classes.Length == 0 )
        {
            return [];
        }

        ImmutableArray<Diagnostic>.Builder diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();

        foreach ( ClassSymbol declared in result.Extraction.Classes )
        {
            if ( declared.SourceFile.Length > 0 )
            {
                // Arrived through an insert; its declaration lives in another file.
                continue;
            }

            if ( TryFindCycle(declared, store, contextId, out string through) )
            {
                diagnostics.Add(Diagnostic.Create(
                    declared.NameRange,
                    DiagnosticSeverity.Error,
                    GscDiagnosticCode.ClassInheritanceCycle,
                    declared.Name,
                    through));
            }
        }

        return diagnostics.ToImmutable();
    }

    /// <summary>
    /// Walks the parent chain looking for the starting class. Reports the chain that got back to it,
    /// because "A inherits from itself" is not actionable without knowing which link to cut.
    /// </summary>
    private static bool TryFindCycle(
        ClassSymbol start, LanguageStore store, string contextId, out string through)
    {
        through = "";

        List<string> chain = [];
        HashSet<string> visited = new(StringComparer.OrdinalIgnoreCase);
        string? parent = start.ParentKeyName;

        for ( int depth = 0; depth < MaxDepth; depth++ )
        {
            if ( string.IsNullOrEmpty(parent) )
            {
                return false;
            }

            if ( string.Equals(parent, start.KeyName, StringComparison.OrdinalIgnoreCase) )
            {
                through = chain.Count == 0 ? start.Name : string.Join(" -> ", chain);
                return true;
            }

            // A cycle that does NOT pass through the starting class — B : C, C : B, reached from
            // A : B. It is a real cycle, but B and C are where it is reported, so stop rather than
            // blame A for it.
            if ( !visited.Add(parent) )
            {
                return false;
            }

            chain.Add(parent);

            ImmutableArray<ResolvedClass> parents = DatabaseQueries.LookupClasses(
                store, contextId, namespaceName: null, parent);

            if ( parents.Length == 0 )
            {
                return false;
            }

            parent = parents[0].Class.ParentKeyName;
        }

        return false;
    }
}
