using System.Collections.Immutable;
using GSCode.Core;
using GSCode.Core.Symbols;
using GSCode.Core.Text;
using GSCode.Parser;
using GSCode.Parser.Extraction;
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
        List<LocalUse> uses = [];

        // The parameter list is part of the function, but not part of its body, so it is walked
        // separately. A parameter is where the name is introduced — the caller supplied the value —
        // which is the same precedence LocalDefinition.Find applies.
        foreach ( ParameterNode parameter in function.Parameters )
        {
            if ( Matches(parameter.NameToken, name) )
            {
                uses.Add(new LocalUse(parameter.NameToken, LocalUseKind.Assign));
            }
        }

        foreach ( LocalUse use in LocalUses.Of(function.Body) )
        {
            if ( Matches(use.Token, name) )
            {
                uses.Add(use);
            }
        }

        ImmutableArray<LocalOccurrence>.Builder occurrences =
            ImmutableArray.CreateBuilder<LocalOccurrence>(uses.Count);
        foreach ( LocalUse use in uses )
        {
            occurrences.Add(new LocalOccurrence(use.Token.RootRange, use.IsWrite, IsDeclaration: false));
        }

        return MarkDeclaration(occurrences);
    }

    /// <summary>
    /// Whether <paramref name="name"/> is already taken where <paramref name="position"/>'s
    /// enclosing function can see it — a parameter, anything written anywhere in the body, or a
    /// name that arrives from OUTSIDE the function: a global object, an Infinity Ward file-scope
    /// constant, a class member.
    ///
    /// The collision test a rename has to make. Renaming `i` to a name the function already uses
    /// does not fail, it MERGES two variables into one, and the script keeps running while meaning
    /// something different — the worst shape a refactor can take in a language where an undefined
    /// read is not an error. The outside-in names are the same hazard in the other direction:
    /// renaming a local onto a member the method reads captures every one of those reads, which is
    /// why this applies the same list <see cref="IsFunctionScoped"/> does, spelled against the new
    /// name rather than a token.
    /// </summary>
    public static bool BindsName(ParseResult result, Position position, string name, GameProfile? profile = null)
    {
        if ( !TryFindLocal(result.Tree.Root, position, out PToken _, out FunctionNode function) )
        {
            return false;
        }

        GameProfile game = profile ?? GameProfile.Active;
        if ( NameArrivesFromOutside(result, position, name, game) )
        {
            return true;
        }

        foreach ( ParameterNode parameter in function.Parameters )
        {
            if ( Matches(parameter.NameToken, name) )
            {
                return true;
            }
        }

        foreach ( LocalUse use in LocalUses.Of(function.Body) )
        {
            if ( use.IsWrite && Matches(use.Token, name) )
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Semantic-token classification for every parameter and local in the file — the workspace
    /// half of highlighting. <see cref="SemanticTokenBuilder"/> classifies what the reference
    /// index knows (functions, classes, macros, fields) and lets everything else fall through,
    /// because <see cref="SymbolKind"/> has no member for a parameter or a local; this walk
    /// supplies those two from the AST, which is where a local's identity lives.
    ///
    /// A name is emitted only when the function BINDS it — a parameter, or written somewhere in
    /// the body — and it does not arrive from outside. A bare read of a name nothing binds stays
    /// uncoloured on purpose: it is undefined, and painting it like a variable would dress up
    /// exactly the mistake the unassigned-variable lint exists to report.
    /// </summary>
    public static ImmutableArray<SemanticToken> SemanticTokens(
        ParseResult result, GameProfile? profile = null)
    {
        GameProfile game = profile ?? GameProfile.Active;
        ImmutableArray<SemanticToken>.Builder tokens = ImmutableArray.CreateBuilder<SemanticToken>();

        foreach ( FunctionNode function in Functions(result.Tree.Root) )
        {
            List<LocalUse> uses = [];
            HashSet<string> parameters = new(StringComparer.OrdinalIgnoreCase);

            foreach ( ParameterNode parameter in function.Parameters )
            {
                parameters.Add(parameter.NameToken.Text);
                uses.Add(new LocalUse(parameter.NameToken, LocalUseKind.Assign));
            }

            uses.AddRange(LocalUses.Of(function.Body));

            HashSet<string> written = new(StringComparer.OrdinalIgnoreCase);
            foreach ( LocalUse use in uses )
            {
                if ( use.IsWrite )
                {
                    written.Add(use.Token.Text);
                }
            }

            // Decided once per function rather than once per occurrence: the outside-in checks
            // scan classes and file-scope declarations, and `i` does not change meaning between
            // its uses.
            Dictionary<string, bool> ownedByFunction = new(StringComparer.OrdinalIgnoreCase);

            foreach ( LocalUse use in uses )
            {
                PToken token = use.Token;

                // The characters at a macro invocation are the macro's, not a variable's, and the
                // engine-supplied tokens are nobody's to colour — the same per-token guards
                // IsFunctionScoped applies.
                if ( token.Provenance.DefinitionSite is not null
                    || token.Kind is TokenKind.Vararg or TokenKind.ThisThread )
                {
                    continue;
                }

                string name = token.Text;
                bool isParameter = parameters.Contains(name);
                if ( !isParameter && !written.Contains(name) )
                {
                    continue;
                }

                if ( !ownedByFunction.TryGetValue(name, out bool owned) )
                {
                    owned = !NameArrivesFromOutside(result, token.RootRange.Start, name, game);
                    ownedByFunction[name] = owned;
                }

                if ( !owned )
                {
                    continue;
                }

                tokens.Add(new SemanticToken(
                    token.RootRange.Start.Line,
                    token.RootRange.Start.Character,
                    name.Length,
                    isParameter ? SemanticTokenType.Parameter : SemanticTokenType.Variable));
            }
        }

        return tokens.ToImmutable();
    }

    /// <summary>
    /// Every function in the file — top-level, class methods, dev-block wrapped — without
    /// descending into their bodies, since GSC has no nested functions.
    /// </summary>
    private static IEnumerable<FunctionNode> Functions(AstNode node)
    {
        foreach ( AstNode child in AstSearch.ChildrenOf(node) )
        {
            if ( child is FunctionNode function )
            {
                yield return function;
            }
            else
            {
                foreach ( FunctionNode nested in Functions(child) )
                {
                    yield return nested;
                }
            }
        }
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

        return !NameArrivesFromOutside(result, position, token.Text, game);
    }

    /// <summary>
    /// The ways a bare name legitimately arrives from outside the enclosing function, shared by
    /// every caller that has to draw the local/not-local line: <see cref="IsFunctionScoped"/> for
    /// the name under the cursor, <see cref="BindsName"/> for a rename's new name, and
    /// <see cref="SemanticTokens"/> for every candidate in a file.
    /// </summary>
    private static bool NameArrivesFromOutside(
        ParseResult result, Position position, string name, GameProfile game)
    {
        // level / self / world / anim / game, from the profile so a dialect gets exactly its own.
        foreach ( string global in game.GlobalObjectNames )
        {
            if ( string.Equals(global, name, StringComparison.OrdinalIgnoreCase) )
            {
                return true;
            }
        }

        // The Infinity Ward dialects allow a constant at FILE scope — `BRIDGE_COLLAPSE_SPEED = 1.0;`
        // between two functions, readable from all of them. Its references are not this function's
        // to list, and answering with only this function's would hide every other reader.
        if ( game.HasFileScopeConstants && IsFileScopeConstant(result.Tree.Root.Elements, name) )
        {
            return true;
        }

        // Inside a class method a bare name may be a `var` member, whose readers are other methods
        // and potentially other files entirely.
        if ( IsClassMember(result, position, name) )
        {
            return true;
        }

        return false;
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
}
