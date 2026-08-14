using System.Globalization;
using GSCode.Parser.Preprocessing;
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
/// <param name="Value">
/// The inferred value, carried whole rather than already projected.
///
/// A consumer that JUDGES a type wants <see cref="InferredAssignment.Type"/>, the coarse
/// projection the typing lints compare against. A consumer that SHOWS one wants
/// <see cref="InferredAssignment.Display"/>, which keeps the class name of a `new Foo()` and the
/// hashness of a `#"str"` — both of which the projection has to collapse. Storing the projection
/// here is what threw those away before either consumer could ask.
/// </param>
/// <param name="Name">Display-case local name (lets hover match an identifier by name).</param>
/// <param name="IsFirstForName">
/// Whether this is the first typed assignment to the name in its function.
///
/// Inlay hints want only these — a `: int` label repeated at every reassignment is noise. Hover
/// wants them all, so it can report the type as of the cursor rather than the type the variable
/// started with. The list carries every assignment and each consumer filters, because building it
/// for the hint case alone is what made hover report a stale type.
/// </param>
/// <param name="IsField">
/// Whether this is a field write (`self.count = 1`) rather than a local (`count = 1`). The two
/// share a Name, so a consumer asking about a FIELD would otherwise be answered by a local that
/// happens to be spelled the same - which is common, since a field and the local feeding it are
/// usually named alike.
/// </param>
public readonly record struct InferredAssignment(
    TextRange NameRange, ScrValue Value, string Name, bool IsFirstForName = true, bool IsField = false)
{
    /// <summary>The coarse projection, for a caller judging the type rather than showing it.</summary>
    public ScrType Type
    {
        get { return Value.ToScrType(); }
    }

    /// <summary>The label to show a reader.</summary>
    public string Display
    {
        get { return Value.DisplayName(); }
    }
}

/// <summary>The inferred type of the local identifier under a cursor (for hover).</summary>
public readonly record struct LocalTypeHover(string Name, TextRange Range, ScrValue Value)
{
    /// <summary>The coarse projection, for a caller judging the type rather than showing it.</summary>
    public ScrType Type
    {
        get { return Value.ToScrType(); }
    }

    /// <summary>The label to show a reader.</summary>
    public string Display
    {
        get { return Value.DisplayName(); }
    }
}

/// <summary>
/// One write to `owner.field`, carrying the owner's inferred type AT THAT POINT. Lets a lint decide
/// whether a field is read-only without re-deriving types: `SpawnStruct()` gives Struct, `self`
/// gives Entity, and an owner the flow cannot type gives Unknown.
///
/// <paramref name="Value"/> is the assigned expression for a plain `=`, and null for a compound
/// assignment or `++`/`--` — those have no single assigned value, and a rule about what was
/// assigned must not fire on them.
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
    private readonly GameProfile _game;

    /// <summary>
    /// Set only for the hover pass, and null for the hint pass.
    ///
    /// The hint pass wants every assignment in the file; hover wants the environment as it stands
    /// at ONE position, which is a different question and cannot be answered by filtering the
    /// hints afterwards. A hint records what a name became at each assignment site, while the
    /// environment records what it is HERE — including the join of two branches that have both
    /// already run, which no single assignment site represents.
    /// </summary>
    private Position? _cursor;

    /// <summary>
    /// Set only while <see cref="InferValues"/> is running. Null the rest of the time so the hint
    /// and hover passes cost nothing extra — they ask about one name or one position and would pay
    /// for a whole file's map to answer it.
    /// </summary>
    private Dictionary<ExprNode, ScrValue>? _recorded;

    /// <summary>
    /// <paramref name="profile"/> defaults to the active one, matching how every <c>Analyze</c>
    /// entry point in Analysis, Api and Database takes it — so a test can type a function against a
    /// dialect other than the one the server happens to be running.
    /// </summary>
    public FlowTyper(BuiltinApi builtins, ObjectFields objectFields, GameProfile? profile = null)
    {
        _builtins = builtins;
        _objectFields = objectFields;
        _game = profile ?? GameProfile.Active;
    }

    /// <summary>Whether the hover cursor, if there is one, falls inside this node.</summary>
    private bool ContainsCursor(AstNode node)
    {
        return _cursor is Position cursor && node.Range.Contains(cursor);
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

        // The innermost identifier under the cursor and the function that encloses it.
        if ( !AstSearch.TryFindLocalContext(
            result.Tree.Root, position, out IdentifierNode identifier, out FunctionNode function) )
        {
            return false;
        }

        // The environment as it stands AT the cursor, which is a different question from "what did
        // the last assignment say". Reading the hint list reported whichever arm of an if/else was
        // written last, because a hint records what a name became at one assignment site and no
        // site represents the join of two branches that have both already run.
        //
        // The walk already computes that join; it simply threw the environment away. Running it
        // with a stop position keeps it.
        string name = identifier.Token.Text;
        Dictionary<string, ScrValue> environment = EnvironmentAt(function, position);

        // Projected onto the coarse lattice at the boundary: a union has no single-value answer for
        // a hover label, which is exactly what the old behaviour was.
        if ( !environment.TryGetValue(name, out ScrValue value) )
        {
            return false;
        }

        ScrType type = value.ToScrType();
        if ( !type.IsKnown() )
        {
            return false;
        }

        hover = new LocalTypeHover(name, identifier.Range, value);
        return true;
    }

    /// <summary>
    /// The local environment of one function as it stands at <paramref name="position"/>.
    ///
    /// Parameters seed it as unknown so that a name is at least KNOWN to be a local — an assignment
    /// to a parameter then types it from that point, which is exactly what the flow says, while an
    /// untyped parameter still reports nothing rather than a guess. Typing one properly needs
    /// call-site analysis, which is a different pass — and the seed says so, carrying
    /// <see cref="ScrImprecision.UntypedParameter"/> rather than an anonymous unknown.
    /// </summary>
    private Dictionary<string, ScrValue> EnvironmentAt(FunctionNode function, Position position)
    {
        Dictionary<string, ScrValue> environment = new(StringComparer.OrdinalIgnoreCase);
        foreach ( ParameterNode parameter in function.Parameters )
        {
            environment[parameter.NameToken.Text] =
                ScrValue.Of(ScrTypeSet.Universe, ScrImprecision.UntypedParameter);
        }

        ImmutableArray<InferredAssignment>.Builder hints = ImmutableArray.CreateBuilder<InferredAssignment>();
        ImmutableArray<FieldWrite>.Builder writes = ImmutableArray.CreateBuilder<FieldWrite>();

        _cursor = position;
        try
        {
            WalkStatement(function.Body, environment, new HashSet<string>(StringComparer.OrdinalIgnoreCase), hints, writes);
        }
        finally
        {
            // Cleared so the same instance can serve a hint pass afterwards, which must see the
            // whole function rather than stopping partway through it.
            _cursor = null;
        }

        return environment;
    }

    private void TypeFunction(FunctionNode function, ImmutableArray<InferredAssignment>.Builder hints, ImmutableArray<FieldWrite>.Builder writes)
    {
        Dictionary<string, ScrValue> environment = new(StringComparer.OrdinalIgnoreCase);

        // Parameters are seeded here as well as in EnvironmentAt, so a name is known to be a local
        // whichever entry point ran. They were seeded only for the hover pass, which meant the hint
        // pass could not tell an assignment to a parameter from one to a fresh local.
        foreach ( ParameterNode parameter in function.Parameters )
        {
            environment[parameter.NameToken.Text] =
                ScrValue.Of(ScrTypeSet.Universe, ScrImprecision.UntypedParameter);
        }

        HashSet<string> hinted = new(StringComparer.OrdinalIgnoreCase);
        WalkStatement(function.Body, environment, hinted, hints, writes);
    }

    /// <summary>
    /// Every value the pass worked out for a file, keyed by the expression that produced it.
    ///
    /// The transpiler entry point. Unlike <see cref="InferAssignments(ParseResult)"/>, which reports
    /// the sites an editor wants to decorate, this keeps the value of EVERY expression walked —
    /// including the ones nothing is reported about, which is most of them.
    /// </summary>
    public ScriptTypes InferValues(ParseResult result)
    {
        _recorded = new Dictionary<ExprNode, ScrValue>(ReferenceEqualityComparer.Instance);

        try
        {
            ImmutableArray<InferredAssignment> assignments = InferAssignments(result, out ImmutableArray<FieldWrite> writes);
            return new ScriptTypes(_recorded, assignments, writes);
        }
        finally
        {
            _recorded = null;
        }
    }

    /// <summary>
    /// The full value of the local under a cursor, where <see cref="TryGetLocalTypeAt"/> gives the
    /// coarse projection an editor label needs. A caller deciding how to translate a parameter wants
    /// the union and the reason, not a single name.
    /// </summary>
    public bool TryGetValueAt(ParseResult result, Position position, out ScrValue value)
    {
        value = ScrValue.Unknown;

        if ( !AstSearch.TryFindLocalContext(
            result.Tree.Root, position, out IdentifierNode identifier, out FunctionNode function) )
        {
            return false;
        }

        Dictionary<string, ScrValue> environment = EnvironmentAt(function, position);

        return environment.TryGetValue(identifier.Token.Text, out value);
    }

    private void WalkStatement(AstNode statement, Dictionary<string, ScrValue> environment, HashSet<string> hinted, ImmutableArray<InferredAssignment>.Builder hints, ImmutableArray<FieldWrite>.Builder writes)
    {
        // Everything below the cursor is skipped when one is set: it says nothing about the value
        // being read at the cursor, and letting it run would report a type the variable has not
        // taken yet.
        if ( _cursor is Position stop && statement.Range.Start > stop )
        {
            return;
        }

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
                // A cursor INSIDE one arm is on that path, not at the merge: the other arm has not
                // run and joining it in would report a type the code at the cursor cannot see.
                // Walking the containing arm directly is what makes the answer the arm's own.
                if ( ContainsCursor(ifNode.Then) )
                {
                    ApplyIsDefinedNarrowing(ifNode.Condition, environment, Clone(environment));
                    WalkStatement(ifNode.Then, environment, hinted, hints, writes);
                    return;
                }

                if ( ifNode.Else is not null && ContainsCursor(ifNode.Else) )
                {
                    ApplyIsDefinedNarrowing(ifNode.Condition, Clone(environment), environment);
                    WalkStatement(ifNode.Else, environment, hinted, hints, writes);
                    return;
                }

                // The condition is evaluated before either arm, so an assignment inside it applies
                // on both paths. Typed against the live environment for that reason.
                TypeExpressionForEffects(ifNode.Condition, environment, hinted, hints, writes);

                // The two arms are alternatives, so each walks its own copy and the results
                // are joined. Sharing one environment would let whichever arm ran last win.
                Dictionary<string, ScrValue> thenEnvironment = Clone(environment);
                Dictionary<string, ScrValue> elseEnvironment = Clone(environment);
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
                // The condition runs whether or not the body does.
                TypeExpressionForEffects(whileNode.Condition, environment, hinted, hints, writes);
                MergeLoopBody(whileNode.Body, environment, hinted, hints, writes);
                return;
            case DoWhileNode doWhile:
                // The body always runs at least once, so its effects apply directly.
                WalkStatement(doWhile.Body, environment, hinted, hints, writes);
                TypeExpressionForEffects(doWhile.Condition, environment, hinted, hints, writes);
                return;
            case ForNode forNode:
                if ( forNode.Initializer is not null )
                {
                    // The initializer runs unconditionally, before the loop can be skipped.
                    WalkStatement(forNode.Initializer, environment, hinted, hints, writes);
                }

                // The condition is evaluated even when the body never runs, and an assignment can
                // hide in it. The increment runs only if the body did, so it goes with the body.
                if ( forNode.Condition is not null )
                {
                    TypeExpressionForEffects(forNode.Condition, environment, hinted, hints, writes);
                }

                MergeLoopBody(forNode.Body, environment, hinted, hints, writes, forNode.Increment);
                return;
            case ForeachNode foreachNode:
                WalkForeach(foreachNode, environment, hinted, hints, writes);
                return;
            case SwitchNode switchNode:
                WalkSwitch(switchNode, environment, hinted, hints, writes);
                return;
            case DevBlockStmtNode devBlock:
                // `/# … #/` is real code — it runs in a debug build, and assignments inside it want
                // their hints exactly as anywhere else. It was simply never visited, so nothing
                // inside a dev block had an inferred type at all.
                //
                // Walked as an ALTERNATIVE path rather than inline, on the same reasoning as a loop
                // body: the block is compiled out of a release build, so code after it cannot
                // assume anything it assigned still holds. Inside the block the assignments are
                // exact; outside, a name typed only there joins with the environment as it stood
                // before and becomes Unknown, which is the honest answer.
                MergeDevBlock(devBlock, environment, hinted, hints, writes);
                return;
            case ConstDeclNode constDecl:
            {
                // A `const` binds a name for the rest of the function exactly as an assignment
                // does, and it was falling through the default case — so `const MAX = 4;` left MAX
                // untyped and unhinted while `MAX = 4;` was both.
                ScrValue value = TypeOf(constDecl.Value, environment);
                environment[constDecl.NameToken.Text] = value;

                ScrType type = value.ToScrType();
                if ( type.IsKnown() && constDecl.NameToken.Provenance.DefinitionSite is null )
                {
                    hints.Add(new InferredAssignment(
                        constDecl.NameToken.RootRange, value, constDecl.NameToken.Text,
                        IsFirstForName: hinted.Add(constDecl.NameToken.Text)));
                }

                return;
            }
            case ReturnNode returnNode:
                // Typed for its effects only. Nothing collects return values yet, but an assignment
                // can hide in one, and typing it here is the prerequisite for ever inferring a
                // script function's return type.
                if ( returnNode.Value is not null )
                {
                    TypeExpressionForEffects(returnNode.Value, environment, hinted, hints, writes);
                }

                return;
            case WaitNode wait:
                TypeExpressionForEffects(wait.Duration, environment, hinted, hints, writes);
                return;
            default:
                return;
        }
    }

    /// <summary>
    /// Walks a dev block's statements as an alternative path.
    ///
    /// Takes the STATEMENTS rather than the node, because handing the node back to
    /// <see cref="WalkStatement"/> would land on the dev-block case again and recurse forever.
    /// </summary>
    private void MergeDevBlock(
        DevBlockStmtNode devBlock,
        Dictionary<string, ScrValue> environment,
        HashSet<string> hinted,
        ImmutableArray<InferredAssignment>.Builder hints,
        ImmutableArray<FieldWrite>.Builder writes)
    {
        // Inside the block, the code at the cursor is on the path where it ran.
        if ( ContainsCursor(devBlock) )
        {
            foreach ( AstNode statement in devBlock.Statements )
            {
                WalkStatement(statement, environment, hinted, hints, writes);
            }

            return;
        }

        Dictionary<string, ScrValue> blockEnvironment = Clone(environment);
        foreach ( AstNode statement in devBlock.Statements )
        {
            WalkStatement(statement, blockEnvironment, hinted, hints, writes);
        }

        MergeAlternatives(environment, environment, blockEnvironment);
    }

    /// <summary>
    /// Walks a loop body as an alternative path: the body may run zero times, so its effects
    /// are joined with the environment as it stood before the loop.
    /// </summary>
    /// <param name="increment">
    /// A for-loop's increment, which runs only on iterations where the body ran — so it belongs on
    /// the body's path rather than the outer one.
    /// </param>
    private void MergeLoopBody(
        AstNode body,
        Dictionary<string, ScrValue> environment,
        HashSet<string> hinted,
        ImmutableArray<InferredAssignment>.Builder hints,
        ImmutableArray<FieldWrite>.Builder writes,
        AstNode? increment = null)
    {
        // Inside the body the loop HAS run, so the zero-iteration alternative is not a possibility
        // the code at the cursor has to allow for.
        if ( ContainsCursor(body) )
        {
            WalkStatement(body, environment, hinted, hints, writes);
            return;
        }

        Dictionary<string, ScrValue> bodyEnvironment = Clone(environment);
        WalkStatement(body, bodyEnvironment, hinted, hints, writes);

        if ( increment is not null )
        {
            WalkStatement(increment, bodyEnvironment, hinted, hints, writes);
        }

        // One join suffices: a union only ever grows, so iterating to a fixpoint could not narrow
        // the answer this single pass gives.
        MergeAlternatives(environment, environment, bodyEnvironment);
    }

    /// <summary>
    /// A <c>foreach</c>, whose BINDINGS were never entered into the environment — so
    /// <c>foreach ( item in items )</c> left <c>item</c> untracked and nothing downstream could say
    /// anything about it. That is also the blocker for lowering a foreach into a <c>for</c> over
    /// <c>getarraykeys</c>, which needs to know the collection is an array.
    ///
    /// The collection is typed but its ELEMENT type is not modelled, so the value binding is an
    /// unknown carrying <see cref="ScrImprecision.ArrayElement"/> — enough to say the name is a
    /// local and to say why nothing more is known. A key, where the two-variable form is used, is a
    /// string or an int, which is the array-key rule rather than a guess.
    /// </summary>
    private void WalkForeach(
        ForeachNode foreachNode,
        Dictionary<string, ScrValue> environment,
        HashSet<string> hinted,
        ImmutableArray<InferredAssignment>.Builder hints,
        ImmutableArray<FieldWrite>.Builder writes)
    {
        // Typed for its effects, and for the collection's own sake: `foreach ( x in a[ 0 ] )` has an
        // expression worth walking.
        TypeExpressionForEffects(foreachNode.Collection, environment, hinted, hints, writes);

        // Inside the body the loop HAS run, so the bindings apply to the live environment and the
        // zero-iteration alternative is not a possibility the code at the cursor has to allow for.
        // Same shape as MergeLoopBody, and decided ONCE rather than asked twice.
        if ( ContainsCursor(foreachNode.Body) )
        {
            BindLoopVariables(foreachNode, environment);
            WalkStatement(foreachNode.Body, environment, hinted, hints, writes);
            return;
        }

        Dictionary<string, ScrValue> bodyEnvironment = Clone(environment);
        BindLoopVariables(foreachNode, bodyEnvironment);
        WalkStatement(foreachNode.Body, bodyEnvironment, hinted, hints, writes);

        // The bindings are NOT dropped before the join. GSC scopes locals to the function, not the
        // block — `for ( i = 0; … )` and then reading `i` afterwards is an ordinary idiom, and a
        // foreach binding is the same kind of variable — so after the loop the name holds the last
        // element, or is undefined where the collection was empty. Removing it here said instead
        // that the name kept whatever it held BEFORE the loop, which is the one thing it cannot be.
        MergeAlternatives(environment, environment, bodyEnvironment);
    }

    /// <summary>
    /// Enters a foreach's bindings. The element type is not modelled, so the value binding says only
    /// that the name IS a local and why nothing more is known; a key is a string or an int, which is
    /// the array-key rule rather than a guess.
    /// </summary>
    private static void BindLoopVariables(ForeachNode foreachNode, Dictionary<string, ScrValue> environment)
    {
        environment[foreachNode.ValueToken.Text] =
            ScrValue.Of(ScrTypeSet.Universe, ScrImprecision.ArrayElement);

        if ( foreachNode.KeyToken is PToken keyToken )
        {
            environment[keyToken.Text] =
                ScrValue.Of(ScrTypeSet.Int | ScrTypeSet.String, ScrImprecision.ArrayElement);
        }
    }

    /// <summary>
    /// Walks each case group as its own alternative path. Without a default label no group
    /// need run at all, so the pre-switch environment joins in as a further alternative.
    /// </summary>
    private void WalkSwitch(
        SwitchNode switchNode,
        Dictionary<string, ScrValue> environment,
        HashSet<string> hinted,
        ImmutableArray<InferredAssignment>.Builder hints,
        ImmutableArray<FieldWrite>.Builder writes)
    {
        List<Dictionary<string, ScrValue>> paths = new();

        foreach ( CaseGroupNode group in switchNode.Cases )
        {
            Dictionary<string, ScrValue> caseEnvironment = Clone(environment);
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

        Dictionary<string, ScrValue> merged = paths[0];
        for ( int index = 1; index < paths.Count; index++ )
        {
            MergeAlternatives(merged, merged, paths[index]);
        }

        environment.Clear();
        foreach ( KeyValuePair<string, ScrValue> entry in merged )
        {
            environment[entry.Key] = entry.Value;
        }
    }

    /// <summary>A null label marks the default group.</summary>
    private static bool HasDefaultLabel(SwitchNode switchNode)
    {
        foreach ( CaseGroupNode group in switchNode.Cases )
        {
            foreach ( CaseLabel label in group.Labels )
            {
                if ( label.Value is null )
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
        Dictionary<string, ScrValue> thenEnvironment,
        Dictionary<string, ScrValue> elseEnvironment)
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

        Dictionary<string, ScrValue> definedSide = negated ? elseEnvironment : thenEnvironment;
        Dictionary<string, ScrValue> undefinedSide = negated ? thenEnvironment : elseEnvironment;

        // Known to exist, so undefined comes out of the set. On the union lattice this is exact
        // rather than approximate: a name that was `Int | Undefined` becomes plain `Int` here, where
        // the flat lattice could only raise a pure Undefined up to Unknown and had no way to express
        // the mixed case at all.
        if ( definedSide.TryGetValue(name, out ScrValue existing) )
        {
            ScrValue defined = existing.Without(ScrTypeSet.Undefined);

            // Narrowing away everything means the guard contradicts the flow. Say nothing rather
            // than assert a value that cannot exist.
            definedSide[name] = defined.Types == ScrTypeSet.None ? ScrValue.Unknown : defined;
        }

        undefinedSide[name] = ScrValue.Of(ScrTypeSet.Undefined);
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

    private static Dictionary<string, ScrValue> Clone(Dictionary<string, ScrValue> environment)
    {
        return new Dictionary<string, ScrValue>(environment, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Replaces <paramref name="destination"/> with the join of two alternative paths.
    ///
    /// The join is now a set UNION rather than a collapse. Two arms assigning an int and a string
    /// produce <c>int|string</c>, where the flat lattice produced nothing usable — and the
    /// projection still reports Unknown to the editor, so nothing visible changes.
    ///
    /// A name typed on only one path unions with <c>undefined</c> rather than becoming anonymously
    /// unknown, because that is what is actually true: the other path did not assign it. That is
    /// also what makes a later <c>isdefined</c> narrowing able to recover the type exactly.
    /// </summary>
    private static void MergeAlternatives(
        Dictionary<string, ScrValue> destination,
        Dictionary<string, ScrValue> first,
        Dictionary<string, ScrValue> second)
    {
        Dictionary<string, ScrValue> joined = new(StringComparer.OrdinalIgnoreCase);
        ScrValue unassigned = ScrValue.Of(ScrTypeSet.Undefined);

        foreach ( KeyValuePair<string, ScrValue> entry in first )
        {
            joined[entry.Key] = second.TryGetValue(entry.Key, out ScrValue other)
                ? ScrValue.Union(entry.Value, other)
                : ScrValue.Union(entry.Value, unassigned);
        }

        foreach ( KeyValuePair<string, ScrValue> entry in second )
        {
            if ( !joined.ContainsKey(entry.Key) )
            {
                joined[entry.Key] = ScrValue.Union(entry.Value, unassigned);
            }
        }

        destination.Clear();
        foreach ( KeyValuePair<string, ScrValue> entry in joined )
        {
            destination[entry.Key] = entry.Value;
        }
    }

    private void TypeExpressionForEffects(ExprNode expression, Dictionary<string, ScrValue> environment, HashSet<string> hinted, ImmutableArray<InferredAssignment>.Builder hints, ImmutableArray<FieldWrite>.Builder writes)
    {
        // ++ and -- read and write in one step, so they are field writes too.
        MemberNode? incremented = IncrementedMember(expression);
        if ( incremented is not null )
        {
            writes.Add(new FieldWrite(
                incremented.NameToken.RootRange,
                incremented.NameToken.Text,
                TypeOf(incremented.Object, environment).ToScrType()));
            return;
        }

        // Parentheses around an assignment are how the deliberate form is written — `if ( ( x = f() ) )`
        // is what suppresses 3013 — so they must not hide the assignment from the walk.
        while ( expression is ParenNode paren )
        {
            expression = paren.Inner;
        }

        if ( expression is not AssignmentNode assignment )
        {
            // Not an assignment, so there is nothing to hint and nothing to bind — but a bare call
            // statement is still full of expressions, and `helper( 5 );` was contributing nothing to
            // the map at all. Typed for its value, which is then discarded.
            TypeOf(expression, environment);
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
                TypeOf(member.Object, environment).ToScrType(),
                assignment.Operator == TokenKind.Assign ? assignment.Value : null));

            // A field assignment is an assignment: `level.foo = "text"` says as much about foo as
            // `foo = "text"` says about the local, and the hint was missing on one and not the other
            // purely because this branch returned first.
            //
            // The type is the VALUE's, not the field's. Reading it back through TypeOfField would
            // consult the engine data instead and say nothing at all about a field the scripts
            // invented, which is most of them.
            if ( assignment.Operator == TokenKind.Assign
                && member.NameToken.Provenance.DefinitionSite is null )
            {
                ScrValue fieldValue = TypeOf(assignment.Value, environment);
                if ( fieldValue.ToScrType().IsKnown() )
                {
                    // Keyed by the whole path, so `self.count` and `level.count` are separate names
                    // and hinting one does not silently suppress the other.
                    string path = FieldPathOf(member);
                    hints.Add(new InferredAssignment(
                        member.NameToken.RootRange, fieldValue, member.NameToken.Text,
                        IsFirstForName: hinted.Add(path), IsField: true));
                }
            }

            return;
        }

        // Only plain `local = value` (the '=' operator) yields a type; compound ops keep
        // the existing type.
        if ( assignment.Operator != TokenKind.Assign || assignment.Target is not IdentifierNode target )
        {
            return;
        }

        ScrValue value = TypeOf(assignment.Value, environment);
        string name = target.Token.Text;
        environment[name] = value;

        // Projected only to decide whether there is a hint worth showing. The environment keeps the
        // full value, so a union survives the assignment even though no label is emitted for it.
        ScrType type = value.ToScrType();

        // An assignment inside a macro body reports the INVOCATION's range, so hinting it
        // would label the call site with a type the user never wrote there. The environment
        // still updates, since the assignment does happen.
        if ( target.Token.Provenance.DefinitionSite is not null )
        {
            return;
        }

        if ( type.IsKnown() )
        {
            hints.Add(new InferredAssignment(target.Token.RootRange, value, name, IsFirstForName: hinted.Add(name)));
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

    /// <summary>
    /// Types one expression, recording the answer when a caller asked for the whole map.
    ///
    /// Wrapped rather than folded into the switch so every return path is captured — including the
    /// early ones and the default — which is the difference between a map a rewriter can rely on
    /// and one with holes wherever a case returns directly.
    /// </summary>
    private ScrValue TypeOf(ExprNode expression, Dictionary<string, ScrValue> environment)
    {
        ScrValue value = TypeOfCore(expression, environment);

        if ( _recorded is not null )
        {
            _recorded[expression] = value;
        }

        return value;
    }

    private ScrValue TypeOfCore(ExprNode expression, Dictionary<string, ScrValue> environment)
    {
        switch ( expression )
        {
            case LiteralNode literal:
                return TypeOfLiteral(literal);
            case ParenNode paren:
                return TypeOf(paren.Inner, environment);
            case VectorNode vector:
                return TypeOfVector(vector, environment);
            case ArrayLiteralNode:
                return ScrValue.Of(ScrTypeSet.Array);
            case NewNode newNode:
                // A class instance, not a bare struct: the class name is part of the value's
                // identity and a rewriter lowering BO3 objects needs it.
                return ScrValue.Of(ScrTypeSet.Instance) with { InstanceClass = newNode.ClassToken.Text };
            case IdentifierNode identifier:
                return TypeOfIdentifier(identifier.Token.Text, environment);
            case PrefixNode prefix:
                return TypeOfPrefix(prefix, environment);
            case BinaryNode binary:
                return TypeOfBinary(binary, environment);
            case CallNode call:
                return TypeOfCall(call, environment);
            case MemberNode member:
                return TypeOfField(member);

            // `a[ i ]`. The element type is not modelled — neither did v1.5, whose indexer analysis
            // returned "any" unconditionally — but the BASE being indexed is the question that
            // matters, and the reason says so rather than leaving an anonymous unknown.
            //
            // This arm is what blocks v1.5's `CannotUseAsIndexer`: the INDEX expression is never
            // typed, so there is nothing for that rule to judge. Typing it is additive and belongs
            // in its own change — see FOLLOWUPS.md.
            case IndexNode:
                return ScrValue.Of(ScrTypeSet.Universe, ScrImprecision.ArrayElement);

            // Both arms are live, so the value is one or the other. The flat lattice had no way to
            // say that and returned Unknown.
            case TernaryNode ternary:
                return ScrValue.Union(
                    TypeOf(ternary.WhenTrue, environment),
                    TypeOf(ternary.WhenFalse, environment));

            // `x++` evaluates to the operand's value, and only a number can be incremented.
            case PostfixNode postfix when IsIncrementOrDecrement(postfix.Operator):
                return TypeOf(postfix.Operand, environment).Restrict(ScrTypeSet.Number);

            // A chained assignment evaluates to what was assigned: `a = b = 5`.
            case AssignmentNode chained when chained.Operator == TokenKind.Assign:
                return TypeOf(chained.Value, environment);

            // A class method call. The class is known but return types are not modelled for script
            // code, so this is the script-return case rather than an unsupported form.
            //
            // The object is typed for its effects, so `[[ thing ]]->bump()` records what `thing`
            // holds — which is how a method call is connected to the class declaring it.
            case ArrowCallNode arrowCall:
                TypeOf(arrowCall.Object, environment);
                foreach ( ExprNode argument in arrowCall.Arguments )
                {
                    TypeOf(argument, environment);
                }

                return ScrValue.Of(ScrTypeSet.Universe, ScrImprecision.ScriptFunctionReturn);

            // A bare `ns::foo` or `path\to\file::foo` with no argument list is a function pointer;
            // the parser only produces these outside call position.
            case QualifiedNode bareQualified:
                return ScrValue.Of(ScrTypeSet.Function) with { FunctionTarget = FunctionRefOf(bareQualified) };

            case PathQualifiedNode:
                return ScrValue.Of(ScrTypeSet.Function);

            // `[[ f ]]` names the function it dereferences.
            //
            // Still unconditionally a function, which is what blocks v1.5's `ExpectedFunction`: this
            // says what the DEREFERENCE is, never what the operand holds. The lattice can already
            // express "f is not a function" — nothing here judges the operand. See FOLLOWUPS.md.
            //
            // The operand IS typed, which is a different thing from being judged: it carries which
            // function the pointer holds, and a call site with no way to ask that can show nothing
            // about the function it is calling.
            case PointerDerefNode deref:
                return ScrValue.Of(ScrTypeSet.Function)
                    with { FunctionTarget = TypeOf(deref.Pointer, environment).FunctionTarget };

            default:
                return ScrValue.Unknown;
        }
    }

    /// <summary>
    /// A vector literal, folded when all three components are constant — which is most of them:
    /// <c>( 0, 0, 1 )</c> is the commonest expression in the corpora.
    /// </summary>
    private ScrValue TypeOfVector(VectorNode vector, Dictionary<string, ScrValue> environment)
    {
        ScrValue x = TypeOf(vector.X, environment);
        ScrValue y = TypeOf(vector.Y, environment);
        ScrValue z = TypeOf(vector.Z, environment);

        if ( x.Constant is { } cx && y.Constant is { } cy && z.Constant is { } cz
            && cx.Type is ScrTypeSet.Int or ScrTypeSet.Float
            && cy.Type is ScrTypeSet.Int or ScrTypeSet.Float
            && cz.Type is ScrTypeSet.Int or ScrTypeSet.Float )
        {
            return ScrValue.OfConstant(ScrConstant.OfVector(new Vec3(cx.AsDouble(), cy.AsDouble(), cz.AsDouble())));
        }

        return ScrValue.Of(ScrTypeSet.Vector);
    }

    /// <summary>
    /// The dotted path a field write names — <c>self.count</c>, <c>level.a.b</c> — for use as a
    /// hint key, so two fields sharing a name on different owners stay distinct. Falls back to the
    /// field name alone when the owner is something less nameable, like a call result.
    /// </summary>
    private static string FieldPathOf(MemberNode member)
    {
        switch ( member.Object )
        {
            case IdentifierNode owner:
                return owner.Token.Text + "." + member.NameToken.Text;
            case MemberNode owner:
                return FieldPathOf(owner) + "." + member.NameToken.Text;
            default:
                return member.NameToken.Text;
        }
    }

    /// <summary>
    /// Types a field access `owner.field`. `.size` is always int; otherwise the engine
    /// object-field data seeds a type, but only when every entity kind that declares the
    /// field name agrees (the owner's entity kind isn't inferred, so disagreement → Unknown).
    /// </summary>
    private ScrValue TypeOfField(MemberNode member)
    {
        string fieldName = member.NameToken.Text;
        if ( string.Equals(fieldName, "size", StringComparison.OrdinalIgnoreCase) )
        {
            return ScrValue.Of(ScrTypeSet.Int);
        }

        ImmutableArray<ObjectField> fields = _objectFields.FindField(fieldName);
        if ( fields.Length == 0 )
        {
            // A field the scripts invented, which is most of them. Named as such rather than left
            // anonymously unknown, so a rewriter can tell it from a field we simply failed to type.
            return ScrValue.Of(ScrTypeSet.Universe, ScrImprecision.StructField);
        }

        ScrTypeSet agreed = MapDeclaredType(fields[0].Type);
        for ( int index = 1; index < fields.Length; index++ )
        {
            if ( MapDeclaredType(fields[index].Type) != agreed )
            {
                // The declaring kinds disagree and the owner's kind is not inferred, so no
                // declaration can be the one that applies.
                return ScrValue.Of(ScrTypeSet.Universe, ScrImprecision.UnknownFieldOwner);
            }
        }

        return agreed == ScrTypeSet.None
            ? ScrValue.Of(ScrTypeSet.Universe, ScrImprecision.BuiltinTypeUnmapped)
            : ScrValue.Of(agreed);
    }

    /// <summary>
    /// A literal, carrying its VALUE and not only its type. This is where constant folding starts;
    /// everything <see cref="ScrOperators"/> can fold traces back to a literal read here.
    /// </summary>
    private ScrValue TypeOfLiteral(LiteralNode literal)
    {
        string text = literal.Token.Text;

        switch ( literal.Token.Kind )
        {
            case TokenKind.Integer:
                return long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out long integer)
                    ? ScrValue.OfConstant(ScrConstant.OfInt(integer))
                    : ScrValue.Of(ScrTypeSet.Int);

            case TokenKind.Hex:
                return text.Length > 2
                    && long.TryParse(text.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out long hex)
                    ? ScrValue.OfConstant(ScrConstant.OfInt(hex))
                    : ScrValue.Of(ScrTypeSet.Int);

            // A Treyarch #"…" is a compile-time hash, not a string and not really an int either.
            // The lattice gives it its own member; the projection maps it back onto int, which is
            // what the coarse lattice said.
            case TokenKind.HashString:
                return ScrValue.Of(ScrTypeSet.HashString);

            case TokenKind.Float:
                return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double real)
                    ? ScrValue.OfConstant(ScrConstant.OfFloat(real))
                    : ScrValue.Of(ScrTypeSet.Float);

            // The token's text is already interned, so it is carried as written — quotes included —
            // rather than substringed. ScrConstant.Content unquotes on demand for the rare caller
            // that wants the characters.
            case TokenKind.String:
                return ScrValue.OfConstant(ScrConstant.OfString(text));

            case TokenKind.LocalizedString:
                return ScrValue.OfConstant(ScrConstant.OfString(text, ScrTypeSet.IString));

            case TokenKind.True:
                return ScrValue.OfConstant(ScrConstant.OfBool(true));
            case TokenKind.False:
                return ScrValue.OfConstant(ScrConstant.OfBool(false));

            case TokenKind.Undefined:
                return ScrValue.OfConstant(ScrConstant.OfUndefined());

            default:
                // Anim references and #animtree are literals too, and nothing here can type them.
                return ScrValue.Of(ScrTypeSet.Universe, ScrImprecision.UnsupportedExpression);
        }
    }

    private ScrValue TypeOfIdentifier(string name, Dictionary<string, ScrValue> environment)
    {
        if ( environment.TryGetValue(name, out ScrValue value) )
        {
            return value;
        }

        switch ( name.ToLowerInvariant() )
        {
            case "self":
                return ScrValue.Of(ScrTypeSet.Entity);
            // world is a BO3+ global; where the dialect has no world, a bare "world" is an ordinary
            // name (the case falls through to default), so it isn't mistyped as the world struct.
            case "world" when _game.HasWorldObject:
            case "level":
            case "anim":
                return ScrValue.Of(ScrTypeSet.Struct);
            case "game":
                return ScrValue.Of(ScrTypeSet.Array);

            // `world` where the dialect has none. Reported as a distinct reason rather than an
            // anonymous unknown, since a transpiler re-homing world's fields needs to see it.
            case "world":
                return ScrValue.Of(ScrTypeSet.Universe, ScrImprecision.DialectGlobalAbsent);

            default:
                return ScrValue.Unknown;
        }
    }

    /// <summary>
    /// A prefix operator, delegated to <see cref="ScrOperators"/> — which is what makes <c>-vec</c>
    /// a vector rather than Unknown, and lets <c>!</c> and <c>~</c> fold a constant operand.
    /// </summary>
    private ScrValue TypeOfPrefix(PrefixNode prefix, Dictionary<string, ScrValue> environment)
    {
        if ( !TryMapUnary(prefix.Operator, out ScrUnaryOp op) )
        {
            return ScrValue.Unknown;
        }

        // Address-of needs no operand type, and asking for one would type the callee name as an
        // undefined local.
        ScrValue operand = op == ScrUnaryOp.AddressOf
            ? ScrValue.Unknown
            : TypeOf(prefix.Operand, environment);

        ScrValue value = ScrOperators.Apply(op, operand).Value;

        // `&helper` is the one place a pointer's target is written down. Recording it here is what
        // lets `[[ ptr ]]( ... )` further down the function name the function it calls.
        if ( op == ScrUnaryOp.AddressOf )
        {
            return value with { FunctionTarget = FunctionRefOf(prefix.Operand) };
        }

        return value;
    }

    /// <summary>
    /// The function an expression NAMES, for the two forms that name one: <c>helper</c> and
    /// <c>ns::helper</c>. Null for anything else, including a path-qualified reference — resolving
    /// one needs the path resolver, which this pass does not have.
    /// </summary>
    private static ScrFunctionRef? FunctionRefOf(ExprNode expression)
    {
        switch ( expression )
        {
            case IdentifierNode identifier:
                return new ScrFunctionRef(null, identifier.Token.Text);

            case QualifiedNode qualified:
                return new ScrFunctionRef(qualified.NamespaceToken.Text, qualified.NameToken.Text);

            default:
                return null;
        }
    }

    /// <summary>
    /// A binary operator, delegated to <see cref="ScrOperators"/>.
    ///
    /// This is where the <c>vector * 0.5</c> bug is actually fixed: the old code routed every
    /// arithmetic operator through a helper that took no operator and knew only Int/Float/Unknown,
    /// so a scaled vector came out a float. The operand diagnosis is discarded here — this pass
    /// types expressions and does not report — but it is what a rule or a rewriter would read.
    /// </summary>
    private ScrValue TypeOfBinary(BinaryNode binary, Dictionary<string, ScrValue> environment)
    {
        if ( !TryMapBinary(binary.Operator, out ScrBinaryOp op) )
        {
            return ScrValue.Unknown;
        }

        ScrValue left = TypeOf(binary.Left, environment);
        ScrValue right = TypeOf(binary.Right, environment);

        return ScrOperators.Apply(op, left, right).Value;
    }

    /// <summary>Maps a token kind onto the semantic operator. The one place syntax meets semantics.</summary>
    private static bool TryMapBinary(TokenKind kind, out ScrBinaryOp op)
    {
        switch ( kind )
        {
            case TokenKind.Plus: op = ScrBinaryOp.Add; return true;
            case TokenKind.Minus: op = ScrBinaryOp.Subtract; return true;
            case TokenKind.Star: op = ScrBinaryOp.Multiply; return true;
            case TokenKind.Slash: op = ScrBinaryOp.Divide; return true;
            case TokenKind.Percent: op = ScrBinaryOp.Modulo; return true;
            case TokenKind.EqualsEquals: op = ScrBinaryOp.Equal; return true;
            case TokenKind.NotEquals: op = ScrBinaryOp.NotEqual; return true;
            case TokenKind.StrictEquals: op = ScrBinaryOp.StrictEqual; return true;
            case TokenKind.StrictNotEquals: op = ScrBinaryOp.StrictNotEqual; return true;
            case TokenKind.LessThan: op = ScrBinaryOp.Less; return true;
            case TokenKind.LessThanEquals: op = ScrBinaryOp.LessOrEqual; return true;
            case TokenKind.GreaterThan: op = ScrBinaryOp.Greater; return true;
            case TokenKind.GreaterThanEquals: op = ScrBinaryOp.GreaterOrEqual; return true;
            case TokenKind.LogicalAnd: op = ScrBinaryOp.And; return true;
            case TokenKind.LogicalOr: op = ScrBinaryOp.Or; return true;
            case TokenKind.Ampersand: op = ScrBinaryOp.BitAnd; return true;
            case TokenKind.Pipe: op = ScrBinaryOp.BitOr; return true;
            case TokenKind.Caret: op = ScrBinaryOp.BitXor; return true;
            case TokenKind.ShiftLeft: op = ScrBinaryOp.ShiftLeft; return true;
            case TokenKind.ShiftRight: op = ScrBinaryOp.ShiftRight; return true;
            default: op = default; return false;
        }
    }

    private static bool TryMapUnary(TokenKind kind, out ScrUnaryOp op)
    {
        switch ( kind )
        {
            case TokenKind.Bang: op = ScrUnaryOp.Not; return true;
            case TokenKind.Minus: op = ScrUnaryOp.Negate; return true;
            case TokenKind.Tilde: op = ScrUnaryOp.BitNot; return true;
            case TokenKind.Ampersand: op = ScrUnaryOp.AddressOf; return true;
            default: op = default; return false;
        }
    }

    private ScrValue TypeOfCall(CallNode call, Dictionary<string, ScrValue> environment)
    {
        // The arguments are expressions in their own right and were never typed — the old code read
        // only the callee's name. A per-node map with holes wherever an argument sits is no use to a
        // rewriter, and inferring a parameter from its call sites needs exactly these values.
        foreach ( ExprNode argument in call.Arguments )
        {
            TypeOf(argument, environment);
        }

        if ( call.Target is not null )
        {
            TypeOf(call.Target, environment);
        }

        // `[[ ptr ]]( ... )`. The callee is typed only in this form: the other callees are NAMES,
        // and typing one would look up a local that does not exist and record it as undefined.
        if ( call.Callee is PointerDerefNode )
        {
            TypeOf(call.Callee, environment);
        }

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
            return ScrValue.Unknown;
        }

        // Callable keywords (isdefined, vectorscale) have no API entry, so their return types
        // come from the emulation table instead.
        if ( BuiltinEmulations.TryGetReturnType(name, out ScrType emulated) )
        {
            return ScrValue.FromScrType(emulated);
        }

        BuiltinFunction? builtin = _builtins.Find(name);
        if ( builtin is null || builtin.Overloads.Length == 0 )
        {
            // Either a script function, whose body is not re-typed here, or a name the game's
            // library does not carry. Both are worth telling apart from an untypeable expression.
            return ScrValue.Of(ScrTypeSet.Universe, ScrImprecision.ScriptFunctionReturn);
        }

        // The union across EVERY overload, parsed at load. The old code read overload zero and no
        // further, so a builtin whose overloads return different things was reported as returning
        // whichever happened to be listed first.
        ScrTypeSet mapped = builtin.ReturnTypes;

        if ( mapped == ScrTypeSet.None )
        {
            return ScrValue.Of(ScrTypeSet.Universe, ScrImprecision.BuiltinTypeUnmapped);
        }

        // A low-confidence entry is a weaker fact than a verified one, and saying so is what lets a
        // consumer decide for itself — v1.5 shipped a whole second diagnostic code because it had
        // nowhere to record this.
        return ScrValue.Of(
            mapped,
            builtin.Confidence == BuiltinConfidence.Low
                ? ScrImprecision.BuiltinUnverified
                : ScrImprecision.None);
    }

    /// <summary>
    /// Maps a declared type spelling from the bundled data onto the lattice. <see cref="ScrTypeSet.None"/>
    /// means the spelling is one this cannot express.
    ///
    /// Still text-driven and still dropping <c>any[]</c>, unions and <c>number</c> — the loader
    /// flattens the structured JSON to a display string before anything here can see it, and
    /// unpicking that is its own change. What is different is that failure is now REPORTED as
    /// <see cref="ScrImprecision.BuiltinTypeUnmapped"/> instead of being indistinguishable from
    /// every other unknown.
    /// </summary>
    private static ScrTypeSet MapDeclaredType(string typeText)
    {
        switch ( typeText.ToLowerInvariant() )
        {
            case "int": return ScrTypeSet.Int;
            case "float": return ScrTypeSet.Float;
            case "bool": return ScrTypeSet.Bool;
            case "string": return ScrTypeSet.String;
            case "istring": return ScrTypeSet.IString;
            case "vector": return ScrTypeSet.Vector;
            case "struct": return ScrTypeSet.Struct;
            case "entity": return ScrTypeSet.Entity;
            case "function": return ScrTypeSet.Function;
            default: return ScrTypeSet.None;
        }
    }
}
