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
public sealed class CompletionEngine
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
        bool insideFunction = IsInsideFunctionBody(result, position);

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
            // A '#' inside a function body begins a HASH STRING, the one thing it can mean there.
            // The quotes are added because the cursor is not inside a string yet — only the '#'
            // has been typed.
            return includeLiterals
                ? LiteralCompletions(result, contextId, SymbolKind.HashString, quoted: true)
                : [];
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

        return StatementScopeCompletions(
            result,
            contextId,
            offset,
            insideFunction,
            IsVarargInScope(result, position, game),
            CallSnippet(tokens, currentIndex, offset, callPunctuation),
            game,
            parameterHints);
    }

    // --- Contexts ---

    /// <summary>
    /// The precache asset types this file may actually use. The <c>client_*</c> family belongs to
    /// the client world, so a <c>.gsc</c> is never offered one it could not honour.
    /// </summary>
    /// <param name="language">The asking file's language, which decides the client half.</param>
    /// <param name="quoted">
    /// Whether to insert the quotes too. False when the cursor already sits inside a string —
    /// otherwise accepting a suggestion produces <c>""model""</c>.
    /// </param>
    private static ImmutableArray<CompletionEntry> AssetTypeCompletions(ScriptLanguage language, bool quoted = true)
    {
        ImmutableArray<CompletionEntry>.Builder entries = ImmutableArray.CreateBuilder<CompletionEntry>();
        foreach ( string name in PrecacheAssetTypes.NamesFor(language).OrderBy(static n => n, StringComparer.Ordinal) )
        {
            entries.Add(new CompletionEntry(
                name, CompletionKind.AssetType, "precache asset type", quoted ? "\"" + name + "\"" : name));
        }

        return entries.ToImmutable();
    }

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
    /// What may follow the <c>function</c> keyword: the declaration modifiers, and the script
    /// functions already visible. No builtins, macros or globals — none of them can be declared.
    /// </summary>
    private ImmutableArray<CompletionEntry> DeclarationNameCompletions(ParseResult result, string contextId, GameProfile game)
    {
        ImmutableArray<CompletionEntry>.Builder entries = ImmutableArray.CreateBuilder<CompletionEntry>();

        foreach ( string modifier in (string[])["private", "autoexec"] )
        {
            if ( GscKeywords.IsAvailable(modifier, game) )
            {
                entries.Add(new CompletionEntry(
                    modifier, CompletionKind.Keyword, "", "", KeywordDocs.Find(modifier) ?? ""));
            }
        }

        LanguageStore store = _database.StoreFor(result.Language);
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);

        // The file's own declarations come from the live extraction as well as the store, so a
        // function written a moment ago is offered before the record is reindexed.
        foreach ( FunctionSymbol function in result.Extraction.Functions )
        {
            if ( function.SourceFile.Length == 0 && seen.Add(function.Name) )
            {
                entries.Add(new CompletionEntry(function.Name, CompletionKind.Function, "function"));
            }
        }

        ImmutableArray<string> declaredNamespaces = DatabaseQueries.DeclaredNamespaces(result);

        foreach ( string ns in declaredNamespaces )
        {
            foreach ( FunctionSymbol function in DatabaseQueries.FunctionsInNamespace(
                store, contextId, result.FilePath, ns, declaredNamespaces) )
            {
                if ( seen.Add(function.Name) )
                {
                    entries.Add(new CompletionEntry(function.Name, CompletionKind.Function, "function"));
                }
            }
        }

        return entries.ToImmutable();
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

    private ImmutableArray<CompletionEntry> TryPathContext(ParseResult result, string contextId, ImmutableArray<Token> tokens, int currentIndex, int offset)
    {
        // Detect a #using/#insert/#include earlier on the same line than the cursor.
        int probe = currentIndex >= 0 ? currentIndex : FirstAtOrAfter(tokens, offset);
        int scan = probe - 1;
        while ( scan >= 0 )
        {
            TokenKind kind = tokens[scan].Kind;
            if ( kind == TokenKind.Newline )
            {
                break;
            }

            // #include is the merge dialects' import and takes the same script path as #using —
            // it was simply missing here, so the whole Infinity Ward line got no path completion on
            // the one directive it actually writes. It is not an #insert: a header is a Treyarch
            // thing, and those dialects have none.
            if ( kind == TokenKind.UsingDirective || kind == TokenKind.InsertDirective
                || kind == TokenKind.IncludeDirective )
            {
                string typed = TypedPathBefore(result, tokens[scan].End, offset);
                return PathSegmentCompletions(
                    result, contextId, isInsert: kind == TokenKind.InsertDirective, typed);
            }

            scan--;
        }

        return default;
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
    /// Path completion, ONE SEGMENT AT A TIME, like a folder picker.
    ///
    /// Offering whole relative paths did not work: the client's word pattern excludes '\', so at
    /// `scripts\mp\` the editor's current word is empty and it cannot filter `scripts\mp\_arena`
    /// against anything the user typed — the list stayed unfiltered and highlighted whatever came
    /// first. Offering only the next segment means the word being matched IS the segment, so the
    /// editor filters it correctly with no special handling.
    ///
    /// Folders insert a trailing '\' and reopen the list, so a path is walked down rather than
    /// typed out.
    /// </summary>
    /// <param name="isInsert">
    /// Whether this is <c>#insert</c>, which takes a header. Headers live in the shared GSH store
    /// rather than either language store, so serving both from one store offered <c>#insert</c>
    /// the <c>.gsc</c> files it can never include.
    /// </param>
    /// <param name="typed">The path already typed, e.g. <c>scripts\mp\_ar</c>.</param>
    private ImmutableArray<CompletionEntry> PathSegmentCompletions(
        ParseResult result, string contextId, bool isInsert, string typed)
    {
        // Everything up to the last separator is the folder being listed. What follows is the
        // partial segment the editor filters on, so it must NOT narrow the candidates here.
        int lastSeparator = typed.LastIndexOf('\\');
        string directory = lastSeparator >= 0 ? typed[..(lastSeparator + 1)] : "";

        // Segment -> whether it is a folder (has more path below it).
        Dictionary<string, bool> segments = new(StringComparer.OrdinalIgnoreCase);

        foreach ( ScriptRecord record in PathCandidates(result, isInsert) )
        {
            if ( record.RelativePath.Length == 0 || !ScriptDatabase.CanSee(contextId, record.ContextId) )
            {
                continue;
            }

            // #insert writes the extension, #using does not — an asymmetry of the language, not
            // of this code, and unanimous across the stock scripts: all 2,137 #inserts end in
            // .gsh and all 7,738 #usings are bare. Keeping the extension for #insert also makes
            // the segmenting below fall out for free, since the leaf is simply "shared.gsh".
            string relative = record.RelativePath.Replace('/', '\\');
            string path = isInsert
                ? relative
                : System.IO.Path.ChangeExtension(relative, null) ?? relative;

            if ( !path.StartsWith(directory, StringComparison.OrdinalIgnoreCase) )
            {
                continue;
            }

            string remainder = path[directory.Length..];
            if ( remainder.Length == 0 )
            {
                continue;
            }

            int separator = remainder.IndexOf('\\');
            bool isFolder = separator >= 0;
            string segment = isFolder ? remainder[..separator] : remainder;

            // A name that is both a folder and a file lists as a folder, which is the one with
            // more below it to reach.
            segments[segment] = segments.TryGetValue(segment, out bool existing) ? existing || isFolder : isFolder;
        }

        ImmutableArray<CompletionEntry>.Builder entries = ImmutableArray.CreateBuilder<CompletionEntry>();
        foreach ( KeyValuePair<string, bool> segment in segments.OrderBy(static s => s.Key, StringComparer.OrdinalIgnoreCase) )
        {
            bool isFolder = segment.Value;
            entries.Add(new CompletionEntry(
                segment.Key,
                isFolder ? CompletionKind.PathSegment : CompletionKind.PathFile,
                isFolder ? "folder" : (isInsert ? "header" : "script"),
                isFolder ? segment.Key + "\\" : segment.Key,
                RetriggerCompletion: isFolder));
        }

        return entries.ToImmutable();
    }

    /// <summary>
    /// The records a path directive may name: headers for <c>#insert</c>, this file's own
    /// language for <c>#using</c>. A <c>.gsc</c> never includes a <c>.csc</c> or vice versa.
    /// </summary>
    private IEnumerable<ScriptRecord> PathCandidates(ParseResult result, bool isInsert)
    {
        if ( isInsert )
        {
            return _database.AllGshRecords;
        }

        return _database.StoreFor(result.Language).AllRecords;
    }

    /// <param name="quoted">
    /// Whether to insert the surrounding quotes. True when only the sigil has been typed — at
    /// `notify(#` the cursor is not inside a string yet, so the entry has to supply them.
    /// </param>
    private ImmutableArray<CompletionEntry> LiteralCompletions(
        ParseResult result, string contextId, SymbolKind literalKind, bool quoted = false)
    {
        LanguageStore store = _database.StoreFor(result.Language);

        // String literals are content-exact; hash/istring names are already lowercase-canonical,
        // so an ordinal set dedups every kind correctly.
        HashSet<string> seen = new(StringComparer.Ordinal);
        ImmutableArray<CompletionEntry>.Builder entries = ImmutableArray.CreateBuilder<CompletionEntry>();

        // The current file's live literals first, then every visible record's. Message fragments
        // are already excluded upstream: a string spliced into a `+` chain is recorded as
        // ConcatenatedLiteral, and this only accepts ReferenceKind.Literal.
        CollectLiterals(result.Extraction.References, literalKind, seen, entries, quoted);
        foreach ( ScriptRecord record in store.AllRecords )
        {
            if ( ScriptDatabase.CanSee(contextId, record.ContextId) )
            {
                CollectLiterals(record.References, literalKind, seen, entries, quoted);
            }
        }

        return entries.ToImmutable();
    }

    private static void CollectLiterals(
        ImmutableArray<ReferenceEntry> references,
        SymbolKind literalKind,
        HashSet<string> seen,
        ImmutableArray<CompletionEntry>.Builder entries,
        bool quoted)
    {
        string detail = LiteralDetail(literalKind);
        foreach ( ReferenceEntry entry in references )
        {
            if ( entry.Kind != ReferenceKind.Literal || entry.Key.Kind != literalKind )
            {
                continue;
            }

            if ( !IsNameLike(entry.Key.Name) )
            {
                continue;
            }

            if ( seen.Add(entry.Key.Name) )
            {
                entries.Add(new CompletionEntry(
                    entry.Key.Name,
                    CompletionKind.Literal,
                    detail,
                    quoted ? "\"" + entry.Key.Name + "\"" : ""));
            }
        }
    }

    /// <summary>The shortest run of letters and digits a literal must have to read as a name.</summary>
    private const int MinimumNameCharacters = 3;

    /// <summary>
    /// Whether a literal reads as a NAME rather than as text or data, and so is worth offering.
    ///
    /// Three conditions, each measured against the stock scripts rather than guessed. Of the 2,094
    /// literals in unambiguous name positions there (notify, endon, flag and clientfield calls,
    /// precache, tag and weapon lookups), all 2,094 satisfy the first two and 2,093 satisfy all
    /// three:
    ///
    /// 1. Identifier-shaped characters only. Those 2,094 contain nothing but letters, digits and
    ///    underscores; the sole other characters anywhere among them are 163 spaces and 16 colons,
    ///    which is exactly the prose being excluded. Path punctuation is allowed alongside, for
    ///    asset paths and versioned model names.
    /// 2. At least one letter, which removes the numbers and lone punctuation that were being
    ///    offered inside a string — "0.25", "-1", ".", "/". Not one real name lacks a letter.
    /// 3. At least <see cref="MinimumNameCharacters"/> letters-and-digits, which removes stray
    ///    one- and two-character fragments.
    ///
    /// Counting letters AND DIGITS in the third rule, rather than letters alone, is what keeps
    /// weapon names: "hk416" has two letters, "m32" has one, and both are real. Requiring three
    /// letters would have thrown them away. The single casualty is "tp", used once.
    ///
    /// These are blunter than the structural rule that drops <c>+</c> operands, and deliberately
    /// so — they also lose the handful of stock events written as several words ("abort forfeit",
    /// "missile fired"). A clean list was judged worth more than those.
    /// </summary>
    private static bool IsNameLike(string literal)
    {
        bool hasLetter = false;
        int nameCharacters = 0;

        foreach ( char c in literal )
        {
            if ( char.IsLetter(c) )
            {
                hasLetter = true;
                nameCharacters++;
                continue;
            }

            if ( char.IsDigit(c) )
            {
                nameCharacters++;
                continue;
            }

            // Underscore for names; the rest for asset paths and versioned model names. These do
            // not count towards the length, so "_a" and "a.b" are still too short.
            if ( c is '_' or '-' or '.' or '\\' or '/' )
            {
                continue;
            }

            return false;
        }

        return hasLetter && nameCharacters >= MinimumNameCharacters;
    }

    private static string LiteralDetail(SymbolKind literalKind)
    {
        switch ( literalKind )
        {
            case SymbolKind.LocalizedString:
                return "localized string";
            case SymbolKind.HashString:
                return "hash string";
            default:
                return "string";
        }
    }

    /// <summary>
    /// What may follow <c>name::</c>. A qualifier can name a namespace, a class, or — in BO3's
    /// phalanx.gsc and throttle_shared.gsc — BOTH, so this offers the union rather than choosing.
    /// Both forms are legal to write there, and across the stock scripts no namespace function and
    /// same-named class method ever collide, so the union is unambiguous in practice.
    /// </summary>
    private ImmutableArray<CompletionEntry> NamespaceFunctionCompletions(
        ParseResult result, string contextId, string ns, string callSuffix, bool parameterHints)
    {
        LanguageStore store = _database.StoreFor(result.Language);
        ImmutableArray<CompletionEntry>.Builder entries = ImmutableArray.CreateBuilder<CompletionEntry>();
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);

        foreach ( FunctionSymbol function in DatabaseQueries.FunctionsInNamespace(store, contextId, result.FilePath, ns, DatabaseQueries.DeclaredNamespaces(result)) )
        {
            if ( seen.Add(function.KeyName) )
            {
                entries.Add(FunctionEntry(function, callSuffix, parameterHints));
            }
        }

        // Typing `cScene::` used to return nothing at all: the qualifier is not a namespace, so the
        // namespace query found none of its 59 methods.
        foreach ( ClassMethod method in MethodResolution.MethodsOf(store, contextId, ns, result.Extraction.Classes) )
        {
            if ( seen.Add(method.Method.KeyName) )
            {
                entries.Add(MethodEntry(method, callSuffix, parameterHints));
            }
        }

        return entries.ToImmutable();
    }

    /// <summary>
    /// What may follow <c>[[receiver]]-&gt;</c>.
    ///
    /// <c>[[self]]-&gt;</c> inside a class offers that class's chain. Every other receiver offers
    /// every visible class's methods, labelled with the class that declares each — the receiver's
    /// type is not known, and 155 of the 159 arrow calls in the stock scripts are that shape, so the
    /// wide list is the one that carries the feature.
    /// </summary>
    private ImmutableArray<CompletionEntry> ArrowMethodCompletions(
        ParseResult result, string contextId, string? receiverClass, string callSuffix, bool parameterHints)
    {
        LanguageStore store = _database.StoreFor(result.Language);
        ImmutableArray<CompletionEntry>.Builder entries = ImmutableArray.CreateBuilder<CompletionEntry>();

        if ( receiverClass is not null )
        {
            foreach ( ClassMethod method in MethodResolution.MethodsOf(
                store, contextId, receiverClass, result.Extraction.Classes) )
            {
                entries.Add(MethodEntry(method, callSuffix, parameterHints));
            }

            return entries.ToImmutable();
        }

        // The store's classes plus this file's own, which may not be indexed yet.
        HashSet<string> classNames = new(store.Classes.AllClassNames(), StringComparer.Ordinal);
        foreach ( ClassSymbol classSymbol in result.Extraction.Classes )
        {
            classNames.Add(classSymbol.KeyName);
        }

        HashSet<string> seen = new(StringComparer.Ordinal);
        foreach ( string className in classNames )
        {
            foreach ( ClassMethod method in MethodResolution.MethodsOf(
                store, contextId, className, result.Extraction.Classes) )
            {
                // Keyed by class AND name: two classes may declare the same method, and both are
                // genuine candidates when the receiver could be either.
                if ( seen.Add(className + "::" + method.Method.KeyName) )
                {
                    entries.Add(MethodEntry(method, callSuffix, parameterHints));
                }
            }
        }

        return entries.ToImmutable();
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

    /// <summary>A method entry, labelled with the class that declares it rather than a namespace.</summary>
    private static CompletionEntry MethodEntry(ClassMethod method, string callSuffix, bool parameterHints)
    {
        return FunctionEntry(method.Method, callSuffix, parameterHints) with { Detail = method.OwnerClass.Name };
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
    /// </remarks>
    private static string InlinePathBefore(SourceText text, int offset)
    {
        int cursor = Math.Clamp(offset, 0, text.Length);
        int start = cursor;
        bool sawSeparator = false;

        while ( start > 0 )
        {
            char c = text.Text[start - 1];
            if ( c == '\\' || c == '/' )
            {
                sawSeparator = true;
            }
            else if ( !IsWordChar(c) )
            {
                break;
            }

            start--;
        }

        return sawSeparator ? text.Text[start..cursor].Replace('/', '\\') : "";
    }

    /// <summary>
    /// What accepting a directive actually inserts: its full form, with the cursor placed where
    /// the argument goes. Inserting the bare word left a directive that does not parse until the
    /// parentheses and semicolon are typed by hand — and a half-written directive reddens the
    /// lines under it, so the editor looks broken while you finish the line.
    ///
    /// The '#' is already in the buffer (that is what put us in this context), so none of these
    /// repeat it. Conditionals and #endif take no punctuation and are inserted plain.
    /// </summary>
    private static string DirectiveSnippet(string keyword, string withoutHash)
    {
        switch ( keyword )
        {
            case "#precache":
                // Both arguments are quoted, and the first is a closed vocabulary that
                // completion offers as soon as the cursor lands inside the quotes.
                return "precache( \"$1\", \"$2\" );$0";
            case "#using_animtree":
                return "using_animtree( \"$1\" );$0";
            case "#using":
            case "#insert":
                // Pre-fill the root: every one of the 9,875 path directives in the stock scripts
                // starts at `scripts\`, so typing it is pure ceremony. The cursor lands after the
                // separator, where completion reopens on that folder's contents.
                //
                return withoutHash + " " + SnippetLiteral(@"scripts\") + "$1;$0";
            case "#namespace":
                return "namespace $1;$0";
            case "#define":
                // No semicolon: a #define runs to the end of the line.
                return "define $1 $0";
            default:
                return withoutHash;
        }
    }

    /// <summary>
    /// Escapes text that must appear VERBATIM inside a snippet.
    ///
    /// Snippet syntax gives '\', '$' and '}' meaning, and '\' escapes whatever follows it — so an
    /// unescaped path separator swallowed the tab stop after it and put `#using scripts$1;` in the
    /// buffer as literal text. GSC paths are full of separators, so this has to be a function
    /// rather than something remembered at each call site.
    /// </summary>
    private static string SnippetLiteral(string text)
    {
        return text
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("$", "\\$", StringComparison.Ordinal)
            .Replace("}", "\\}", StringComparison.Ordinal);
    }

    /// <summary>
    /// Whether the cursor lands somewhere this engine can actually suggest for, so accepting the
    /// directive should reopen the suggestion list.
    ///
    /// Only where a vocabulary exists. Reopening on <c>#define</c> or <c>#namespace</c>, whose
    /// argument is a name the user is inventing, would pop a list over what they are typing.
    /// </summary>
    private static bool DirectiveArgumentHasVocabulary(string keyword)
    {
        switch ( keyword )
        {
            // The asset type: a closed list from PrecacheAssetTypes.
            case "#precache":
            // A script path: offered from the indexed files.
            case "#using":
            case "#insert":
                return true;
            default:
                return false;
        }
    }

    /// <summary>
    /// The directives, offered with the leading '#' stripped from what the editor filters and
    /// inserts. The client's word pattern excludes '#', so after typing "#p" the current word is
    /// "p": a "#precache" label would be filtered out while "private" survived — the reported
    /// bug. Filtering on "precache" matches, and inserting "precache" onto the '#' already in the
    /// buffer avoids producing "##precache". The label keeps its '#' so the list stays readable.
    /// </summary>
    private static ImmutableArray<CompletionEntry> DirectiveCompletions(GameProfile game)
    {
        ImmutableArray<CompletionEntry>.Builder entries = ImmutableArray.CreateBuilder<CompletionEntry>();

        foreach ( string keyword in GscKeywords.TopLevelKeywords )
        {
            if ( !keyword.StartsWith('#') || !GscKeywords.IsAvailable(keyword, game) )
            {
                continue;
            }

            string withoutHash = keyword[1..];
            entries.Add(new CompletionEntry(
                keyword,
                CompletionKind.Keyword,
                "directive",
                DirectiveSnippet(keyword, withoutHash),
                KeywordDocs.Find(keyword) ?? "",
                withoutHash,
                RetriggerCompletion: DirectiveArgumentHasVocabulary(keyword)));
        }

        return entries.ToImmutable();
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

    private ImmutableArray<CompletionEntry> FieldCompletions(
        ParseResult result,
        string contextId,
        string ownerName,
        FieldScope fieldScope)
    {
        ImmutableArray<CompletionEntry>.Builder entries = ImmutableArray.CreateBuilder<CompletionEntry>();
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);

        // Scope only when asked AND the owner is known; otherwise every owner contributes.
        bool scopeToOwner = fieldScope == FieldScope.Owner && ownerName.Length > 0;

        // The live file first, so unsaved edits are offered immediately, then every visible
        // record — a field assigned on `level` in one file is reachable from all of them.
        CollectAssignedFields(result.Extraction.Functions, scopeToOwner, ownerName, seen, entries);

        LanguageStore fieldStore = _database.StoreFor(result.Language);
        foreach ( ScriptRecord record in fieldStore.AllRecords )
        {
            if ( ScriptDatabase.CanSee(contextId, record.ContextId) )
            {
                CollectAssignedFields(record.Functions, scopeToOwner, ownerName, seen, entries);
            }
        }

        // The .size pseudo-member.
        if ( seen.Add("size") )
        {
            entries.Add(new CompletionEntry("size", CompletionKind.Field, "int (read-only)"));
        }

        // Engine object fields. The owner's entity kind isn't known at this point, so every
        // documented field name is offered with its type when the declaring kinds agree.
        foreach ( string fieldName in _objectFields.FieldNames() )
        {
            if ( !seen.Add(fieldName) )
            {
                continue;
            }

            // A name can be both; take the radiant comment as documentation so the doc is not
            // lost to the de-duplication below.
            RadiantKey? alsoAKey = _objectFields.FindRadiantKey(fieldName, result.Language);
            entries.Add(new CompletionEntry(
                fieldName,
                CompletionKind.Field,
                DescribeField(_objectFields.FindField(fieldName)),
                "",
                alsoAKey?.Comment ?? ""));
        }

        // Radiant map-entity KVP keys, which scripts read straight off spawned entities.
        foreach ( RadiantKey key in _objectFields.RadiantKeysFor(result.Language) )
        {
            if ( !seen.Add(key.Name) )
            {
                continue;
            }

            entries.Add(new CompletionEntry(key.Name, CompletionKind.Field, key.Type + " (map key)", "", key.Comment));
        }

        return entries.ToImmutable();
    }

    /// <summary>Adds field names written as `owner.name = ...`, optionally only for one owner.</summary>
    private static void CollectAssignedFields(
        ImmutableArray<FunctionSymbol> functions,
        bool scopeToOwner,
        string ownerName,
        HashSet<string> seen,
        ImmutableArray<CompletionEntry>.Builder entries)
    {
        foreach ( FunctionSymbol function in functions )
        {
            foreach ( AssignmentSymbol assignment in function.Assignments )
            {
                // An empty owner marks a plain local, which is not a field at all.
                if ( assignment.OwnerName.Length == 0 )
                {
                    continue;
                }

                if ( scopeToOwner && !string.Equals(assignment.OwnerName, ownerName, StringComparison.Ordinal) )
                {
                    continue;
                }

                if ( seen.Add(assignment.Name) )
                {
                    entries.Add(new CompletionEntry(assignment.Name, CompletionKind.Field, "field"));
                }
            }
        }
    }

    /// <summary>The shared type of a field name's declarations, or a bare "field" when they disagree.</summary>
    private static string DescribeField(ImmutableArray<ObjectField> declarations)
    {
        if ( declarations.Length == 0 )
        {
            return "field";
        }

        string type = declarations[0].Type;
        foreach ( ObjectField declaration in declarations )
        {
            if ( !string.Equals(declaration.Type, type, StringComparison.OrdinalIgnoreCase) )
            {
                return "field";
            }
        }

        return type;
    }

    /// <summary>
    /// A whole function declaration, not just the word that starts one.
    ///
    /// Writing one by hand is four pieces of punctuation that are the same every time — the
    /// parentheses, the braces, and getting the brace onto its own line — so the snippet does them
    /// and leaves the caret on the NAME, which is the only part that varies.
    ///
    /// Laid out the way the formatter would: Allman braces and a tab, measured at 51,048 Allman
    /// against 37 same-line and 247,613 tab-led lines against 886 space-led across the stock
    /// scripts. A snippet that had to be reformatted the moment it landed would be a strange thing
    /// to ship.
    ///
    /// The dialect decides the opening: BO3 declares with the `function` keyword, while the merge
    /// dialects open with the bare name. The label stays "function" either way — it is what the
    /// user is looking for, not what gets inserted.
    /// </summary>
    private static CompletionEntry FunctionDeclarationSnippet(GameProfile game)
    {
        string opening = game.HasFunctionKeyword ? "function " : "";

        return new CompletionEntry(
            "function",
            CompletionKind.Snippet,
            "declaration",
            opening + "${1:name}()\n{\n\t$0\n}",
            "Declares a function, with the caret on the name.");
    }

    private ImmutableArray<CompletionEntry> StatementScopeCompletions(
        ParseResult result, string contextId, int offset, bool insideFunction, bool varargInScope, string callSuffix,
        GameProfile game, bool parameterHints)
    {
        ImmutableArray<CompletionEntry>.Builder entries = ImmutableArray.CreateBuilder<CompletionEntry>();

        // A '#' has been typed at top level, so nothing but a directive can be meant. Returning
        // early also keeps functions and variables out of the list. Inside a function body the
        // caller has already handled '#' as the start of a hash string.
        if ( !insideFunction && IsAfterDirectiveHash(result.Text, offset) )
        {
            return DirectiveCompletions(game);
        }

        // Both scopes are filtered to what the active game actually has, so e.g. CoD4 is not
        // offered class/#using. The dialect's global objects are NOT folded in here — see the
        // separate loop below for why.
        IEnumerable<string> words = insideFunction
            ? GscKeywords.StatementKeywords
            : GscKeywords.TopLevelKeywords;

        if ( !insideFunction )
        {
            entries.Add(FunctionDeclarationSnippet(game));
        }

        // The parameter pack is offered per-FUNCTION rather than from the keyword list, because
        // unlike every other keyword its availability depends on the declaration this cursor sits
        // in and not on the dialect alone. It is a Variable rather than a Keyword because that is
        // what it reads as at a use site: an array to index, count and iterate.
        if ( varargInScope )
        {
            entries.Add(new CompletionEntry(
                "vararg",
                CompletionKind.Variable,
                "array",
                "vararg",
                KeywordDocs.Find("vararg") ?? ""));
        }

        // The dialect's engine globals (self, level, world, …). Emitted in their own loop rather
        // than concatenated onto the keyword list, because the loop below gates every word through
        // GscKeywords.IsAvailable — which ends at the profile's KEYWORD set. No global object is a
        // keyword in any dialect, so every one of them failed that gate and none was ever offered,
        // in any game. The profile still decides the set, so BO3 gets world/classes and CoD4 does
        // not.
        //
        // Variables rather than Keywords for the reason vararg is: at a use site that is what they
        // read as — something to send a call on and index fields off, not a word with syntax.
        if ( insideFunction )
        {
            foreach ( string global in game.GlobalObjectNames )
            {
                entries.Add(new CompletionEntry(
                    global,
                    CompletionKind.Variable,
                    "global",
                    global,
                    KeywordDocs.Find(global) ?? ""));
            }
        }

        // Snippets whose construct only some dialects have. They cannot be contributed by the
        // extension, because a contributed snippet is registered per LANGUAGE ID and one id covers
        // five games — which is how CoD4 came to be offered a foreach loop it cannot run. See
        // GscSnippets.
        ImmutableArray<GscSnippets.Entry> snippets = GscSnippets.For(game, insideFunction);
        HashSet<string> snippetLabels = new(StringComparer.Ordinal);
        foreach ( GscSnippets.Entry snippet in snippets )
        {
            snippetLabels.Add(snippet.Label);
            entries.Add(new CompletionEntry(
                snippet.Label,
                CompletionKind.Snippet,
                "snippet",
                snippet.Body,
                snippet.Documentation));
        }

        foreach ( string keyword in words )
        {
            if ( !GscKeywords.IsAvailable(keyword, game) )
            {
                continue;
            }

            // A keyword a snippet already covers is not offered beside it. Two items with the same
            // label and different behaviour is a choice nobody wants to make, and in each of these
            // cases the snippet is the bare word plus the punctuation that follows it every time —
            // there is nothing the plain keyword does that it does not.
            //
            // `function` at top level is the same rule, spelled separately because its snippet is
            // FunctionDeclarationSnippet above: that one is built per dialect rather than listed,
            // since the merge games declare with a bare name and have no `function` keyword to hide.
            if ( snippetLabels.Contains(keyword) )
            {
                continue;
            }

            if ( !insideFunction && string.Equals(keyword, "function", StringComparison.Ordinal) )
            {
                continue;
            }

            // Documented keywords/directives (isdefined, notify, #using, …) carry their PDF blurb.
            string documentation = KeywordDocs.Find(keyword) ?? "";
            entries.Add(new CompletionEntry(
                keyword, CompletionKind.Keyword, "", KeywordInsertText(keyword, callSuffix), documentation));
        }

        if ( !insideFunction )
        {
            return entries.ToImmutable();
        }

        LanguageStore store = _database.StoreFor(result.Language);

        // Methods of the class this cursor is inside, own and inherited, FIRST — a bare name written
        // in a class body means a method: all 525 such calls in the stock BO3 scripts do. They are
        // added ahead of the namespace functions and builtins so that when the editor's own ordering
        // is a wash, the thing the call would actually reach is the thing offered.
        //
        // From the live extraction's class ranges rather than the store, so a method typed a moment
        // ago is offered before the record is reindexed.
        string? enclosingClass = EnclosingClassAt(result, offset);
        if ( enclosingClass is not null )
        {
            foreach ( ClassMethod method in MethodResolution.MethodsOf(
                store, contextId, enclosingClass, result.Extraction.Classes) )
            {
                entries.Add(MethodEntry(method, callSuffix, parameterHints));
            }
        }

        // Macros defined in this file.
        foreach ( GSCode.Parser.Preprocessing.MacroDefinition macro in result.Preprocessed.Macros.All )
        {
            if ( macro.SourceFile is null )
            {
                entries.Add(new CompletionEntry(macro.Name, CompletionKind.Macro, "macro"));
            }
        }

        // The declared set rather than the namespace spans, which carry a leading region named after
        // the file whenever its imports sit above its #namespace line — a phantom that cost a full
        // store scan per keystroke to return nothing.
        ImmutableArray<string> ownNamespaces = DatabaseQueries.DeclaredNamespaces(result);

        // Functions reachable through an import, dialect-dependent. A namespace dialect (BO3) still
        // needs the qualifier at the call site even though only the bare name was typed — so these
        // are offered under their bare name (for discovery and filtering) but INSERT the qualified
        // form. A merge dialect (#include) has already folded the function into local scope, so it
        // is offered and inserted exactly like one declared in this file.
        //
        // WHICH QUERY ANSWERS "what is in scope here" IS THE WHOLE SPLIT, and it is not the same
        // question in the two dialects.
        //
        // In BO3 a namespace is declared, shared deliberately, and IS the unit of scope, so the
        // file's own namespaces are asked first and the imported ones after.
        //
        // In a merge dialect there is no #namespace at all: SymbolExtractor defaults the namespace
        // to the FILE NAME STEM, which exists as a resolution fallback and names no scope anybody
        // wrote. Asking it here was wrong twice over on MW2's own scripts. Editing
        // maps\mp\_utility.gsc, every function of the unrelated maps\_utility.gsc was offered —
        // same stem, no #include between them, nothing in scope — and the asking file's own
        // functions came back from BOTH passes, since FunctionsInIncludeScope already returns them
        // through its same-file arm. Each query deduplicates internally and neither could see the
        // other, so `_playLocalSound` was listed twice.
        //
        // The include scope alone is the answer for a merge dialect: this file, plus the files it
        // actually includes.
        if ( game.ResolvesByNamespace )
        {
            foreach ( string ns in ownNamespaces )
            {
                foreach ( FunctionSymbol function in DatabaseQueries.FunctionsInNamespace(store, contextId, result.FilePath, ns, ownNamespaces) )
                {
                    entries.Add(FunctionEntry(function, callSuffix, parameterHints));
                }
            }

            ImmutableArray<string> importedPaths = DatabaseQueries.ImportedScriptPaths(result);
            foreach ( string ns in DatabaseQueries.ImportedNamespaces(store, contextId, importedPaths, ownNamespaces) )
            {
                // The namespace ITSELF, so typing its name (rather than one of its members by
                // heart) finds it too: "util" -> inserts "util::" and reopens the list, which the
                // ns:: handler above already fills with util's members. Without this, a function
                // whose name shares nothing with its namespace's name (the common case) was only
                // reachable by already knowing it existed.
                entries.Add(NamespaceEntry(ns));

                foreach ( FunctionSymbol function in DatabaseQueries.FunctionsInNamespace(
                    store, contextId, result.FilePath, ns, ownNamespaces) )
                {
                    entries.Add(ImportedFunctionEntry(function, ns, callSuffix, parameterHints));
                }
            }
        }
        else
        {
            foreach ( FunctionSymbol function in DatabaseQueries.FunctionsInIncludeScope(
                store, contextId, result.FilePath, DatabaseQueries.IncludedScriptPaths(result)) )
            {
                entries.Add(FunctionEntry(function, callSuffix, parameterHints));
            }
        }

        // Classes this file may name (for `new C()` and `C::`) — its own, plus those in the files
        // it #usings. The file's own come from the live extraction as well as the store, so a
        // class typed a moment ago completes before the record is reindexed.
        HashSet<string> classNames = new(StringComparer.Ordinal);
        foreach ( ClassSymbol classSymbol in result.Extraction.Classes )
        {
            classNames.Add(classSymbol.Name);
        }

        foreach ( ClassSymbol classSymbol in DatabaseQueries.AllVisibleClasses(
            store, contextId, result.FilePath, DatabaseQueries.ImportedScriptPaths(result)) )
        {
            classNames.Add(classSymbol.Name);
        }

        foreach ( string className in classNames )
        {
            entries.Add(new CompletionEntry(className, CompletionKind.Class, "class"));
        }

        // Namespace-less builtins.
        foreach ( BuiltinFunction builtin in _builtins.For(result.Language).All )
        {
            entries.Add(BuiltinEntry(builtin, callSuffix, parameterHints));
        }

        return entries.ToImmutable();
    }

    /// <summary>How much parameter text a label may carry before it is cut short.</summary>
    /// <remarks>
    /// A row that runs past the popup's width is truncated by the editor anyway, at whatever
    /// character it happens to reach — and a name cut mid-word reads as though it were the name.
    /// Cutting it here, at a separator, keeps the row honest about being incomplete.
    /// </remarks>
    private const int MaximumParameterHintLength = 42;

    /// <summary>
    /// The parameter list shown INLINE in a completion label — <c>( entity, team )</c>.
    ///
    /// Names only. The types, defaults and descriptions belong to signature help and the doc
    /// panel, which have room for them; what the list is for is telling apart the entries whose
    /// names alone do not, which in this codebase is most of them — <c>on_agent_generic_damaged</c>
    /// and <c>on_agent_player_damaged</c> differ by their arguments and nothing else.
    ///
    /// Free to produce: the parameters are already in hand when the list is built, unlike
    /// documentation, which is why this does not need the resolve round trip that a doc block does.
    /// </summary>
    private static string ParameterHint(ImmutableArray<ParameterSymbol> parameters, bool hasVarargs)
    {
        if ( parameters.Length == 0 )
        {
            return hasVarargs ? "( ... )" : "()";
        }

        System.Text.StringBuilder rendered = new();
        foreach ( ParameterSymbol parameter in parameters )
        {
            if ( rendered.Length > 0 )
            {
                rendered.Append(", ");
            }

            if ( rendered.Length > MaximumParameterHintLength )
            {
                rendered.Append('…');
                return "( " + rendered + " )";
            }

            rendered.Append(parameter.Name);
        }

        if ( hasVarargs )
        {
            rendered.Append(", ...");
        }

        return "( " + rendered + " )";
    }

    private static CompletionEntry FunctionEntry(FunctionSymbol function, string callSuffix, bool parameterHints)
    {
        string detail = function.Namespace.Length > 0 ? function.Namespace + "::" + function.Name : function.Name;

        // No Documentation: rendering a doc block for every function in the workspace on every
        // keystroke is exactly what completionItem/resolve exists to avoid. The namespace rides
        // along so resolve can find this function again.
        //
        // The parameters go in LabelDetail rather than the label, so the label stays exactly the
        // name — which is what the editor filters, sorts and resolves on.
        return new CompletionEntry(
            function.Name,
            CompletionKind.Function,
            detail,
            function.Name + callSuffix,
            Namespace: function.Namespace,
            LabelDetail: parameterHints ? ParameterHint(function.Parameters, function.HasVarargs) : "");
    }

    /// <summary>
    /// An engine builtin. Its parameters come from the FIRST overload — the data models several,
    /// but a completion row has space for one shape, and the doc panel behind it lists them all.
    /// A trailing <c>?</c> marks an optional parameter, matching how signature help renders them.
    /// </summary>
    private static CompletionEntry BuiltinEntry(BuiltinFunction builtin, string callSuffix, bool parameterHints)
    {
        if ( !parameterHints )
        {
            return new CompletionEntry(
                builtin.Name, CompletionKind.Function, "builtin", builtin.Name + callSuffix, IsBuiltin: true);
        }

        BuiltinOverload? overload = builtin.Overloads.FirstOrDefault();
        ImmutableArray<ParameterSymbol> parameters = overload is null
            ? []
            : [.. overload.Parameters.Select(static p =>
                new ParameterSymbol(p.Mandatory ? p.Name : p.Name + "?", false, ""))];

        return new CompletionEntry(
            builtin.Name,
            CompletionKind.Function,
            "builtin",
            builtin.Name + callSuffix,
            LabelDetail: ParameterHint(parameters, hasVarargs: false),
            IsBuiltin: true);
    }

    /// <summary>
    /// A function reached through a <c>#using</c> import, labelled and inserted FULLY QUALIFIED.
    ///
    /// The label carries the qualifier because that is what makes the namespace findable: the
    /// editor filters on the label, so typing "uti" surfaces every <c>util::</c> function at once
    /// rather than only the ones whose own name happens to begin that way. Typing the function's
    /// name still matches it — the qualifier is a prefix, not a replacement — so both routes work.
    ///
    /// Inserting the qualifier is not a convenience but a correctness matter: an unqualified call
    /// into another namespace does not resolve.
    /// </summary>
    private static CompletionEntry ImportedFunctionEntry(
        FunctionSymbol function, string ns, string callSuffix, bool parameterHints)
    {
        string qualified = ns + "::" + function.Name;

        // The label stays the QUALIFIED name and nothing else: filtering on the qualifier is the
        // whole reason it carries one, and the editor filters on the label. ResolveName keeps the
        // function's OWN name, which is what documentation is looked up by — that one is needed
        // whatever the parameter hints do, since `util::get_players` matches no symbol.
        return new CompletionEntry(
            qualified,
            CompletionKind.Function,
            "function",
            qualified + callSuffix,
            Namespace: ns,
            ResolveName: function.Name,
            LabelDetail: parameterHints ? ParameterHint(function.Parameters, function.HasVarargs) : "");
    }

    /// <summary>
    /// An imported namespace, offered by NAME so it is findable without already knowing one of its
    /// functions. Inserts the qualifier and reopens the list — which the explicit `ns::` handler
    /// above then fills with that namespace's members — the same walk-it-down shape path segments
    /// use for a folder.
    /// </summary>
    private static CompletionEntry NamespaceEntry(string ns)
    {
        return new CompletionEntry(ns, CompletionKind.Namespace, "namespace", ns + "::", Namespace: ns, RetriggerCompletion: true);
    }

    /// <summary>
    /// What a completed call brings with it after the name, honouring the callPunctuation setting.
    ///
    /// The semicolon is added only in STATEMENT position. `x = foobar()` and `foobar()[0]` are
    /// expressions, and closing them would put a semicolon in the middle of one.
    /// </summary>
    private static string CallSnippet(
        ImmutableArray<Token> tokens, int currentIndex, int offset, CallPunctuation punctuation)
    {
        switch ( punctuation )
        {
            case CallPunctuation.Off:
                return "";
            case CallPunctuation.ParensAndSemicolon when IsStatementPosition(tokens, currentIndex, offset):
                return "($0);";
            default:
                return "($0)";
        }
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
    /// What a completed KEYWORD inserts, derived from the same call suffix functions use so the
    /// callPunctuation setting governs both.
    ///
    /// <paramref name="callSuffix"/> already encodes the setting and the position: "" when
    /// punctuation is off, "($0)" for a call, "($0);" for a call that is a whole statement. Each
    /// keyword shape reads what it needs from that rather than re-deriving it.
    /// </summary>
    private static string KeywordInsertText(string keyword, string callSuffix)
    {
        bool punctuate = callSuffix.Length > 0;
        bool statement = callSuffix.EndsWith(';');

        switch ( GscKeywords.ShapeOf(keyword) )
        {
            case KeywordShape.StatementCall:
                return punctuate ? keyword + callSuffix : "";

            // `waittillframeend;`, `break;`, `continue;` — nothing but the terminator.
            case KeywordShape.BareStatement:
                return statement ? keyword + ";" : "";

            // `return$0;` — the caret sits before the terminator, so typing nothing leaves
            // `return;` while typing a value leaves `return 5;`. Neither needs a correction
            // afterwards, which is what a bare `return;` would cost in the value case.
            case KeywordShape.ValueStatement:
                return statement ? keyword + "$0;" : "";

            default:
                // Empty means "insert the label", which is what a plain word wants.
                return "";
        }
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
