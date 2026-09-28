using GSCode.Core.Symbols;

namespace GSCode.Workspace.Database;

/// <summary>
/// The workspace's vocabulary for the two completion lists that offer names written ANYWHERE: the
/// string, hash and localized literals it uses, and the fields it assigns (<c>owner.name = ...</c>).
/// Each maps the name to the files using it, so a list can be built from the distinct names
/// visible to the asking file rather than from every occurrence in every record.
///
/// A walk of every record per request — every reference for literals, every function's assignments
/// for fields — grows with the workspace, while distinct names barely do: a large workspace mostly
/// reuses the same notify strings, tags and fields. At 50,000 files that walk put literal
/// completion at 190 ms p99 against a 10 ms budget (PERF.md, the scale section).
///
/// A literal is indexed only when literal completion could offer it — a plain
/// <see cref="ReferenceKind.Literal"/> not produced by a macro body. The name-shape filter the list
/// applies stays at the query, where it is cheap.
/// </summary>
public sealed class VocabularyIndex
{
    /// <summary>What one file contributes, built outside the caller's write gate.</summary>
    public sealed class Contribution
    {
        public static Contribution None { get; } = new([], []);

        public Contribution(HashSet<SymbolKey> literals, HashSet<(string OwnerName, string Name)> fields)
        {
            Literals = literals;
            Fields = fields;
        }

        public HashSet<SymbolKey> Literals { get; }

        public HashSet<(string OwnerName, string Name)> Fields { get; }
    }

    private readonly PackedInvertedIndex<SymbolKey> _literals = new(EqualityComparer<SymbolKey>.Default);

    private readonly PackedInvertedIndex<(string OwnerName, string Name)> _fields =
        new(EqualityComparer<(string OwnerName, string Name)>.Default);

    public static Contribution Of(ScriptRecord? record)
    {
        if ( record is null )
        {
            return Contribution.None;
        }

        HashSet<SymbolKey> literals = [];
        foreach ( ReferenceEntry entry in record.References )
        {
            if ( entry.Kind == ReferenceKind.Literal && !entry.FromMacro )
            {
                literals.Add(entry.Key);
            }
        }

        HashSet<(string OwnerName, string Name)> fields = [];
        foreach ( FunctionSymbol function in record.Functions )
        {
            foreach ( AssignmentSymbol assignment in function.Assignments )
            {
                // An empty owner marks a plain local, which is not a field at all.
                if ( assignment.OwnerName.Length > 0 )
                {
                    fields.Add((assignment.OwnerName, assignment.Name));
                }
            }
        }

        if ( literals.Count == 0 && fields.Count == 0 )
        {
            return Contribution.None;
        }

        return new Contribution(literals, fields);
    }

    /// <summary>Replaces one file's contribution.</summary>
    public void Apply(string path, Contribution previous, Contribution next)
    {
        _literals.Apply(path, previous.Literals, next.Literals);
        _fields.Apply(path, previous.Fields, next.Fields);
    }

    /// <summary>The distinct literals of one kind used by at least one file <paramref name="visible"/> accepts.</summary>
    public List<string> Literals(SymbolKind kind, Func<string, bool> visible)
    {
        List<SymbolKey> keys = [];
        _literals.CollectKeys(key => key.Kind == kind, visible, keys);

        List<string> names = new(keys.Count);
        foreach ( SymbolKey key in keys )
        {
            names.Add(key.Name);
        }

        return names;
    }

    /// <summary>
    /// The distinct field names assigned in at least one file <paramref name="visible"/> accepts —
    /// on <paramref name="ownerName"/> only when one is given, compared ordinally.
    /// </summary>
    public List<string> FieldNames(string? ownerName, Func<string, bool> visible)
    {
        List<(string OwnerName, string Name)> keys = [];
        _fields.CollectKeys(
            key => ownerName is null || string.Equals(key.OwnerName, ownerName, StringComparison.Ordinal),
            visible,
            keys);

        List<string> names = new(keys.Count);
        foreach ( (string OwnerName, string Name) key in keys )
        {
            names.Add(key.Name);
        }

        return names;
    }
}
