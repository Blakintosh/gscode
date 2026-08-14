using System.Collections.Immutable;
using GSCode.Core;
using GSCode.Core.Symbols;
using GSCode.Core.Text;
using GSCode.Parser;
using GSCode.Parser.Lexing;
using GSCode.Parser.Preprocessing;
using GSCode.Parser.Syntax;
using GSCode.Parser.Syntax.Ast;

namespace GSCode.Workspace.Database;

/// <summary>One occurrence of a local: where it is, and how the name is used there.</summary>
/// <param name="IsWrite">The name is WRITTEN here — an assignment target, a loop binding, a
/// <c>waittill</c> output, or the parameter itself.</param>
/// <param name="IsDeclaration">
/// This is where the name is INTRODUCED: the parameter, or the first write when there is no
/// parameter. The same rule <see cref="Analysis.UnusedLocalLint"/> reports against — a later write
/// is a reference to something that already exists.
/// </param>
public readonly record struct LocalOccurrence(TextRange Range, bool IsWrite, bool IsDeclaration);

/// <summary>
/// Find-all-references for a LOCAL: every occurrence of a variable within the function that scopes
/// it.
///
/// The companion to <see cref="LocalDefinition"/>, and it exists for the same reason. Locals are not
/// in the reference index and deliberately so — the index is keyed by <see cref="SymbolKey"/> and
/// shared across the workspace, while an `i` in one function has nothing to do with an `i` in
/// another, so putting them there would make every local in every file collide. That leaves
/// find-references, highlight and rename with nothing to find on a variable, which is the reported
/// symptom.
///
/// Resolved from the AST instead, per function, which is the scope a local actually has. GSC has no
/// block scoping — a name written inside an `if` lives for the rest of the function, which is why
/// <see cref="Typing.FlowTyper"/> does not drop bindings before a control-flow join — so identity
/// here is (enclosing function, name), matched case-insensitively as everywhere else.
///
/// A CONSTRUCTOR or DESTRUCTOR body answers nothing, and that is not an oversight:
/// <see cref="AstSearch.TryFindLocalContext"/> only reports a <see cref="FunctionNode"/> as the
/// enclosing scope, and a constructor exists to initialise members it never reads. Reaching those
/// needs member resolution first, the same conclusion <see cref="Analysis.UnusedLocalLint"/> came
/// to.
/// </summary>
public static class LocalReferences
{
    /// <summary>
    /// Every occurrence of the local under <paramref name="position"/> within its function, in
    /// source order. Empty when the position is not on one, or when the name is not the function's
    /// to own — see the guards in <see cref="IsFunctionScoped"/>.
    /// </summary>
    public static ImmutableArray<LocalOccurrence> Find(
        ParseResult result, Position position, GameProfile? profile = null)
    {
        if ( !TryFindLocal(result.Tree.Root, position, out PToken token, out FunctionNode function) )
        {
            return [];
        }

        GameProfile game = profile ?? GameProfile.Active;
        if ( !IsFunctionScoped(result, position, token, game) )
        {
            return [];
        }

        string name = token.Text;
        ImmutableArray<LocalOccurrence>.Builder occurrences = ImmutableArray.CreateBuilder<LocalOccurrence>();

        // The parameter list is part of the function, but not part of its body, so it is walked
        // separately. A parameter is where the name is introduced — the caller supplied the value —
        // which is the same precedence LocalDefinition.Find applies.
        foreach ( ParameterNode parameter in function.Parameters )
        {
            if ( Matches(parameter.NameToken, name) )
            {
                Add(parameter.NameToken, isWrite: true, occurrences);
            }
        }

        Collect(function.Body, name, occurrences);

        return MarkDeclaration(occurrences);
    }

    /// <summary>
    /// Whether the function enclosing <paramref name="position"/> already binds
    /// <paramref name="name"/> — a parameter, or anything written anywhere in the body.
    ///
    /// The collision test a rename has to make. Renaming `i` to a name the function already uses
    /// does not fail, it MERGES two variables into one, and the script keeps running while meaning
    /// something different — the worst shape a refactor can take in a language where an undefined
    /// read is not an error.
    /// </summary>
    public static bool BindsName(ParseResult result, Position position, string name)
    {
        if ( !TryFindLocal(result.Tree.Root, position, out PToken _, out FunctionNode function) )
        {
            return false;
        }

        foreach ( ParameterNode parameter in function.Parameters )
        {
            if ( Matches(parameter.NameToken, name) )
            {
                return true;
            }
        }

        ImmutableArray<LocalOccurrence>.Builder occurrences = ImmutableArray.CreateBuilder<LocalOccurrence>();
        Collect(function.Body, name, occurrences);

        foreach ( LocalOccurrence occurrence in occurrences )
        {
            if ( occurrence.IsWrite )
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The name token under the cursor and the function enclosing it.
    ///
    /// Not <see cref="AstSearch.TryFindLocalContext"/>, which reports an
    /// <see cref="IdentifierNode"/>, and a BINDING is not one: a parameter, a <c>foreach</c> key or
    /// value and a <c>const</c> name are all bare tokens hanging off their declaring node. Clicking
    /// the `item` in <c>foreach ( item in list )</c> therefore found no identifier at all and
    /// answered nothing — on the one occurrence a user is most likely to click, since it is where
    /// the name is introduced.
    ///
    /// The chain runs outermost to innermost, so the last match wins and a nested binding beats an
    /// enclosing one.
    /// </summary>
    private static bool TryFindLocal(
        ScriptNode root, Position position, out PToken token, out FunctionNode function)
    {
        PToken found = default;
        bool haveToken = false;
        FunctionNode? enclosing = null;

        foreach ( AstNode node in AstSearch.ChainAt(root, position) )
        {
            switch ( node )
            {
                case FunctionNode candidate:
                    enclosing = candidate;
                    continue;

                case IdentifierNode identifier:
                    found = identifier.Token;
                    haveToken = true;
                    continue;

                case ParameterNode parameter when parameter.NameToken.Range.Contains(position):
                    found = parameter.NameToken;
                    haveToken = true;
                    continue;

                case ConstDeclNode constDecl when constDecl.NameToken.Range.Contains(position):
                    found = constDecl.NameToken;
                    haveToken = true;
                    continue;

                case ForeachNode foreachNode:
                    if ( foreachNode.KeyToken is not null
                        && foreachNode.KeyToken.Value.Range.Contains(position) )
                    {
                        found = foreachNode.KeyToken.Value;
                        haveToken = true;
                    }
                    else if ( foreachNode.ValueToken.Range.Contains(position) )
                    {
                        found = foreachNode.ValueToken;
                        haveToken = true;
                    }

                    continue;
            }
        }

        token = found;
        function = enclosing!;
        return haveToken && enclosing is not null;
    }

    /// <summary>
    /// Whether this name is genuinely scoped to the enclosing function, rather than something that
    /// merely looks like a local at the cursor.
    ///
    /// Each rejection is a way a name legitimately arrives from outside, and every one of them is a
    /// case where a per-function answer would be actively wrong rather than merely incomplete.
    /// </summary>
    private static bool IsFunctionScoped(
        ParseResult result, Position position, PToken token, GameProfile game)
    {
        // Came out of a macro body. The characters under the cursor are the macro invocation's, so
        // the ranges would point at text the author did not write, and the name is not theirs.
        if ( token.Provenance.DefinitionSite is not null )
        {
            return false;
        }

        // Engine-supplied names nobody binds. By token kind rather than by spelling, so the dialect
        // gate comes free: on a game whose keyword set lacks the word it lexes as a plain
        // identifier and gets the ordinary treatment. Same test UnassignedVariableLint makes.
        if ( token.Kind is TokenKind.Vararg or TokenKind.ThisThread )
        {
            return false;
        }

        // level / self / world / anim / game, from the profile so a dialect gets exactly its own.
        foreach ( string global in game.GlobalObjectNames )
        {
            if ( string.Equals(global, token.Text, StringComparison.OrdinalIgnoreCase) )
            {
                return false;
            }
        }

        // The Infinity Ward dialects allow a constant at FILE scope — `BRIDGE_COLLAPSE_SPEED = 1.0;`
        // between two functions, readable from all of them. Its references are not this function's
        // to list, and answering with only this function's would hide every other reader.
        if ( game.HasFileScopeConstants && IsFileScopeConstant(result.Tree.Root.Elements, token.Text) )
        {
            return false;
        }

        // Inside a class method a bare name may be a `var` member, whose readers are other methods
        // and potentially other files entirely.
        if ( IsClassMember(result, position, token.Text) )
        {
            return false;
        }

        return true;
    }

    /// <summary>Whether a name is declared at file scope, including inside a dev block at that level.</summary>
    private static bool IsFileScopeConstant(IEnumerable<AstNode> elements, string name)
    {
        foreach ( AstNode element in elements )
        {
            switch ( element )
            {
                case FileScopeConstantNode constant:
                    if ( Matches(constant.NameToken, name) )
                    {
                        return true;
                    }

                    continue;

                case DevBlockDeclNode devBlock:
                    if ( IsFileScopeConstant(devBlock.Declarations, name) )
                    {
                        return true;
                    }

                    continue;
            }
        }

        return false;
    }

    /// <summary>
    /// Whether the name is a <c>var</c> member of the class containing this position, or of one of
    /// its ancestors declared in the same file.
    ///
    /// Same-file only, because this resolver takes a ParseResult and nothing else — the same scope
    /// LocalDefinition works in. An inherited member from another file therefore still answers as a
    /// local, which under-reports rather than pointing somewhere wrong, and is the recoverable half
    /// of the trade.
    /// </summary>
    private static bool IsClassMember(ParseResult result, Position position, string name)
    {
        ClassSymbol? enclosing = null;
        foreach ( ClassSymbol candidate in result.Extraction.Classes )
        {
            if ( candidate.FullRange.Contains(position) )
            {
                enclosing = candidate;
                break;
            }
        }

        // Bounded by the class count: a cycle in the parent chain cannot spin here, and
        // ClassCycleLint reports one separately.
        int remaining = result.Extraction.Classes.Length;
        while ( enclosing is not null && remaining > 0 )
        {
            foreach ( MemberSymbol member in enclosing.Members )
            {
                if ( string.Equals(member.Name, name, StringComparison.OrdinalIgnoreCase) )
                {
                    return true;
                }
            }

            enclosing = FindClass(result.Extraction.Classes, enclosing.ParentKeyName);
            remaining--;
        }

        return false;
    }

    private static ClassSymbol? FindClass(ImmutableArray<ClassSymbol> classes, string? keyName)
    {
        if ( keyName is null )
        {
            return null;
        }

        foreach ( ClassSymbol candidate in classes )
        {
            if ( string.Equals(candidate.KeyName, keyName, StringComparison.OrdinalIgnoreCase) )
            {
                return candidate;
            }
        }

        return null;
    }

    /// <summary>
    /// Walks a function body, recording every occurrence of one name and whether it is written
    /// there.
    ///
    /// Descends through <see cref="AstSearch.ChildrenOf"/> rather than a switch over every node
    /// kind, the same way <see cref="Analysis.UnassignedVariableLint"/> and
    /// <see cref="Analysis.UnusedLocalLint"/> do. The interesting nodes are few — assignments, the
    /// binding forms, and the places an identifier is not a variable at all — and enumerating
    /// children generically means a node type added later is traversed without this file having to
    /// learn about it.
    /// </summary>
    private static void Collect(
        AstNode node, string name, ImmutableArray<LocalOccurrence>.Builder occurrences)
    {
        switch ( node )
        {
            case AssignmentNode assignment:
                // The whole target is a WRITE, down to the name it is rooted at. `a[ 0 ] = x`
                // CREATES `a` when it does not exist — that is how a GSC array is built, and
                // `quotes[ quotes.size ] = "…"` appears all through the stock scripts. The
                // subscript itself is still read: `a[ i ] = x` genuinely reads `i`.
                CollectAssignmentTarget(assignment.Target, name, occurrences);
                Collect(assignment.Value, name, occurrences);
                return;

            case ForeachNode foreachNode:
                // `foreach ( key, value in … )` BINDS both — the loop writes them each pass.
                if ( foreachNode.KeyToken is not null && Matches(foreachNode.KeyToken.Value, name) )
                {
                    Add(foreachNode.KeyToken.Value, isWrite: true, occurrences);
                }

                if ( Matches(foreachNode.ValueToken, name) )
                {
                    Add(foreachNode.ValueToken, isWrite: true, occurrences);
                }

                Collect(foreachNode.Collection, name, occurrences);
                Collect(foreachNode.Body, name, occurrences);
                return;

            case ConstDeclNode constDecl:
                if ( Matches(constDecl.NameToken, name) )
                {
                    Add(constDecl.NameToken, isWrite: true, occurrences);
                }

                Collect(constDecl.Value, name, occurrences);
                return;

            case IdentifierNode identifier:
                if ( Matches(identifier.Token, name) )
                {
                    Add(identifier.Token, isWrite: false, occurrences);
                }

                return;

            case MemberNode member:
                // `a.b` reads `a`; `b` is a field name rather than a variable, and a field of that
                // spelling has a life of its own that another script may read.
                Collect(member.Object, name, occurrences);
                return;

            case CallNode call:
            {
                // The Callee of `foo()` names a FUNCTION, so it is not a use of a local spelled
                // foo. `[[ handler ]]()` is different: that really does read the local.
                if ( call.Callee is not (IdentifierNode or QualifiedNode or PathQualifiedNode) )
                {
                    Collect(call.Callee, name, occurrences);
                }

                // Target is what the call is made ON — `self` in `self foo()` — which is a value.
                if ( call.Target is not null )
                {
                    Collect(call.Target, name, occurrences);
                }

                // `self waittill( "damage", attacker, amount );` BINDS attacker and amount: they
                // are outputs the engine fills in, not values being read. The first argument is the
                // event NAME and is a genuine read.
                bool bindsOutputs = AstSearch.IsWaittill(call.Callee);

                for ( int index = 0; index < call.Arguments.Length; index++ )
                {
                    if ( bindsOutputs && index > 0 && call.Arguments[index] is IdentifierNode bound )
                    {
                        if ( Matches(bound.Token, name) )
                        {
                            Add(bound.Token, isWrite: true, occurrences);
                        }

                        continue;
                    }

                    Collect(call.Arguments[index], name, occurrences);
                }

                return;
            }

            case PrefixNode prefix when prefix.Operator == TokenKind.Ampersand:
                // `&foo` is a pointer to a FUNCTION, not a use of a variable.
                return;

            default:
                foreach ( AstNode child in AstSearch.ChildrenOf(node) )
                {
                    Collect(child, name, occurrences);
                }

                return;
        }
    }

    /// <summary>
    /// Records an assignment target: the name it is rooted at is WRITTEN, while any subscript
    /// expression along the way is read.
    /// </summary>
    private static void CollectAssignmentTarget(
        ExprNode target, string name, ImmutableArray<LocalOccurrence>.Builder occurrences)
    {
        switch ( target )
        {
            case IdentifierNode identifier:
                if ( Matches(identifier.Token, name) )
                {
                    Add(identifier.Token, isWrite: true, occurrences);
                }

                return;

            case IndexNode index:
                CollectAssignmentTarget(index.Object, name, occurrences);
                Collect(index.Index, name, occurrences);
                return;

            case MemberNode member:
                CollectAssignmentTarget(member.Object, name, occurrences);
                return;

            default:
                // Anything else — a call result, a deref — introduces no name, so the ordinary read
                // rules apply.
                Collect(target, name, occurrences);
                return;
        }
    }

    /// <summary>
    /// Marks the occurrence that INTRODUCES the name: the parameter when there is one, else the
    /// first write in source order.
    ///
    /// The first write, not every write, for the reason UnusedLocalLint reports against it — it is
    /// where the name comes into existence, and a later one writes to something that already does.
    /// A parameter needs no separate case: it is added before the body walk, so the first write in
    /// the list IS the parameter whenever one exists.
    /// </summary>
    private static ImmutableArray<LocalOccurrence> MarkDeclaration(
        ImmutableArray<LocalOccurrence>.Builder occurrences)
    {
        for ( int index = 0; index < occurrences.Count; index++ )
        {
            if ( !occurrences[index].IsWrite )
            {
                continue;
            }

            occurrences[index] = occurrences[index] with { IsDeclaration = true };
            return occurrences.ToImmutable();
        }

        return occurrences.ToImmutable();
    }

    private static bool Matches(PToken token, string name)
    {
        return string.Equals(token.Text, name, StringComparison.OrdinalIgnoreCase);
    }

    private static void Add(
        PToken token, bool isWrite, ImmutableArray<LocalOccurrence>.Builder occurrences)
    {
        occurrences.Add(new LocalOccurrence(token.RootRange, isWrite, IsDeclaration: false));
    }
}
