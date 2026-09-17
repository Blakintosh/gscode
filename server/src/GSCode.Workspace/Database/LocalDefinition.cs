using System.Collections.Immutable;
using GSCode.Core;
using GSCode.Core.Symbols;
using GSCode.Core.Text;
using GSCode.Parser;
using GSCode.Parser.Syntax;
using GSCode.Parser.Syntax.Ast;

namespace GSCode.Workspace.Database;

/// <summary>
/// Go-to-definition for a LOCAL: a parameter, or the assignment that introduced a variable.
///
/// Locals are not in the reference index and deliberately so — the index is keyed by
/// <see cref="SymbolKey"/> and shared across the workspace, while an `i` in one function has
/// nothing to do with an `i` in another, so putting them there would make every local in every file
/// collide. That leaves go-to-definition with nothing to find on a variable, which is exactly the
/// reported symptom.
///
/// Resolved from the AST instead, per function, which is the scope a local actually has.
/// </summary>
public static class LocalDefinition
{
    /// <summary>
    /// Where the local under <paramref name="position"/> is introduced, or null when the position
    /// is not on one.
    ///
    /// A parameter wins over an assignment: `function f( count ) { count = 1; }` introduces the
    /// name in the signature, and the assignment is a write to something that already exists.
    /// Otherwise it is the LAST assignment at or before the cursor, matching what hover reports —
    /// jumping to the first would send you somewhere the value no longer comes from, and the two
    /// surfaces disagreeing about the same variable is worse than either answer alone.
    /// </summary>
    public static TextRange? Find(ParseResult result, Position position, GameProfile? profile = null)
    {
        if ( !AstSearch.TryFindLocalContext(
            result.Tree.Root, position, out IdentifierNode identifier, out FunctionNode function) )
        {
            return null;
        }

        string name = identifier.Token.Text;

        foreach ( ParameterNode parameter in function.Parameters )
        {
            if ( string.Equals(parameter.NameToken.Text, name, StringComparison.OrdinalIgnoreCase) )
            {
                return parameter.NameToken.RootRange;
            }
        }

        // The occurrence list, not the parser's raw AssignmentSymbols: those skip `a[ 0 ] = x`
        // (which CREATES `a` when it does not exist — how every array in the stock scripts is
        // built) and never reach a class method's body at all, since `result.Extraction.Functions`
        // holds only top-level functions. LocalReferences already gets both right for the same
        // variable, and disagreeing with it is exactly the reported symptom.
        ImmutableArray<LocalOccurrence> occurrences = LocalReferences.Find(result, position, profile);

        TextRange? best = null;
        foreach ( LocalOccurrence occurrence in occurrences )
        {
            if ( !occurrence.IsWrite )
            {
                continue;
            }

            // At or before the cursor. An occurrence further down says nothing about where the
            // value being read here came from.
            if ( occurrence.Range.Start.Line > position.Line
                || (occurrence.Range.Start.Line == position.Line
                    && occurrence.Range.Start.Character > position.Character) )
            {
                continue;
            }

            best = occurrence.Range;
        }

        return best;
    }
}
