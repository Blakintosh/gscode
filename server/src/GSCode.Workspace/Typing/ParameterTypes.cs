using System.Collections.Immutable;
using GSCode.Core.Symbols;
using GSCode.Parser;
using GSCode.Parser.Syntax;
using GSCode.Parser.Syntax.Ast;

namespace GSCode.Workspace.Typing;

/// <summary>
/// What a function's parameters hold, inferred from the arguments its callers pass.
///
/// The flow pass types a function in isolation, so a parameter starts as unknown carrying
/// <see cref="ScrImprecision.UntypedParameter"/> — and for a dialect transpiler that is the one gap
/// that actually blocks work. Whether an array parameter is mutated by its callee is the only
/// behavioural difference between Black Ops III and the earlier games, so "is this parameter an
/// array" has to be answerable before any of it can be translated.
///
/// SAME FILE ONLY, and the limit is structural rather than a shortcut not taken. A call site's
/// ARGUMENTS live in the caller's syntax tree, and the database stores extraction records —
/// symbols, references, dependencies — not trees. Reading arguments from another file means
/// re-parsing it, measured at roughly 44 ms per file, or about 43 seconds for Black Ops III's 980
/// scripts on every query. That needs a cached argument index to be worth having, which is its own
/// piece of work; see FOLLOWUPS.
///
/// What the same-file half does cover is the case that motivates the whole exercise: a helper
/// declared and called in one script, which is most of what a per-file rewrite has to reason about.
/// </summary>
public static class ParameterTypes
{
    /// <summary>
    /// Reports what each function's parameters hold, per position, from the arguments passed at
    /// every call to it in this file.
    ///
    /// It RETURNS those values rather than applying them: nothing here re-types the functions with
    /// their parameters seeded. A caller wanting that runs its own pass with these as the starting
    /// environment, and would resolve one further level of indirection by doing so — an argument
    /// that is itself the caller's own parameter contributes nothing on this pass and says so,
    /// carrying <see cref="ScrImprecision.UntypedParameter"/>.
    /// </summary>
    public static ImmutableDictionary<string, ImmutableArray<ScrValue>> Infer(ParseResult result, FlowTyper typer)
    {
        // The typing walk runs with parameters unknown, which is what makes this a first
        // approximation: an argument that is itself the caller's own parameter contributes nothing,
        // and stays that way. Seeding the parameters found here and typing again would resolve one
        // more level, at the cost of another full walk per level — worth doing only if a caller
        // turns out to need it, and the imprecision reason says plainly when a value was unresolved.
        return Infer(result, typer.InferValues(result));
    }

    /// <summary>
    /// The same, against a map the caller already has.
    ///
    /// The overload that matters for anything doing real work. A rewriter wants BOTH the per-node
    /// values and the parameter signatures, and the convenience overload above builds a whole
    /// file's map to read the arguments out of it — so a caller that has one already would
    /// otherwise pay for a second walk to get an answer derivable from the first.
    /// </summary>
    public static ImmutableDictionary<string, ImmutableArray<ScrValue>> Infer(ParseResult result, ScriptTypes typed)
    {
        Dictionary<string, Signature> byName = new(StringComparer.OrdinalIgnoreCase);
        CollectCalls(result.Tree.Root, typed, byName);

        ImmutableDictionary<string, ImmutableArray<ScrValue>>.Builder inferred =
            ImmutableDictionary.CreateBuilder<string, ImmutableArray<ScrValue>>(StringComparer.OrdinalIgnoreCase);

        foreach ( KeyValuePair<string, Signature> entry in byName )
        {
            inferred[entry.Key] = [.. entry.Value.Positions];
        }

        return inferred.ToImmutable();
    }

    /// <summary>
    /// What every call to one name has passed so far, and HOW MANY calls that is.
    ///
    /// The count is the part that is easy to leave out and wrong to. A position first seen on the
    /// third call was omitted by the two before it, and only the count knows that — see
    /// <see cref="Record"/>.
    /// </summary>
    private sealed class Signature
    {
        public List<ScrValue> Positions { get; } = [];

        public int Calls { get; set; }
    }

    /// <summary>
    /// Finds every call in the file and unions its argument values into the entry for the called
    /// name, by position.
    ///
    /// Keyed by bare NAME, which is as precise as a same-file view can be. Two functions of one name
    /// in a file is already <c>gscode-4005</c>, so the collision this ignores is one the workspace
    /// reports separately.
    /// </summary>
    private static void CollectCalls(AstNode node, ScriptTypes types, Dictionary<string, Signature> byName)
    {
        if ( node is CallNode call && CalleeName(call) is string name )
        {
            Record(name, call.Arguments, types, byName);
        }

        foreach ( AstNode child in AstSearch.ChildrenOf(node) )
        {
            CollectCalls(child, types, byName);
        }
    }

    /// <summary>
    /// Folds one call's arguments into the running signature for that name.
    ///
    /// Omission has to be handled in BOTH directions or the answer depends on the order the calls
    /// happen to appear in, which is the bug this shape exists to prevent:
    ///
    /// - This call passing FEWER arguments than an earlier one widens the trailing positions with
    ///   undefined. Legal in GSC — the missing ones simply are undefined — and a fact a rewriter
    ///   must not lose.
    /// - This call passing MORE means every earlier call omitted the new positions, so they are
    ///   seeded with undefined as they are added. Leaving this half out made `h( 1 ); h( 1, 2 );`
    ///   report the second parameter as a plain int while the reverse order reported int|undefined.
    /// </summary>
    private static void Record(
        string name,
        ImmutableArray<ExprNode> arguments,
        ScriptTypes types,
        Dictionary<string, Signature> byName)
    {
        if ( !byName.TryGetValue(name, out Signature? signature) )
        {
            signature = new Signature();
            byName[name] = signature;
        }

        signature.Calls++;
        List<ScrValue> positions = signature.Positions;

        for ( int index = 0; index < arguments.Length; index++ )
        {
            ScrValue value = types.ValueOf(arguments[index]);

            if ( index < positions.Count )
            {
                // Another caller passing something else widens the parameter rather than replacing
                // it: the function must accept both.
                positions[index] = ScrValue.Union(positions[index], value);
                continue;
            }

            // A position no earlier call reached. If there WERE earlier calls, all of them left it
            // undefined.
            positions.Add(signature.Calls > 1
                ? ScrValue.Union(value, ScrValue.Of(ScrTypeSet.Undefined))
                : value);
        }

        for ( int index = arguments.Length; index < positions.Count; index++ )
        {
            positions[index] = ScrValue.Union(positions[index], ScrValue.Of(ScrTypeSet.Undefined));
        }
    }

    /// <summary>
    /// The called name, for the call shapes that can name a script function in this file.
    ///
    /// A pointer dereference (<c>[[ f ]]()</c>) names nothing statically and an arrow call is a
    /// class method, so neither contributes.
    /// </summary>
    private static string? CalleeName(CallNode call)
    {
        switch ( call.Callee )
        {
            case IdentifierNode identifier:
                return identifier.Token.Text;
            case QualifiedNode qualified:
                return qualified.NameToken.Text;
            case PathQualifiedNode path:
                return path.NameToken.Text;
            default:
                return null;
        }
    }
}
