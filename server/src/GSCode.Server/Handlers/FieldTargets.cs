using System.Collections.Immutable;
using GSCode.Core.Symbols;
using GSCode.Parser.Extraction;
using GSCode.Workspace.Database;

namespace GSCode.Server.Handlers;

/// <summary>A function a field holds: the key its write named, and the declaration that resolves to.</summary>
internal readonly record struct BoundFunction(SymbolKey Key, ResolvedFunction Function);

/// <summary>
/// What the scripts put IN a field, for the four requests that each ask a version of that question:
/// go-to-type-definition (what is it), go-to-implementation (which function answers this callback),
/// call hierarchy (who calls that function) and type hierarchy (what does that class inherit).
///
/// The four share one gap: a field is declared nowhere, so there is no symbol to hand any of them.
/// <see cref="FieldBinding"/> is that symbol, recorded at extraction and carried in the record, and
/// this is the one place a handler turns a field key into it, so the four cannot disagree about
/// which write counts or which declaration it names.
///
/// The cost is bounded by the field's WRITES, not by the workspace. The reference query is already
/// indexed by key (<c>LanguageStore.FilesReferencing</c>), a write is a small fraction of a
/// popular field's uses, and only the files holding one are read at all.
///
/// Every entry point comes in two forms: one that runs the reference query and one that takes a
/// set already fetched. Go-to-implementation needs the writes themselves as well as what they
/// bind, and a second query would pay the indexed lookup, the shadow rule and the include scoping
/// twice — and answer from two sets nothing guarantees are the same.
/// </summary>
internal static class FieldTargets
{
    /// <summary>The reference set the other entry points read, for a caller that has none yet.</summary>
    public static ImmutableArray<(ScriptRecord Record, ReferenceEntry Entry)> ReferencesTo(
        NavigationSupport support, NavigationTarget target, SymbolKey field)
    {
        return support.FindAllReferences(target, field, ReferenceKind.FieldAccess);
    }

    /// <summary>The declarations of every function bound to this field.</summary>
    public static ImmutableArray<ResolvedFunction> FunctionsOf(
        NavigationSupport support, NavigationTarget target, SymbolKey field, CancellationToken cancellationToken)
    {
        return FunctionsOf(target, ReferencesTo(support, target, field), field, cancellationToken);
    }

    /// <summary>The same, from a reference set the caller already has.</summary>
    public static ImmutableArray<ResolvedFunction> FunctionsOf(
        NavigationTarget target,
        ImmutableArray<(ScriptRecord Record, ReferenceEntry Entry)> references,
        SymbolKey field,
        CancellationToken cancellationToken)
    {
        ImmutableArray<ResolvedFunction>.Builder found = ImmutableArray.CreateBuilder<ResolvedFunction>();
        foreach ( BoundFunction bound in BoundFunctionsOf(target, references, field, cancellationToken) )
        {
            found.Add(bound.Function);
        }

        return found.ToImmutable();
    }

    /// <summary>
    /// Every function bound to this field, each with the key its write named — for call hierarchy,
    /// whose item carries the key in <c>Data</c> so the next step can resolve without re-reading the
    /// position. The key a write NAMED is not the one rebuilt from its declaration: an unqualified
    /// <c>&amp;foo</c> keys with a null namespace, which is what the union behind find-references is
    /// built on.
    /// </summary>
    public static ImmutableArray<BoundFunction> BoundFunctionsOf(
        NavigationSupport support, NavigationTarget target, SymbolKey field, CancellationToken cancellationToken)
    {
        return BoundFunctionsOf(target, ReferencesTo(support, target, field), field, cancellationToken);
    }

    /// <summary>
    /// The same, from a reference set the caller already has.
    ///
    /// Each target is resolved in the file that WROTE it, as a call written there would be:
    /// <c>level.cb = ::handler;</c> means the handler that file reaches, whichever file later calls
    /// <c>[[ level.cb ]]()</c>, and <c>maps\_util::handler</c> means maps\_util's. Asked from the reader's
    /// file instead, the answer was every same-named function in the workspace on an include dialect.
    /// Deduplicated by declaration: one handler stored from four game modes is one implementation.
    /// </summary>
    public static ImmutableArray<BoundFunction> BoundFunctionsOf(
        NavigationTarget target,
        ImmutableArray<(ScriptRecord Record, ReferenceEntry Entry)> references,
        SymbolKey field,
        CancellationToken cancellationToken)
    {
        ImmutableArray<BoundFunction>.Builder found = ImmutableArray.CreateBuilder<BoundFunction>();
        if ( field.Kind != SymbolKind.Field )
        {
            return found.ToImmutable();
        }

        HashSet<string> visited = new(StringComparer.Ordinal);
        HashSet<(string Path, GSCode.Core.Text.TextRange Range)> seen = [];

        foreach ( (ScriptRecord Record, ReferenceEntry Entry) reference in references )
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Only a file that WRITES the field can bind it, and a file is read once however many
            // times it writes.
            ScriptRecord writer = reference.Record;
            if ( reference.Entry.Kind != ReferenceKind.FieldWrite || !visited.Add(writer.Path) )
            {
                continue;
            }

            LanguageStore? store = StoreHolding(target, writer);
            if ( store is null )
            {
                continue;
            }

            foreach ( FieldBinding binding in writer.FieldBindings )
            {
                if ( binding.Field != field || binding.Target.Kind != SymbolKind.Function )
                {
                    continue;
                }

                foreach ( ResolvedFunction resolved in DatabaseQueries.FunctionsTheCallReaches(
                    store,
                    writer.ContextId,
                    writer.Path,
                    binding.Target.Namespace,
                    binding.Target.Name,
                    PathCallReference.At(writer.PathCallTargets, binding.TargetRange),
                    writer.DeclaredNamespaces) )
                {
                    if ( seen.Add((resolved.Record.Path, resolved.Function.NameRange)) )
                    {
                        found.Add(new BoundFunction(binding.Target, resolved));
                    }
                }
            }
        }

        return found.ToImmutable();
    }

    /// <summary>The language store holding <paramref name="record"/>, of those the request may read.</summary>
    private static LanguageStore? StoreHolding(NavigationTarget target, ScriptRecord record)
    {
        foreach ( LanguageStore store in target.Stores )
        {
            if ( store.TryGet(record.Path, out _) )
            {
                return store;
            }
        }

        return null;
    }

    /// <summary>The declarations of every class bound to this field.</summary>
    public static ImmutableArray<ResolvedClass> ClassesOf(
        NavigationSupport support, NavigationTarget target, SymbolKey field, CancellationToken cancellationToken)
    {
        return ClassesOf(target, ReferencesTo(support, target, field), field, cancellationToken);
    }

    /// <summary>The same, from a reference set the caller already has.</summary>
    public static ImmutableArray<ResolvedClass> ClassesOf(
        NavigationTarget target,
        ImmutableArray<(ScriptRecord Record, ReferenceEntry Entry)> references,
        SymbolKey field,
        CancellationToken cancellationToken)
    {
        ImmutableArray<ResolvedClass>.Builder found = ImmutableArray.CreateBuilder<ResolvedClass>();

        foreach ( SymbolKey bound in BoundKeys(references, field, SymbolKind.Class, cancellationToken) )
        {
            found.AddRange(DatabaseQueries.LookupClasses(
                target.Store, target.ContextId, namespaceName: null, bound.Name));
        }

        return found.ToImmutable();
    }

    /// <summary>
    /// Every distinct symbol of <paramref name="kind"/> bound to <paramref name="field"/>, or
    /// empty when the key is not a field or nothing binds it.
    ///
    /// Deduplicated by key rather than by site: <c>level.callback = &amp;on_damage</c> written in
    /// four game modes is one implementation, and offering it four times is a picker the reader has
    /// to squint at rather than an answer.
    /// </summary>
    private static ImmutableArray<SymbolKey> BoundKeys(
        ImmutableArray<(ScriptRecord Record, ReferenceEntry Entry)> references,
        SymbolKey field,
        SymbolKind kind,
        CancellationToken cancellationToken)
    {
        if ( field.Kind != SymbolKind.Field )
        {
            return [];
        }

        HashSet<string> visited = new(StringComparer.Ordinal);
        HashSet<SymbolKey> seen = [];
        ImmutableArray<SymbolKey>.Builder found = ImmutableArray.CreateBuilder<SymbolKey>();

        foreach ( (ScriptRecord Record, ReferenceEntry Entry) reference in references )
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Only a file that WRITES the field can bind it, and a file is read once however many
            // times it writes.
            if ( reference.Entry.Kind != ReferenceKind.FieldWrite || !visited.Add(reference.Record.Path) )
            {
                continue;
            }

            foreach ( FieldBinding binding in reference.Record.FieldBindings )
            {
                if ( binding.Field != field || binding.Target.Kind != kind )
                {
                    continue;
                }

                if ( seen.Add(binding.Target) )
                {
                    found.Add(binding.Target);
                }
            }
        }

        return found.ToImmutable();
    }
}
