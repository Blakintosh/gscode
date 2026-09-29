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

    /// <summary>
    /// The distinct literals of one kind used by at least one file <paramref name="visible"/> accepts,
    /// each handed to <paramref name="found"/> — see <see cref="PackedInvertedIndex{TKey}.ForEachKey"/>.
    /// </summary>
    public void Literals(SymbolKind kind, Func<string, bool> visible, Action<VocabularyName> found)
    {
        _literals.ForEachKey(key => key.Kind == kind, visible, (key, files) => found(new VocabularyName(key.Name, files)));
    }

    /// <summary>
    /// The distinct field names assigned in at least one file <paramref name="visible"/> accepts —
    /// on <paramref name="ownerName"/> only when one is given, compared ordinally. Spelled as WRITTEN:
    /// <c>level.foo</c> and <c>level.Foo</c> are two names here, each counted on its own, and choosing
    /// between them is the completion list's business, not the index's.
    /// </summary>
    public List<VocabularyName> FieldNames(string? ownerName, Func<string, bool> visible)
    {
        // Across every owner one spelling can arrive once per owner — `self.health` and
        // `level.health` — so the counts are summed per spelling rather than listed twice.
        Dictionary<string, int> files = new(StringComparer.Ordinal);
        _fields.ForEachKey(
            key => ownerName is null || string.Equals(key.OwnerName, ownerName, StringComparison.Ordinal),
            visible,
            (key, count) =>
            {
                files.TryGetValue(key.Name, out int sum);
                files[key.Name] = sum + count;
            });

        List<VocabularyName> names = new(files.Count);
        foreach ( KeyValuePair<string, int> name in files )
        {
            names.Add(new VocabularyName(name.Key, name.Value));
        }

        return names;
    }
}

/// <summary>
/// A name from the workspace's vocabulary, and how many files write it — all of them, not only the
/// ones the asker can see: a measure of how widely it is used, for ranking a list, not a count of
/// anything the asker could reach.
/// </summary>
public readonly record struct VocabularyName(string Name, int Files);
