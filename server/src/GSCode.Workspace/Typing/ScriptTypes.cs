using System.Collections.Frozen;
using System.Collections.Immutable;
using GSCode.Core.Symbols;
using GSCode.Parser.Syntax.Ast;

namespace GSCode.Workspace.Typing;

/// <summary>
/// Every value the flow pass worked out for one file, keyed by the expression that produced it.
///
/// For a caller that asks "what is THIS node" about a node no assignment names: the type-mismatch
/// lint asks about a <c>foreach</c> collection and each vector component, and the inlay hints ask
/// about the pointer an <c>-&gt;</c> or <c>[[ ]]</c> call goes through.
///
/// Keyed by REFERENCE, deliberately. Every AST node is a record, so structural equality would make
/// the two <c>0</c> literals in <c>( 0, 0, 1 )</c> the same key, and the second write would silently
/// overwrite the first with a value belonging somewhere else. Reference identity is what makes a
/// position in the tree the thing being asked about.
/// </summary>
public sealed class ScriptTypes
{
    /// <summary>
    /// Read-only by TYPE rather than by convention. The walk hands over the dictionary it built and
    /// keeps no reference to it, so exposing a mutable one would let a consumer edit a result that
    /// reads as a finished answer — and <see cref="Empty"/> is a shared static, where that would be
    /// a cross-caller bug rather than a local one.
    /// </summary>
    private readonly IReadOnlyDictionary<ExprNode, ScrValue> _values;

    internal ScriptTypes(
        IReadOnlyDictionary<ExprNode, ScrValue> values,
        ImmutableArray<InferredAssignment> assignments,
        ImmutableArray<FieldWrite> fieldWrites)
    {
        _values = values;
        Assignments = assignments;
        FieldWrites = fieldWrites;
    }

    /// <summary>Nothing was analysed — an empty file, or a language with no flow pass.</summary>
    public static ScriptTypes Empty { get; } = new(FrozenDictionary<ExprNode, ScrValue>.Empty, [], []);

    /// <summary>The same assignment list the hint and hover surfaces read.</summary>
    public ImmutableArray<InferredAssignment> Assignments { get; }

    /// <summary>The same field-write list the two typing lints read.</summary>
    public ImmutableArray<FieldWrite> FieldWrites { get; }

    /// <summary>How many expressions were typed.</summary>
    public int Count
    {
        get { return _values.Count; }
    }

    /// <summary>
    /// The value of one expression. Returns <see cref="ScrValue.Unknown"/> for a node the walk never
    /// reached — a statement below a cursor stop, or an expression form nothing descends into.
    /// </summary>
    public ScrValue ValueOf(ExprNode node)
    {
        return _values.TryGetValue(node, out ScrValue value) ? value : ScrValue.Unknown;
    }

    public bool TryGetValue(ExprNode node, out ScrValue value)
    {
        return _values.TryGetValue(node, out value);
    }

    /// <summary>Every typed expression, for a sweep over the whole map.</summary>
    public IEnumerable<KeyValuePair<ExprNode, ScrValue>> All
    {
        get { return _values; }
    }
}
