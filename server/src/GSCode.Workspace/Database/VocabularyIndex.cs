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

        public Contribution(HashSet<SymbolKey> literals, HashSet<string> fields)
        {
            Literals = literals;
            Fields = fields;
        }

        public HashSet<SymbolKey> Literals { get; }

        /// <summary>
        /// The field names this file assigns, as written, on ANY owner — see <see cref="FieldNames"/>
        /// for why the owner is not part of the key.
        /// </summary>
        public HashSet<string> Fields { get; }
    }

    private readonly PackedInvertedIndex<SymbolKey> _literals = new(EqualityComparer<SymbolKey>.Default);

    private readonly PackedInvertedIndex<string> _fields = new(StringComparer.Ordinal);

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

        HashSet<string> fields = new(StringComparer.Ordinal);
        foreach ( FunctionSymbol function in record.Functions )
        {
            foreach ( AssignmentSymbol assignment in function.Assignments )
            {
                // An empty owner marks a plain local, which is not a field at all.
                if ( assignment.OwnerName.Length > 0 )
                {
                    fields.Add(assignment.Name);
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
    /// The distinct field names assigned, on any owner, in at least one file <paramref name="visible"/>
    /// accepts. Spelled as WRITTEN: <c>level.foo</c> and <c>level.Foo</c> are two names here, each
    /// counted on its own, and choosing between them is the completion list's business.
    ///
    /// Not keyed by owner. The name before the dot is a variable, not the object: <c>self</c> is
    /// whatever a function was called on, and after <c>blah = level;</c> a <c>blah.</c> is level, so
    /// a field list scoped to the variable name hid fields the object really has.
    /// </summary>
    public List<VocabularyName> FieldNames(Func<string, bool> visible)
    {
        List<VocabularyName> names = [];
        _fields.ForEachKey(static _ => true, visible, (name, files) => names.Add(new VocabularyName(name, files)));
        return names;
    }
}

/// <summary>
/// A name from the workspace's vocabulary, and how many files write it — all of them, not only the
/// ones the asker can see: a measure of how widely it is used, for ranking a list, not a count of
/// anything the asker could reach.
/// </summary>
public readonly record struct VocabularyName(string Name, int Files);
