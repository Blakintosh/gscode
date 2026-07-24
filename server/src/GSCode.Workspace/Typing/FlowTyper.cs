using System.Collections.Immutable;
using GSCode.Core;
using GSCode.Core.Symbols;
using GSCode.Core.Text;
using GSCode.Parser;
using GSCode.Parser.Lexing;
using GSCode.Parser.Syntax;
using GSCode.Parser.Syntax.Ast;
using GSCode.Workspace.Api;

namespace GSCode.Workspace.Typing;

/// <summary>The inferred type of a local at one assignment site.</summary>
/// <param name="NameRange">Root-file range of the assigned local's name.</param>
/// <param name="Type">The inferred concrete type.</param>
/// <param name="Name">Display-case local name (lets hover match an identifier by name).</param>
/// <param name="IsFirstForName">
/// Whether this is the first typed assignment to the name in its function.
///
/// Inlay hints want only these — a `: int` label repeated at every reassignment is noise. Hover
/// wants them all, so it can report the type as of the cursor rather than the type the variable
/// started with. The list carries every assignment and each consumer filters, because building it
/// for the hint case alone is what made hover report a stale type.
/// </param>
public readonly record struct InferredAssignment(TextRange NameRange, ScrType Type, string Name, bool IsFirstForName = true);

/// <summary>The inferred type of the local identifier under a cursor (for hover).</summary>
public readonly record struct LocalTypeHover(string Name, TextRange Range, ScrType Type);

/// <summary>
/// One `owner.field = …` write with the owner's inferred type AT THAT POINT. Lets a lint decide
/// whether a field is read-only without re-deriving types: `SpawnStruct()` gives Struct, `self`
/// gives Entity, and an owner the flow cannot type gives Unknown.
/// </summary>
/// <summary>
/// One write to `owner.field`. <paramref name="Value"/> is the assigned expression for a plain
/// `=`, and null for a compound assignment or `++`/`--` — those have no single assigned value, and
/// a rule about what was assigned must not fire on them.
/// </summary>
public readonly record struct FieldWrite(
    TextRange NameRange, string FieldName, ScrType OwnerType, ExprNode? Value = null);

/// <summary>
/// A deliberately-small forward type-flow pass, per function. It types each assignment's
/// right-hand side from literals, arithmetic, globals, and known builtin return types,
/// threading a local environment so later assignments can use earlier ones. It only ever
/// reports a concrete type — anything uncertain stays Unknown and produces no hint.
/// </summary>
public sealed class FlowTyper
{
    private readonly BuiltinApi _builtins;
    private readonly ObjectFields _objectFields;

    public FlowTyper(BuiltinApi builtins, ObjectFields objectFields)
    {
        _builtins = builtins;
        _objectFields = objectFields;
    }

    /// <summary>Infers a type for the first assignment of each local that resolves to a concrete type.</summary>
    public ImmutableArray<InferredAssignment> InferAssignments(ParseResult result)
    {
        return InferAssignments(result, out _);
    }

    /// <summary>
    /// The same single pass, additionally reporting every `owner.field = …` write with the
    /// owner's inferred type. One walk feeds both so a lint can never disagree with a hint.
    /// </summary>
    public ImmutableArray<InferredAssignment> InferAssignments(ParseResult result, out ImmutableArray<FieldWrite> fieldWrites)
    {
        ImmutableArray<InferredAssignment>.Builder hints = ImmutableArray.CreateBuilder<InferredAssignment>();
        ImmutableArray<FieldWrite>.Builder writes = ImmutableArray.CreateBuilder<FieldWrite>();

        foreach ( AstNode element in result.Tree.Root.Elements )
        {
            if ( element is FunctionNode function )
            {
                TypeFunction(function, hints, writes);
            }
            else if ( element is ClassNode classNode )
            {
                foreach ( AstNode member in classNode.Members )
                {
                    if ( member is FunctionNode method )
                    {
                        TypeFunction(method, hints, writes);
                    }
                }
            }
        }

        fieldWrites = writes.ToImmutable();
        return hints.ToImmutable();
    }

    /// <summary>
    /// Resolves the inferred type of the local variable identifier under a cursor, for hover.
    /// Returns false when the position isn't on a local, the local has no concrete type, or it
    /// is a field/parameter (those aren't inferred here). Reuses the same per-function pass so a
    /// hover always agrees with the inlay hint shown at the assignment.
    /// </summary>
    public bool TryGetLocalTypeAt(ParseResult result, Position position, out LocalTypeHover hover)
    {
        hover = default;

        // Find the innermost identifier under the cursor and the function that encloses it.
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
            return false;
        }

        // The identifier is only a local if the flow pass typed an assignment to that name
        // inside this same function.
        //
        // The LAST assignment at or before the cursor, not the first. A variable reassigned to a
        // different type used to keep reporting the type it started with, so hovering the final
        // `x` in `x = 1; … x = "hello"; use( x );` said int. Assignments BELOW the cursor are
        // skipped outright: they say nothing about the value here.
        string name = identifier.Token.Text;
        bool found = false;

        foreach ( InferredAssignment assignment in InferAssignments(result) )
        {
            if ( !function.Range.Contains(assignment.NameRange.Start) )
            {
                continue;
            }

            if ( !string.Equals(assignment.Name, name, StringComparison.OrdinalIgnoreCase) )
            {
                continue;
            }

            if ( assignment.NameRange.Start > position )
            {
                continue;
            }

            // Assignments arrive in source order, so a later one simply overwrites an earlier.
            // Straight-line code is then exact. Across branches this reports whichever arm is
            // written last rather than the join of both — narrowing that further needs the walk's
            // environment sampled at a position, which it does not currently retain.
            hover = new LocalTypeHover(name, identifier.Range, assignment.Type);
            found = true;
        }

        return found;
    }

    private void TypeFunction(FunctionNode function, ImmutableArray<InferredAssignment>.Builder hints, ImmutableArray<FieldWrite>.Builder writes)
    {
        Dictionary<string, ScrType> environment = new(StringComparer.OrdinalIgnoreCase);
        HashSet<string> hinted = new(StringComparer.OrdinalIgnoreCase);
        WalkStatement(function.Body, environment, hinted, hints, writes);
    }

    private void WalkStatement(AstNode statement, Dictionary<string, ScrType> environment, HashSet<string> hinted, ImmutableArray<InferredAssignment>.Builder hints, ImmutableArray<FieldWrite>.Builder writes)
    {
        switch ( statement )
        {
            case BlockNode block:
                foreach ( AstNode child in block.Statements )
                {
                    WalkStatement(child, environment, hinted, hints, writes);
                }

                return;
            case ExprStatementNode exprStatement:
                TypeExpressionForEffects(exprStatement.Expression, environment, hinted, hints, writes);
                return;
            case IfNode ifNode:
            {
                // The two arms are alternatives, so each walks its own copy and the results
                // are joined. Sharing one environment would let whichever arm ran last win.
                Dictionary<string, ScrType> thenEnvironment = Clone(environment);
                Dictionary<string, ScrType> elseEnvironment = Clone(environment);
                ApplyIsDefinedNarrowing(ifNode.Condition, thenEnvironment, elseEnvironment);

                WalkStatement(ifNode.Then, thenEnvironment, hinted, hints, writes);
                if ( ifNode.Else is not null )
                {
                    WalkStatement(ifNode.Else, elseEnvironment, hinted, hints, writes);
                }

                MergeAlternatives(environment, thenEnvironment, elseEnvironment);
                return;
            }
            case WhileNode whileNode:
                MergeLoopBody(whileNode.Body, environment, hinted, hints, writes);
                return;
            case DoWhileNode doWhile:
                // The body always runs at least once, so its effects apply directly.
                WalkStatement(doWhile.Body, environment, hinted, hints, writes);
                return;
            case ForNode forNode:
                if ( forNode.Initializer is not null )
                {
                    // The initializer runs unconditionally, before the loop can be skipped.
                    WalkStatement(forNode.Initializer, environment, hinted, hints, writes);
                }

                MergeLoopBody(forNode.Body, environment, hinted, hints, writes);
                return;
            case ForeachNode foreachNode:
                MergeLoopBody(foreachNode.Body, environment, hinted, hints, writes);
                return;
            case SwitchNode switchNode:
                WalkSwitch(switchNode, environment, hinted, hints, writes);
                return;
            default:
                return;
        }
    }

    /// <summary>
    /// Walks a loop body as an alternative path: the body may run zero times, so its effects
    /// are joined with the environment as it stood before the loop.
    /// </summary>
    private void MergeLoopBody(
        AstNode body,
        Dictionary<string, ScrType> environment,
        HashSet<string> hinted,
        ImmutableArray<InferredAssignment>.Builder hints,
        ImmutableArray<FieldWrite>.Builder writes)
    {
        Dictionary<string, ScrType> bodyEnvironment = Clone(environment);
        WalkStatement(body, bodyEnvironment, hinted, hints, writes);

        // One join suffices: Join only ever moves toward Unknown, so iterating to a fixpoint
        // could never yield a more precise answer than this single pass.
        MergeAlternatives(environment, environment, bodyEnvironment);
    }

    /// <summary>
    /// Walks each case group as its own alternative path. Without a default label no group
    /// need run at all, so the pre-switch environment joins in as a further alternative.
    /// </summary>
    private void WalkSwitch(
        SwitchNode switchNode,
        Dictionary<string, ScrType> environment,
        HashSet<string> hinted,
        ImmutableArray<InferredAssignment>.Builder hints,
        ImmutableArray<FieldWrite>.Builder writes)
    {
        List<Dictionary<string, ScrType>> paths = new();

        foreach ( CaseGroupNode group in switchNode.Cases )
        {
            Dictionary<string, ScrType> caseEnvironment = Clone(environment);
            foreach ( AstNode child in group.Statements )
            {
                WalkStatement(child, caseEnvironment, hinted, hints, writes);
            }

            paths.Add(caseEnvironment);
        }

        if ( !HasDefaultLabel(switchNode) )
        {
            paths.Add(Clone(environment));
        }

        if ( paths.Count == 0 )
        {
            return;
        }

        Dictionary<string, ScrType> merged = paths[0];
        for ( int index = 1; index < paths.Count; index++ )
        {
            MergeAlternatives(merged, merged, paths[index]);
        }

        environment.Clear();
        foreach ( KeyValuePair<string, ScrType> entry in merged )
        {
            environment[entry.Key] = entry.Value;
        }
    }

    /// <summary>A null label marks the default group.</summary>
    private static bool HasDefaultLabel(SwitchNode switchNode)
    {
        foreach ( CaseGroupNode group in switchNode.Cases )
        {
            foreach ( ExprNode? label in group.Labels )
            {
                if ( label is null )
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// Applies <c>isdefined(x)</c> narrowing to an if statement's two arms: x is defined in the
    /// arm the guard selects and undefined in the other, with a leading <c>!</c> swapping them.
    /// The undefined side is the one that matters — without it a stale type would be asserted
    /// on a path where the value is known not to exist, as in
    /// <c>x = 5; if ( !isdefined( x ) ) { y = x; }</c>.
    /// </summary>
    private static void ApplyIsDefinedNarrowing(
        ExprNode condition,
        Dictionary<string, ScrType> thenEnvironment,
        Dictionary<string, ScrType> elseEnvironment)
    {
        bool negated = false;
        ExprNode current = condition;

        // Peel parentheses and negations; anything else ends the guard shape.
        while ( true )
        {
            if ( current is ParenNode paren )
            {
                current = paren.Inner;
                continue;
            }

            if ( current is PrefixNode prefix && prefix.Operator == TokenKind.Bang )
            {
                negated = !negated;
                current = prefix.Operand;
                continue;
            }

            break;
        }

        if ( !TryGetIsDefinedTarget(current, out string name) )
        {
            return;
        }

        Dictionary<string, ScrType> definedSide = negated ? elseEnvironment : thenEnvironment;
        Dictionary<string, ScrType> undefinedSide = negated ? thenEnvironment : elseEnvironment;

        // Known to exist, but the guard says nothing about which type it holds.
        if ( definedSide.TryGetValue(name, out ScrType existing) && existing == ScrType.Undefined )
        {
            definedSide[name] = ScrType.Unknown;
        }

        undefinedSide[name] = ScrType.Undefined;
    }

    /// <summary>The local name inside <c>isdefined( name )</c>, when the expression is exactly that.</summary>
    private static bool TryGetIsDefinedTarget(ExprNode expression, out string name)
    {
        name = "";

        if ( expression is not CallNode call || call.Arguments.Length != 1 )
        {
            return false;
        }

        // Callable keywords parse as a call with an identifier callee carrying the keyword token.
        if ( call.Callee is not IdentifierNode callee
            || !string.Equals(callee.Token.Text, "isdefined", StringComparison.OrdinalIgnoreCase) )
        {
            return false;
        }

        // Only a bare local narrows; fields and indexes are not tracked in the environment.
        if ( call.Arguments[0] is not IdentifierNode target )
        {
            return false;
        }

        name = target.Token.Text;
        return true;
    }

    private static Dictionary<string, ScrType> Clone(Dictionary<string, ScrType> environment)
    {
        return new Dictionary<string, ScrType>(environment, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Replaces <paramref name="destination"/> with the join of two alternative paths. A name
    /// typed on only one path becomes Unknown: it may be undefined on the other, and the
    /// lattice never guesses a union.
    /// </summary>
    private static void MergeAlternatives(
        Dictionary<string, ScrType> destination,
        Dictionary<string, ScrType> first,
        Dictionary<string, ScrType> second)
    {
        Dictionary<string, ScrType> joined = new(StringComparer.OrdinalIgnoreCase);

        foreach ( KeyValuePair<string, ScrType> entry in first )
        {
            if ( second.TryGetValue(entry.Key, out ScrType other) )
            {
                joined[entry.Key] = ScrTypes.Join(entry.Value, other);
            }
            else
            {
                joined[entry.Key] = ScrType.Unknown;
            }
        }

        foreach ( KeyValuePair<string, ScrType> entry in second )
        {
            if ( !joined.ContainsKey(entry.Key) )
            {
                joined[entry.Key] = ScrType.Unknown;
            }
        }

        destination.Clear();
        foreach ( KeyValuePair<string, ScrType> entry in joined )
        {
            destination[entry.Key] = entry.Value;
        }
    }

    private void TypeExpressionForEffects(ExprNode expression, Dictionary<string, ScrType> environment, HashSet<string> hinted, ImmutableArray<InferredAssignment>.Builder hints, ImmutableArray<FieldWrite>.Builder writes)
    {
        // ++ and -- read and write in one step, so they are field writes too.
        MemberNode? incremented = IncrementedMember(expression);
        if ( incremented is not null )
        {
            writes.Add(new FieldWrite(
                incremented.NameToken.RootRange,
                incremented.NameToken.Text,
                TypeOf(incremented.Object, environment)));
            return;
        }

        if ( expression is not AssignmentNode assignment )
        {
            return;
        }

        // `owner.field = …` is not a local, but the owner's type is exactly what a read-only
        // lint needs, and this is the one place it is known. Compound writes count too: `+=` on
        // a read-only field is just as wrong as `=`.
        if ( assignment.Target is MemberNode member )
        {
            writes.Add(new FieldWrite(
                member.NameToken.RootRange,
                member.NameToken.Text,
                TypeOf(member.Object, environment),
                assignment.Operator == TokenKind.Assign ? assignment.Value : null));
            return;
        }

        // Only plain `local = value` (the '=' operator) yields a type; compound ops keep
        // the existing type.
        if ( assignment.Operator != TokenKind.Assign || assignment.Target is not IdentifierNode target )
        {
            return;
        }

        ScrType type = TypeOf(assignment.Value, environment);
        string name = target.Token.Text;
        environment[name] = type;

        // An assignment inside a macro body reports the INVOCATION's range, so hinting it
        // would label the call site with a type the user never wrote there. The environment
        // still updates, since the assignment does happen.
        if ( target.Token.Provenance.DefinitionSite is not null )
        {
            return;
        }

        if ( type.IsKnown() )
        {
            hints.Add(new InferredAssignment(target.Token.RootRange, type, name, IsFirstForName: hinted.Add(name)));
        }
    }

    /// <summary>The member a ++/-- applies to, or null when the expression is not one.</summary>
    private static MemberNode? IncrementedMember(ExprNode expression)
    {
        if ( expression is PostfixNode postfix && IsIncrementOrDecrement(postfix.Operator) )
        {
            return postfix.Operand as MemberNode;
        }

        if ( expression is PrefixNode prefix && IsIncrementOrDecrement(prefix.Operator) )
        {
            return prefix.Operand as MemberNode;
        }

        return null;
    }

    private static bool IsIncrementOrDecrement(TokenKind kind)
    {
        return kind == TokenKind.PlusPlus || kind == TokenKind.MinusMinus;
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
            case MemberNode member:
                return TypeOfField(member);
            default:
                return ScrType.Unknown;
        }
    }

    /// <summary>
    /// Types a field access `owner.field`. `.size` is always int; otherwise the engine
    /// object-field data seeds a type, but only when every entity kind that declares the
    /// field name agrees (the owner's entity kind isn't inferred, so disagreement → Unknown).
    /// </summary>
    private ScrType TypeOfField(MemberNode member)
    {
        string fieldName = member.NameToken.Text;
        if ( string.Equals(fieldName, "size", StringComparison.OrdinalIgnoreCase) )
        {
            return ScrType.Int;
        }

        ImmutableArray<ObjectField> fields = _objectFields.FindField(fieldName);
        if ( fields.Length == 0 )
        {
            return ScrType.Unknown;
        }

        ScrType agreed = MapReturnType(fields[0].Type.ToLowerInvariant());
        for ( int index = 1; index < fields.Length; index++ )
        {
            if ( MapReturnType(fields[index].Type.ToLowerInvariant()) != agreed )
            {
                return ScrType.Unknown;
            }
        }

        return agreed;
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
            // world is a BO3+ global; where the dialect has no world, a bare "world" is an ordinary
            // name (the case falls through to default), so it isn't mistyped as the world struct.
            case "world" when GameProfile.Active.HasWorldObject:
            case "level":
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
            PathQualifiedNode path => path.NameToken.Text,
            _ => null,
        };

        if ( name is null )
        {
            return ScrType.Unknown;
        }

        // Callable keywords (isdefined, vectorscale) have no API entry, so their return types
        // come from the emulation table instead.
        if ( BuiltinEmulations.TryGetReturnType(name, out ScrType emulated) )
        {
            return emulated;
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
