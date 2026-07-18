using System.Collections.Immutable;
using GSCode.Core.Symbols;
using GSCode.Core.Text;
using GSCode.Parser;
using GSCode.Parser.Lexing;
using GSCode.Parser.Syntax.Ast;
using GSCode.Workspace.Api;

namespace GSCode.Workspace.Typing;

/// <summary>The inferred type of a local at its assignment site (for inlay hints).</summary>
public readonly record struct InferredAssignment(TextRange NameRange, ScrType Type);

/// <summary>
/// A deliberately-small forward type-flow pass, per function. It types each assignment's
/// right-hand side from literals, arithmetic, globals, and known builtin return types,
/// threading a local environment so later assignments can use earlier ones. It only ever
/// reports a concrete type — anything uncertain stays Unknown and produces no hint.
/// </summary>
public sealed class FlowTyper
{
    private readonly BuiltinApi _builtins;

    public FlowTyper(BuiltinApi builtins)
    {
        _builtins = builtins;
    }

    /// <summary>Infers a type for the first assignment of each local that resolves to a concrete type.</summary>
    public ImmutableArray<InferredAssignment> InferAssignments(ParseResult result)
    {
        ImmutableArray<InferredAssignment>.Builder hints = ImmutableArray.CreateBuilder<InferredAssignment>();

        foreach ( AstNode element in result.Tree.Root.Elements )
        {
            if ( element is FunctionNode function )
            {
                TypeFunction(function, hints);
            }
            else if ( element is ClassNode classNode )
            {
                foreach ( AstNode member in classNode.Members )
                {
                    if ( member is FunctionNode method )
                    {
                        TypeFunction(method, hints);
                    }
                }
            }
        }

        return hints.ToImmutable();
    }

    private void TypeFunction(FunctionNode function, ImmutableArray<InferredAssignment>.Builder hints)
    {
        Dictionary<string, ScrType> environment = new(StringComparer.OrdinalIgnoreCase);
        HashSet<string> hinted = new(StringComparer.OrdinalIgnoreCase);
        WalkStatement(function.Body, environment, hinted, hints);
    }

    private void WalkStatement(AstNode statement, Dictionary<string, ScrType> environment, HashSet<string> hinted, ImmutableArray<InferredAssignment>.Builder hints)
    {
        switch ( statement )
        {
            case BlockNode block:
                foreach ( AstNode child in block.Statements )
                {
                    WalkStatement(child, environment, hinted, hints);
                }

                return;
            case ExprStatementNode exprStatement:
                TypeExpressionForEffects(exprStatement.Expression, environment, hinted, hints);
                return;
            case IfNode ifNode:
                WalkStatement(ifNode.Then, environment, hinted, hints);
                if ( ifNode.Else is not null )
                {
                    WalkStatement(ifNode.Else, environment, hinted, hints);
                }

                return;
            case WhileNode whileNode:
                WalkStatement(whileNode.Body, environment, hinted, hints);
                return;
            case DoWhileNode doWhile:
                WalkStatement(doWhile.Body, environment, hinted, hints);
                return;
            case ForNode forNode:
                if ( forNode.Initializer is not null )
                {
                    WalkStatement(forNode.Initializer, environment, hinted, hints);
                }

                WalkStatement(forNode.Body, environment, hinted, hints);
                return;
            case ForeachNode foreachNode:
                WalkStatement(foreachNode.Body, environment, hinted, hints);
                return;
            case SwitchNode switchNode:
                foreach ( CaseGroupNode group in switchNode.Cases )
                {
                    foreach ( AstNode child in group.Statements )
                    {
                        WalkStatement(child, environment, hinted, hints);
                    }
                }

                return;
            default:
                return;
        }
    }

    private void TypeExpressionForEffects(ExprNode expression, Dictionary<string, ScrType> environment, HashSet<string> hinted, ImmutableArray<InferredAssignment>.Builder hints)
    {
        if ( expression is not AssignmentNode assignment )
        {
            return;
        }

        // Only plain `local = value` (the '=' operator) yields a type; compound ops keep
        // the existing type and field/index targets aren't locals.
        if ( assignment.Operator != TokenKind.Assign || assignment.Target is not IdentifierNode target )
        {
            return;
        }

        ScrType type = TypeOf(assignment.Value, environment);
        string name = target.Token.Text;
        environment[name] = type;

        if ( type.IsKnown() && hinted.Add(name) )
        {
            hints.Add(new InferredAssignment(target.Token.RootRange, type));
        }
    }

    private ScrType TypeOf(ExprNode expression, Dictionary<string, ScrType> environment)
    {
        switch ( expression )
        {
            case LiteralNode literal:
                return TypeOfLiteral(literal.Token.Kind);
            case ParenNode paren:
                return TypeOf(paren.Inner, environment);
            case VectorNode:
                return ScrType.Vector;
            case ArrayLiteralNode:
                return ScrType.Array;
            case NewNode:
                return ScrType.Struct;
            case IdentifierNode identifier:
                return TypeOfIdentifier(identifier.Token.Text, environment);
            case PrefixNode prefix:
                return TypeOfPrefix(prefix, environment);
            case BinaryNode binary:
                return TypeOfBinary(binary, environment);
            case CallNode call:
                return TypeOfCall(call);
            default:
                return ScrType.Unknown;
        }
    }

    private static ScrType TypeOfLiteral(TokenKind kind)
    {
        switch ( kind )
        {
            case TokenKind.Integer:
            case TokenKind.Hex:
            case TokenKind.HashString:
                return ScrType.Int;
            case TokenKind.Float:
                return ScrType.Float;
            case TokenKind.String:
                return ScrType.String;
            case TokenKind.LocalizedString:
                return ScrType.IString;
            case TokenKind.True:
            case TokenKind.False:
                return ScrType.Bool;
            case TokenKind.Undefined:
                return ScrType.Undefined;
            default:
                return ScrType.Unknown;
        }
    }

    private static ScrType TypeOfIdentifier(string name, Dictionary<string, ScrType> environment)
    {
        if ( environment.TryGetValue(name, out ScrType type) )
        {
            return type;
        }

        switch ( name.ToLowerInvariant() )
        {
            case "self":
                return ScrType.Entity;
            case "level":
            case "world":
            case "anim":
                return ScrType.Struct;
            case "game":
                return ScrType.Array;
            default:
                return ScrType.Unknown;
        }
    }

    private ScrType TypeOfPrefix(PrefixNode prefix, Dictionary<string, ScrType> environment)
    {
        switch ( prefix.Operator )
        {
            case TokenKind.Bang:
                return ScrType.Bool;
            case TokenKind.Ampersand:
                return ScrType.Function;
            case TokenKind.Tilde:
                return ScrType.Int;
            case TokenKind.Minus:
            {
                ScrType operand = TypeOf(prefix.Operand, environment);
                return operand is ScrType.Int or ScrType.Float ? operand : ScrType.Unknown;
            }
            default:
                return ScrType.Unknown;
        }
    }

    private ScrType TypeOfBinary(BinaryNode binary, Dictionary<string, ScrType> environment)
    {
        switch ( binary.Operator )
        {
            case TokenKind.EqualsEquals:
            case TokenKind.NotEquals:
            case TokenKind.StrictEquals:
            case TokenKind.StrictNotEquals:
            case TokenKind.LessThan:
            case TokenKind.LessThanEquals:
            case TokenKind.GreaterThan:
            case TokenKind.GreaterThanEquals:
            case TokenKind.LogicalAnd:
            case TokenKind.LogicalOr:
                return ScrType.Bool;
            case TokenKind.Plus:
            {
                ScrType left = TypeOf(binary.Left, environment);
                ScrType right = TypeOf(binary.Right, environment);
                // String concatenation if either side is a string; otherwise numeric.
                if ( left == ScrType.String || right == ScrType.String )
                {
                    return ScrType.String;
                }

                return NumericResult(left, right);
            }
            case TokenKind.Minus:
            case TokenKind.Star:
            case TokenKind.Slash:
            case TokenKind.Percent:
                return NumericResult(TypeOf(binary.Left, environment), TypeOf(binary.Right, environment));
            case TokenKind.ShiftLeft:
            case TokenKind.ShiftRight:
            case TokenKind.Ampersand:
            case TokenKind.Pipe:
            case TokenKind.Caret:
                return ScrType.Int;
            default:
                return ScrType.Unknown;
        }
    }

    private static ScrType NumericResult(ScrType left, ScrType right)
    {
        if ( left == ScrType.Float || right == ScrType.Float )
        {
            return ScrType.Float;
        }

        if ( left == ScrType.Int && right == ScrType.Int )
        {
            return ScrType.Int;
        }

        return ScrType.Unknown;
    }

    private ScrType TypeOfCall(CallNode call)
    {
        // Only builtin return types are known here; script-function return inference is
        // out of scope for this pass (their bodies aren't re-typed).
        string? name = call.Callee switch
        {
            IdentifierNode identifier => identifier.Token.Text,
            QualifiedNode qualified => qualified.NameToken.Text,
            _ => null,
        };

        if ( name is null )
        {
            return ScrType.Unknown;
        }

        BuiltinFunction? builtin = _builtins.Find(name);
        if ( builtin is null || builtin.Overloads.Length == 0 )
        {
            return ScrType.Unknown;
        }

        return MapReturnType(builtin.Overloads[0].ReturnTypeText);
    }

    /// <summary>Maps a builtin's return-type text to the lattice; unions and vague types stay Unknown.</summary>
    private static ScrType MapReturnType(string typeText)
    {
        switch ( typeText )
        {
            case "int":
                return ScrType.Int;
            case "float":
                return ScrType.Float;
            case "bool":
                return ScrType.Bool;
            case "string":
                return ScrType.String;
            case "istring":
                return ScrType.IString;
            case "vector":
                return ScrType.Vector;
            case "struct":
                return ScrType.Struct;
            case "entity":
                return ScrType.Entity;
            case "function":
                return ScrType.Function;
            default:
                // Arrays ("t[]"), unions ("int | number"), "number", "any" → not certain.
                return ScrType.Unknown;
        }
    }
}
