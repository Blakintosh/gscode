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
    public static TextRange? Find(ParseResult result, Position position)
    {
        List<AstNode> chain = AstSearch.ChainAt(result.Tree.Root, position);

        IdentifierNode? identifier = null;
        FunctionNode? function = null;
        foreach ( AstNode node in chain )
        {
            if ( node is FunctionNode enclosingFunction )
            {
                function = enclosingFunction;
            }
            else if ( node is IdentifierNode identifierNode )
            {
                identifier = identifierNode;
            }
        }

        if ( identifier is null || function is null )
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

        TextRange? best = null;
        foreach ( FunctionSymbol declared in result.Extraction.Functions )
        {
            if ( !declared.FullRange.Contains(position) )
            {
                continue;
            }

            foreach ( AssignmentSymbol assignment in declared.Assignments )
            {
                // Locals only. `self.count = 1` introduces a FIELD on an entity that outlives this
                // function, so it is not what a bare `count` here refers to.
                if ( assignment.OwnerName.Length > 0 )
                {
                    continue;
                }

                if ( !string.Equals(assignment.Name, name, StringComparison.OrdinalIgnoreCase) )
                {
                    continue;
                }

                // At or before the cursor. An assignment further down says nothing about where the
                // value being read here came from.
                if ( assignment.Range.Start.Line > position.Line
                    || (assignment.Range.Start.Line == position.Line
                        && assignment.Range.Start.Character > position.Character) )
                {
                    continue;
                }

                best = assignment.Range;
            }
        }

        return best;
    }
}
