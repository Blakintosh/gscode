using System.Collections.Immutable;
using GSCode.Core;
using GSCode.Core.Symbols;
using GSCode.Core.Text;
using GSCode.Parser;
using GSCode.Parser.Lexing;
using GSCode.Parser.Syntax;
using GSCode.Workspace.Api;
using GSCode.Workspace.Database;

namespace GSCode.Workspace.Completion;

/// <summary>
/// Context-aware completion driven by the token stream around the cursor. Detects the
/// distinct positions (after `::`, after `.`, in a `#precache` type, in a `#using`/`#insert`
/// path, or plain statement scope) and produces the appropriate suggestions.
/// </summary>
public sealed partial class CompletionEngine
{
    private readonly ScriptDatabase _database;
    private readonly BuiltinApiSet _builtins;
    private readonly ObjectFields _objectFields;

    public CompletionEngine(ScriptDatabase database, BuiltinApiSet builtins, ObjectFields objectFields)
    {
        _database = database;
        _builtins = builtins;
        _objectFields = objectFields;
    }

    /// <summary>Produces completion suggestions for a position in an analysed document.</summary>
    /// <param name="includeLiterals">Whether to offer known literals inside a "..."/&amp;"..."/#"..." string (the gscode.completion.literals setting).</param>
    /// <param name="fieldScope">How widely assignment-derived fields are offered after a `.` (the gscode.completion.fieldScope setting).</param>
    /// <param name="profile">
    /// The dialect to complete for; defaults to the active one. Explicit for the same reason
    /// <c>ScriptAnalysis.Analyze</c> takes it — a test naming its dialect does not have to mutate
    /// process-global state, and cannot be perturbed by a test that does.
    /// </param>
    public ImmutableArray<CompletionEntry> Complete(
        ParseResult result,
        string contextId,
        Position position,
        bool includeLiterals = true,
        FieldScope fieldScope = FieldScope.Owner,
        CallPunctuation callPunctuation = CallPunctuation.Parens,
        GameProfile? profile = null,
        bool parameterHints = true)
    {
        GameProfile game = profile ?? GameProfile.Active;
        ImmutableArray<Token> tokens = result.Lexed.Tokens;
        int offset = result.Text.GetOffset(position);

        // Inside a string/istring/hash literal: offer known literals of that kind (or nothing,
        // since statement-scope suggestions never make sense inside a string).
        int literalIndex = FindLiteralAtOffset(tokens, offset);
        if ( literalIndex >= 0 )
        {
            // `#precache( "<here>"` is an asset TYPE, not free text. The quote is a completion
            // trigger character, so by the time this fires the cursor is already inside a string
            // token — and generic literal completion would win and offer every string in the
            // workspace. This has to be asked first, and independently of the literals setting:
            // the asset type is a closed vocabulary, not a convenience.
            if ( IsPrecacheAssetTypeLiteral(tokens, literalIndex) )
            {
                return AssetTypeCompletions(result.Language, quoted: false);
            }

            if ( !includeLiterals )
            {
                return [];
            }

            return LiteralCompletions(result, contextId, LiteralKindOf(tokens[literalIndex].Kind));
        }

        // The token being typed (if the cursor sits in/just after an identifier) and the
        // trigger token before it drive the context decision.
        int currentIndex = FindCurrentWordIndex(tokens, offset);
        int triggerIndex = PreviousSignificant(tokens, currentIndex >= 0 ? currentIndex : FirstAtOrAfter(tokens, offset));

        // Every context below is detected by looking BACKWARD for a trigger character, which
        // answers "what did the user just type" but not "is this construct legal here". The
        // directive family is top level ONLY, so inside a function body the backward scan finds a
        // '#' and confidently offers #using, #insert and #namespace in the middle of a call.
        // The enclosing declaration itself, not just whether there is one. It answers three
        // questions that used to be asked separately and walked the same ranges each time: which
        // keyword set is legal, whether `vararg` binds, and — the reason it is carried rather than
        // reduced to a bool — WHICH parameters and locals are in scope.
        FunctionSymbol? enclosingFunction = EnclosingFunction(result, position);
        bool insideFunction = enclosingFunction is not null;

        if ( !insideFunction )
        {
            // #precache( "type" ...) — offer asset types as the first argument.
            if ( TryPrecacheContext(tokens, triggerIndex) )
            {
                return AssetTypeCompletions(result.Language);
            }

            // #using / #insert / #include path — offer path segments.
            ImmutableArray<CompletionEntry> pathCompletions = TryPathContext(result, contextId, tokens, currentIndex, offset);
            if ( !pathCompletions.IsDefault )
            {
                return pathCompletions;
            }
        }
        else if ( IsAfterDirectiveHash(result.Text, offset) )
        {
            // A '#' inside a function body has TWO readings, and both are offered.
            //
            // The preprocessor walks a flat token stream — Preprocessor.ProcessRange dispatches
            // #define, #insert and the #if chain with no top-level restriction — so the
            // conditional family is as legal in a body as it is at file scope, and wrapping one
            // argument or one statement in an #if is what it is for. Assuming a hash string here
            // meant typing "#i" in a body offered string literals and never a directive.
            //
            // The hash string is the OTHER reading, and only on a dialect that has one: CoD4, WaW
            // and MW2 have no #"..." literal at all, so there the branch returned a hard empty
            // array for any '#' typed in a body — and '#' is a completion trigger character, so
            // that empty list popped over the cursor.
            //
            // The quotes come with the literal because the cursor is not inside a string yet.
            ImmutableArray<CompletionEntry> directives =
                DirectiveCompletions(game, GscKeywords.BodyDirectives);

            return game.HasHashStrings && includeLiterals
                ? directives.AddRange(LiteralCompletions(result, contextId, SymbolKind.HashString, quoted: true))
                : directives;
        }

        // An INLINE path call — `maps\mp\_utility::foo()`. On the dialects that have them a file is
        // reached by naming its path in the middle of an expression, with no import at all, so the
        // same folder-walk the directives get belongs here too: there is no other way to discover
        // what a path may continue into.
        //
        // Gated on the capability, so nothing changes where the syntax does not exist. The path is
        // read from the raw text rather than the tokens for the reason IsAfterDirectiveHash gives —
        // a half-typed path is a run of identifiers and separators that never lexes as one thing.
        if ( game.HasInlinePathCalls )
        {
            string inlinePath = InlinePathBefore(result.Text, offset);
            if ( inlinePath.Length > 0 )
            {
                return PathSegmentCompletions(result, contextId, isInsert: false, inlinePath);
            }
        }

        // `function <here>` — a declaration NAME, not a reference to anything.
        //
        // Nothing callable can legally follow the keyword, so the statement-scope list was pure
        // noise here: every builtin, every macro, every global. What is left is small and genuinely
        // belongs — the modifiers that may follow the keyword, and the script functions already
        // declared, which are worth seeing so an override lands on the right name and a collision
        // is visible before it is written.
        if ( triggerIndex >= 0 && IsFunctionDeclarationName(tokens, triggerIndex) )
        {
            return DeclarationNameCompletions(result, contextId, game);
        }

        // `case 1:` with nothing typed after it — the colon ENDS a label, so the list popped over
        // something already finished. ':' is a completion trigger because of `ns::`, and a lone
        // colon is a different token, so the position fell through to statement scope.
        //
        // ONLY when nothing is being typed. The trigger token stays the colon for everything up to
        // the end of the first statement in that case, so suppressing on it alone silenced the
        // whole case body — `case 1:` then `lev` offered nothing at all, which is far worse than
        // the list appearing a moment early. A word under the cursor means a statement is being
        // written, and that wants the ordinary suggestions.
        //
        // A ternary's colon is a separate matter — `a ? b : <here>` begins an expression and wants
        // the list immediately — so the two are still told apart.
        if ( currentIndex < 0 && triggerIndex >= 0 && tokens[triggerIndex].Kind == TokenKind.Colon
            && IsCaseLabelColon(tokens, triggerIndex) )
        {
            return [];
        }

        // `util:` — HALF of a `::`, and the same trigger character is what opened the list. The
        // only thing that can legally follow is the second colon, so statement scope here is a list
        // of things none of which can be written, sitting over a qualifier mid-keystroke. Showing
        // nothing lets the next ':' arrive and open the namespace's own list, which is what was
        // being reached for.
        if ( currentIndex < 0 && triggerIndex >= 0 && tokens[triggerIndex].Kind == TokenKind.Colon
            && IsIncompleteScopeResolution(tokens, triggerIndex) )
        {
            return [];
        }

        // ns:: — offer functions in that namespace only.
        if ( triggerIndex >= 0 && tokens[triggerIndex].Kind == TokenKind.ScopeResolution )
        {
            int nsIndex = PreviousSignificant(tokens, triggerIndex);
            if ( nsIndex >= 0 && tokens[nsIndex].Kind == TokenKind.Identifier )
            {
                string ns = tokens[nsIndex].GetText(result.Text).ToString().ToLowerInvariant();
                return NamespaceFunctionCompletions(
                    result, contextId, ns, CallSnippet(tokens, currentIndex, offset, callPunctuation), parameterHints);
            }
        }

        // [[receiver]]-> — offer methods. The arrow is the one syntax that can ONLY be a class
        // method call, so nothing else belongs in this list.
        if ( triggerIndex >= 0 && tokens[triggerIndex].Kind == TokenKind.Arrow )
        {
            return ArrowMethodCompletions(
                result,
                contextId,
                ArrowReceiverClass(result, tokens, triggerIndex, position),
                CallSnippet(tokens, currentIndex, offset, callPunctuation),
                parameterHints);
        }

        // owner. — offer fields.
        if ( triggerIndex >= 0 && tokens[triggerIndex].Kind == TokenKind.Dot )
        {
            return FieldCompletions(result, contextId, OwnerBefore(result, tokens, triggerIndex), fieldScope);
        }

        // File scope holds no STATEMENTS, so nothing completed there takes a terminator.
        // IsStatementPosition scans back from the caret, finds the previous function's '}' and
        // answers true — right inside a body and meaningless outside one. The macro that stands
        // alone at that position expands to a DECLARATION (REGISTER_SYSTEM writes `function
        // autoexec ...() { }`), and all 447 of its uses in the shipped BO3 scripts are written
        // without a semicolon.
        CallPunctuation punctuation = callPunctuation;
        if ( !insideFunction && punctuation == CallPunctuation.ParensAndSemicolon )
        {
            punctuation = CallPunctuation.Parens;
        }

        return StatementScopeCompletions(
            result,
            contextId,
            offset,
            position,
            enclosingFunction,
            CallSnippet(tokens, currentIndex, offset, punctuation),
            game,
            parameterHints);
    }
}
