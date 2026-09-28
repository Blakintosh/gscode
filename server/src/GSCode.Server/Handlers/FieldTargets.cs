using System.Collections.Immutable;
using GSCode.Core.Symbols;
using GSCode.Workspace.Database;

namespace GSCode.Server.Handlers;

/// <summary>
/// What the scripts put IN a field, for the four requests that each ask a version of that question:
/// go-to-type-definition (what is it), go-to-implementation (which function answers this callback),
/// call hierarchy (who calls that function) and type hierarchy (what does that class inherit).
///
/// All four used to decline on a field, and it read as four unrelated omissions. It is one: a field
/// is declared nowhere, so there was no symbol to hand any of them. <see cref="FieldBinding"/> is
/// that symbol, recorded at extraction and carried in the record, and this is the one place a
/// handler turns a field key into it — so the four cannot come to disagree about which write counts
/// or which declaration it names.
///
/// The cost is bounded by the field's WRITES, not by the workspace. The reference query is already
/// indexed by key (<c>LanguageStore.FilesReferencing</c>), a write is a small fraction of a
/// popular field's uses, and only the files holding one are read at all.
/// </summary>
internal static class FieldTargets
{
    /// <summary>
    /// Every distinct symbol of <paramref name="kind"/> bound to <paramref name="field"/> anywhere
    /// the asking document can see, or empty when the key is not a field or nothing binds it.
    ///
    /// Deduplicated by key rather than by site: <c>level.callback = &amp;on_damage</c> written in
    /// four game modes is one implementation, and offering it four times is a picker the reader has
    /// to squint at rather than an answer.
    /// </summary>
    public static ImmutableArray<SymbolKey> Of(
        NavigationSupport support,
        NavigationTarget target,
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

        foreach ( (ScriptRecord record, ReferenceEntry entry) in
            support.FindAllReferences(target, field, ReferenceKind.FieldAccess) )
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Only a file that WRITES the field can bind it, and a file is read once however many
            // times it writes.
            if ( entry.Kind != ReferenceKind.FieldWrite || !visited.Add(record.Path) )
            {
                continue;
            }

            foreach ( FieldBinding binding in record.FieldBindings )
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

    /// <summary>
    /// The declarations a bound FUNCTION key names. A null namespace on the key means "written
    /// unqualified", which <see cref="DatabaseQueries.LookupFunctions"/> already reads as "any
    /// namespace this file can reach" — the same treatment a call of that name gets.
    /// </summary>
    public static ImmutableArray<ResolvedFunction> Functions(NavigationTarget target, SymbolKey function)
    {
        return DatabaseQueries.LookupFunctions(
            target.Store,
            target.ContextId,
            target.Path,
            function.Namespace,
            function.Name,
            askingNamespaces: target.Namespaces);
    }

    /// <summary>The declarations a bound CLASS key names.</summary>
    public static IEnumerable<ResolvedClass> Classes(NavigationTarget target, SymbolKey classKey)
    {
        return DatabaseQueries.LookupClasses(target.Store, target.ContextId, namespaceName: null, classKey.Name);
    }
}
