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
/// Where the cursor IS — the half of completion that decides which list to build, before any list
/// is built.
///
/// Every member here is static and reads only the token stream, the source text and the parse tree.
/// That is the property worth preserving: these answer "what did the user just type and what may
/// legally go here", and a question that needed the record store would belong on the producer side
/// instead. Splitting them out is what makes the dispatcher in <c>CompletionEngine.Complete</c>
/// readable as a list of contexts rather than as a wall of scanning.
/// </summary>
public sealed partial class CompletionEngine
{
    /// <summary>
    /// Whether the cursor sits where a function's NAME goes: directly after the <c>function</c>
    /// keyword, or after one of the modifiers that may follow it (<c>function private foo()</c>).
    ///
    /// Only meaningful on a dialect that has the keyword at all — under <c>#include</c> a
    /// declaration opens with the bare name, so there is no such position to detect.
    /// </summary>
    private static bool IsFunctionDeclarationName(ImmutableArray<Token> tokens, int triggerIndex)
    {
        switch ( tokens[triggerIndex].Kind )
        {
            case TokenKind.Function:
                return true;

            // A modifier only puts us here when it is itself modifying a `function`; `private` can
            // introduce other things, and guessing from the modifier alone would suppress the list
            // wherever else it appears.
            case TokenKind.Private:
            case TokenKind.Autoexec:
                int previous = PreviousSignificant(tokens, triggerIndex);
                return previous >= 0 && tokens[previous].Kind == TokenKind.Function;

            default:
                return false;
        }
    }

    /// <summary>
    /// Whether the colon at <paramref name="colonIndex"/> terminates a <c>case</c>/<c>default</c>
    /// label rather than dividing a ternary.
    ///
    /// Decided by scanning back for whichever comes first: the <c>case</c>/<c>default</c> that owns
    /// it, the <c>?</c> that would make it a ternary, or a statement boundary meaning neither is in
    /// play. Matching `case` alone would be wrong inside a switch, where
    /// <c>case 1: x = a ? b : c;</c> has both in the same statement and the nearest one is what
    /// decides.
    /// </summary>
    private static bool IsCaseLabelColon(ImmutableArray<Token> tokens, int colonIndex)
    {
        for ( int index = colonIndex - 1; index >= 0; index-- )
        {
            switch ( tokens[index].Kind )
            {
                case TokenKind.Case:
                case TokenKind.Default:
                    return true;

                case TokenKind.QuestionMark:
                case TokenKind.Semicolon:
                case TokenKind.OpenBrace:
                case TokenKind.CloseBrace:
                    return false;
            }
        }

        return false;
    }

    /// <summary>
    /// Whether the lone colon at <paramref name="colonIndex"/> is the first half of a <c>::</c>
    /// being typed, rather than a ternary's divider.
    ///
    /// Two things decide it, and both are needed. ADJACENCY: a qualifier is written hard against
    /// its name (<c>util::</c>), so a colon with space before it is not one being typed — this is
    /// what keeps the ordinary <c>a ? b : c</c> out. And the absence of a <c>?</c> before the
    /// statement boundary, which catches the unspaced <c>a?b:c</c> that adjacency alone would
    /// mistake for a qualifier.
    ///
    /// A case label is the third reading of a lone colon and is answered before this one.
    /// </summary>
    private static bool IsIncompleteScopeResolution(ImmutableArray<Token> tokens, int colonIndex)
    {
        int nameIndex = PreviousSignificant(tokens, colonIndex);
        if ( nameIndex < 0 || tokens[nameIndex].Kind != TokenKind.Identifier )
        {
            return false;
        }

        if ( tokens[nameIndex].End != tokens[colonIndex].Start )
        {
            return false;
        }

        // Nearest wins, exactly as IsCaseLabelColon does it: a '?' in the same statement means the
        // colon divides a ternary however tightly it is spaced.
        for ( int index = colonIndex - 1; index >= 0; index-- )
        {
            switch ( tokens[index].Kind )
            {
                case TokenKind.QuestionMark:
                    return false;

                case TokenKind.Semicolon:
                case TokenKind.OpenBrace:
                case TokenKind.CloseBrace:
                case TokenKind.Colon:
                    return true;
            }
        }

        return true;
    }

    /// <summary>
    /// True when the string at <paramref name="literalIndex"/> is the FIRST argument of a
    /// <c>#precache(</c> — the asset-type slot. The second argument is the asset's own name and
    /// has no closed vocabulary to offer.
    /// </summary>
    private static bool IsPrecacheAssetTypeLiteral(ImmutableArray<Token> tokens, int literalIndex)
    {
        int openParen = PreviousSignificant(tokens, literalIndex);
        if ( openParen < 0 || tokens[openParen].Kind != TokenKind.OpenParen )
        {
            return false;
        }

        int directive = PreviousSignificant(tokens, openParen);
        return directive >= 0 && tokens[directive].Kind == TokenKind.PrecacheDirective;
    }

    /// <summary>
    /// The path text already typed between the directive and the cursor, normalized to
    /// backslashes. Read from the raw source rather than the tokens because a partial path is a
    /// run of identifiers and separators that never lexes as one thing.
    /// </summary>
    private static string TypedPathBefore(ParseResult result, int directiveEnd, int offset)
    {
        int start = Math.Clamp(directiveEnd, 0, result.Text.Length);
        int end = Math.Clamp(offset, start, result.Text.Length);

        return result.Text.Text[start..end].Trim().Replace('/', '\\');
    }

    /// <summary>
    /// The class of an arrow call's receiver, or null when it is not knowable.
    ///
    /// Only <c>[[self]]-&gt;</c> inside a class body is: the tokens before the arrow are
    /// <c>[[ self ]]</c>, and the class comes from the caret's position. Any other receiver is a
    /// local whose class would need type inference to pin down.
    /// </summary>
    private static string? ArrowReceiverClass(
        ParseResult result, ImmutableArray<Token> tokens, int arrowIndex, Position position)
    {
        // Walk back over the deref rather than re-parsing, since this runs mid-keystroke where the
        // tree may not contain the call at all. `]]` lexes as TWO CloseBracket tokens, not one, so
        // this skips however many are there instead of assuming a single closing token.
        int receiver = PreviousSignificant(tokens, arrowIndex);
        while ( receiver >= 0 && tokens[receiver].Kind == TokenKind.CloseBracket )
        {
            receiver = PreviousSignificant(tokens, receiver);
        }

        if ( receiver < 0 || tokens[receiver].Kind != TokenKind.Identifier )
        {
            return null;
        }

        if ( !TokenFacts.IsSelfName(tokens[receiver].GetText(result.Text)) )
        {
            return null;
        }

        foreach ( ClassSymbol classSymbol in result.Extraction.Classes )
        {
            if ( classSymbol.FullRange.Contains(position) )
            {
                return classSymbol.KeyName;
            }
        }

        return null;
    }

    /// <summary>The class whose body contains this offset, over the file's own handful of classes.</summary>
    private static string? EnclosingClassAt(ParseResult result, int offset)
    {
        Position position = result.Text.GetPosition(offset);

        foreach ( ClassSymbol classSymbol in result.Extraction.Classes )
        {
            if ( classSymbol.FullRange.Contains(position) )
            {
                return classSymbol.KeyName;
            }
        }

        return null;
    }

    /// <summary>
    /// True when a '#' sits immediately before the word being typed, i.e. the cursor is part-way
    /// through a directive.
    ///
    /// This reads the raw text rather than the token stream on purpose. A half-typed "#p" is not
    /// a known directive, so the lexer emits it as a single Error token — and a bare "#" with
    /// nothing after it emits Hash. Walking the characters back over the partial word handles
    /// both without depending on which of those two shapes the lexer chose.
    /// </summary>
    private static bool IsAfterDirectiveHash(SourceText text, int offset)
    {
        int cursor = Math.Min(offset, text.Length);
        while ( cursor > 0 && IsWordChar(text.Text[cursor - 1]) )
        {
            cursor--;
        }

        return cursor > 0 && text.Text[cursor - 1] == '#';
    }

    private static bool IsWordChar(char c)
    {
        return char.IsLetterOrDigit(c) || c == '_';
    }

    /// <summary>
    /// The script path being typed at the cursor in ordinary code — <c>maps\mp\_ut</c> — or "" when
    /// the cursor is not in one.
    /// </summary>
    /// <remarks>
    /// A SEPARATOR is required, not merely word characters, and that is the whole of the
    /// disambiguation: every bare identifier in a function body would otherwise look like the first
    /// segment of a path, and the suggestion list would become script paths everywhere. Once a '\'
    /// has been typed the intent is unambiguous — nothing else in GSC expression syntax contains
    /// one, string literals having already been handled by the caller.
    ///
    /// '\' ONLY, unlike <see cref="TypedPathBefore"/>. That argument holds for the backslash and
    /// not for '/', which is division and the start of a comment: accepting it classified
    /// `hp = maxhealth/2` as a path, and since '/' is a completion trigger character the empty
    /// result popped over the cursor and — returned with IsIncomplete false — was then filtered
    /// client-side for the rest of the identifier. A directive's path has no such collision, so
    /// the normalisation stays there, where a user genuinely does type either separator.
    /// </remarks>
    private static string InlinePathBefore(SourceText text, int offset)
    {
        int cursor = Math.Clamp(offset, 0, text.Length);
        int start = cursor;
        bool sawSeparator = false;

        while ( start > 0 )
        {
            char c = text.Text[start - 1];
            if ( c == '\\' )
            {
                sawSeparator = true;
            }
            else if ( !IsWordChar(c) )
            {
                break;
            }

            start--;
        }

        return sawSeparator ? text.Text[start..cursor] : "";
    }

    /// <summary>
    /// The owner an `owner.` completion is being asked on, lowercased, or "" when it cannot be
    /// determined. Globals like `self`/`level` lex as plain identifiers, so a bare identifier
    /// before the dot IS the owner; anything else (an index or call result, e.g. `players[q].`)
    /// has no name to scope by, and an unknown owner deliberately widens rather than narrows —
    /// offering everything beats offering nothing.
    /// </summary>
    private static string OwnerBefore(ParseResult result, ImmutableArray<Token> tokens, int dotIndex)
    {
        int ownerIndex = PreviousSignificant(tokens, dotIndex);
        if ( ownerIndex < 0 || tokens[ownerIndex].Kind != TokenKind.Identifier )
        {
            return "";
        }

        return tokens[ownerIndex].GetText(result.Text).ToString().ToLowerInvariant();
    }

    /// <summary>
    /// Whether a call written here would be a whole statement.
    ///
    /// Scans back to the nearest statement boundary and accepts only the tokens that can precede
    /// a call in statement position: the object it is called on, and the qualifiers reaching the
    /// name. `self foobar` and `self thread ns::foobar` qualify; `x = foobar` does not, because
    /// the '=' is not in the allowed set.
    ///
    /// A whitelist rather than a blacklist, deliberately. Getting this wrong writes a semicolon
    /// into the middle of an expression, so anything unrecognised has to mean "not a statement".
    /// </summary>
    private static bool IsStatementPosition(ImmutableArray<Token> tokens, int currentIndex, int offset)
    {
        int scan = (currentIndex >= 0 ? currentIndex : FirstAtOrAfter(tokens, offset)) - 1;
        bool seenAssignment = false;

        while ( scan >= 0 )
        {
            TokenKind kind = tokens[scan].Kind;

            if ( tokens[scan].IsTrivia )
            {
                scan--;
                continue;
            }

            // A statement boundary: everything from here to the cursor was allowed, so this is
            // the start of a statement. Colon covers `case x:` and `default:`.
            if ( kind is TokenKind.Semicolon or TokenKind.OpenBrace or TokenKind.CloseBrace or TokenKind.Colon )
            {
                return true;
            }

            // An unbraced control-flow body is a statement too, and `else`/`do` take one
            // directly. Rejecting these is why the semicolon went missing on exactly the lines
            // whose body has no braces.
            if ( kind is TokenKind.Else or TokenKind.Do )
            {
                return true;
            }

            if ( kind == TokenKind.CloseParen )
            {
                // A ')' either closes a control-flow header, so a body follows, or closes a call,
                // in which case this is an expression and the answer is no.
                return ClosesControlFlowHeader(tokens, scan);
            }

            // `x = foo()` and `self.count += tally()` are statements too — the call completes
            // one. Allowed once: a second assignment operator on the way back would mean the
            // first was part of something else, and `a = b = foo()` is not worth the risk.
            if ( TokenFacts.IsAssignmentOperator(kind) )
            {
                if ( seenAssignment )
                {
                    return false;
                }

                seenAssignment = true;
                scan--;
                continue;
            }

            if ( kind == TokenKind.CloseBracket )
            {
                // Skip the whole index — `things[0]` and `things[ get_key() ]` are both just an
                // object being called on, and whatever is inside says nothing about that.
                scan = MatchingOpenBracket(tokens, scan) - 1;
                continue;
            }

            // The object and the path to the name — `self`, `level`, `ns::`, `.field`, `thread`.
            if ( kind is TokenKind.Identifier or TokenKind.Dot or TokenKind.ScopeResolution or TokenKind.Thread )
            {
                scan--;
                continue;
            }

            return false;
        }

        // Nothing but allowed tokens all the way back — the file opens here.
        return true;
    }

    /// <summary>
    /// The index of the <c>[</c> matching the <c>]</c> at <paramref name="closeIndex"/>, or -1
    /// when the brackets are unbalanced (mid-edit, which is most of the time here).
    /// </summary>
    private static int MatchingOpenBracket(ImmutableArray<Token> tokens, int closeIndex)
    {
        int depth = 0;

        for ( int scan = closeIndex; scan >= 0; scan-- )
        {
            if ( tokens[scan].Kind == TokenKind.CloseBracket )
            {
                depth++;
            }
            else if ( tokens[scan].Kind == TokenKind.OpenBracket && --depth == 0 )
            {
                return scan;
            }
        }

        return -1;
    }

    /// <summary>
    /// Whether the <c>)</c> at <paramref name="closeIndex"/> ends an `if`/`while`/`for`/`foreach`
    /// header — so a statement follows it — rather than ending a call.
    ///
    /// Walks back over balanced parentheses to the matching <c>(</c> and looks at the word before
    /// it, which is the only way to tell `if ( ready )` from `get_ready()`.
    /// </summary>
    private static bool ClosesControlFlowHeader(ImmutableArray<Token> tokens, int closeIndex)
    {
        int depth = 0;

        for ( int scan = closeIndex; scan >= 0; scan-- )
        {
            TokenKind kind = tokens[scan].Kind;

            if ( kind == TokenKind.CloseParen )
            {
                depth++;
                continue;
            }

            if ( kind != TokenKind.OpenParen )
            {
                continue;
            }

            depth--;
            if ( depth > 0 )
            {
                continue;
            }

            int keyword = PreviousSignificant(tokens, scan);
            return keyword >= 0
                && tokens[keyword].Kind is TokenKind.If or TokenKind.While
                    or TokenKind.For or TokenKind.Foreach;
        }

        return false;
    }

    // --- Token helpers ---

    private static bool TryPrecacheContext(ImmutableArray<Token> tokens, int triggerIndex)
    {
        // Cursor right after `#precache (` or `#precache ( ,` -> asset-type position.
        if ( triggerIndex < 0 )
        {
            return false;
        }

        TokenKind kind = tokens[triggerIndex].Kind;
        if ( kind != TokenKind.OpenParen && kind != TokenKind.Comma )
        {
            return false;
        }

        int before = PreviousSignificant(tokens, triggerIndex);
        return before >= 0 && tokens[before].Kind == TokenKind.PrecacheDirective && kind == TokenKind.OpenParen;
    }

    /// <summary>Index of the string/istring/hash literal the cursor is typing inside, else -1.</summary>
    private static int FindLiteralAtOffset(ImmutableArray<Token> tokens, int offset)
    {
        for ( int index = 0; index < tokens.Length; index++ )
        {
            Token token = tokens[index];
            bool isLiteral = token.Kind == TokenKind.String
                || token.Kind == TokenKind.LocalizedString
                || token.Kind == TokenKind.HashString;

            // Strictly past the opening quote, up to and including the end (handles a still-open
            // string that runs to the end of the line).
            if ( isLiteral && offset > token.Start && offset <= token.End )
            {
                return index;
            }
        }

        return -1;
    }

    private static SymbolKind LiteralKindOf(TokenKind kind)
    {
        switch ( kind )
        {
            case TokenKind.LocalizedString:
                return SymbolKind.LocalizedString;
            case TokenKind.HashString:
                return SymbolKind.HashString;
            default:
                return SymbolKind.StringLiteral;
        }
    }

    /// <summary>Index of the identifier token the cursor is inside or just after, else -1.</summary>
    private static int FindCurrentWordIndex(ImmutableArray<Token> tokens, int offset)
    {
        for ( int index = 0; index < tokens.Length; index++ )
        {
            Token token = tokens[index];
            bool isWord = token.Kind == TokenKind.Identifier || TokenFacts.IsKeyword(token.Kind);
            if ( isWord && offset > token.Start && offset <= token.End )
            {
                return index;
            }
        }

        return -1;
    }

    private static int FirstAtOrAfter(ImmutableArray<Token> tokens, int offset)
    {
        for ( int index = 0; index < tokens.Length; index++ )
        {
            if ( tokens[index].Start >= offset )
            {
                return index;
            }
        }

        return tokens.Length;
    }

    private static int PreviousSignificant(ImmutableArray<Token> tokens, int fromIndex)
    {
        int index = fromIndex - 1;
        while ( index >= 0 && tokens[index].IsTrivia )
        {
            index--;
        }

        return index;
    }

    private static bool IsInsideFunctionBody(ParseResult result, Position position)
    {
        return EnclosingFunction(result, position) is not null;
    }

    /// <summary>
    /// The function or class METHOD whose body contains this position.
    ///
    /// <c>Extraction.Functions</c> holds top-level functions only — a method lives on its class — so
    /// asking it alone meant the caret inside any method body was not "inside a function". That
    /// decides which keyword set completion offers and whether it offers functions, variables and
    /// builtins at all, so every class method body in the workspace was being completed as though it
    /// were top-level: `#using`, `class` and `function`, and nothing that can actually be written
    /// there.
    /// </summary>
    private static FunctionSymbol? EnclosingFunction(ParseResult result, Position position)
    {
        foreach ( FunctionSymbol function in result.Extraction.Functions )
        {
            if ( function.FullRange.Contains(position) )
            {
                return function;
            }
        }

        foreach ( ClassSymbol classSymbol in result.Extraction.Classes )
        {
            if ( !classSymbol.FullRange.Contains(position) )
            {
                continue;
            }

            foreach ( FunctionSymbol method in classSymbol.Methods )
            {
                if ( method.FullRange.Contains(position) )
                {
                    return method;
                }
            }

            // A constructor or destructor body is a function body too, for every purpose this
            // answers — which is why they are carried on the class at all.
            if ( classSymbol.Constructor is not null && classSymbol.Constructor.FullRange.Contains(position) )
            {
                return classSymbol.Constructor;
            }

            if ( classSymbol.Destructor is not null && classSymbol.Destructor.FullRange.Contains(position) )
            {
                return classSymbol.Destructor;
            }
        }

        return null;
    }

    /// <summary>
    /// Whether the parameter pack is in scope here — the dialect binds one AND the enclosing
    /// function declares <c>...</c>.
    /// </summary>
    /// <remarks>
    /// Both halves matter. Offering <c>vararg</c> from the plain keyword list would suggest it in
    /// every function on the dialect, and in a function without <c>...</c> nothing binds it, so
    /// accepting the suggestion earns a 5024. A completion list that leads to a diagnostic is worse
    /// than one entry short.
    /// </remarks>
    private static bool IsVarargInScope(ParseResult result, Position position, GameProfile game)
    {
        return game.HasVarargBinding
            && EnclosingFunction(result, position) is FunctionSymbol function
            && function.HasVarargs;
    }
}
