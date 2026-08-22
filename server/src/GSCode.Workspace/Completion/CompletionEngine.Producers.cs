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
/// The lists themselves — one producer per context the dispatcher can land on.
///
/// These are the members that reach the database, the builtin API and the engine's field data, and
/// so the ones whose cost is a function of the WORKSPACE rather than of the file. PERF.md's
/// completion sweep is a measurement of this file.
/// </summary>
public sealed partial class CompletionEngine
{
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
    /// The directives, offered with the leading '#' stripped from what the editor filters and
    /// inserts. The client's word pattern excludes '#', so after typing "#p" the current word is
    /// "p": a "#precache" label would be filtered out while "private" survived — the reported
    /// bug. Filtering on "precache" matches, and inserting "precache" onto the '#' already in the
    /// buffer avoids producing "##precache". The label keeps its '#' so the list stays readable.
    /// </summary>
    /// <param name="keywords">
    /// Which set to draw from — <see cref="GscKeywords.TopLevelKeywords"/> at file scope, or
    /// <see cref="GscKeywords.BodyDirectives"/> inside a function body, where only the directives
    /// the preprocessor dispatches from its flat walk are legal. Non-directive words are skipped
    /// either way, so the top-level list can be passed whole.
    /// </param>
    private static ImmutableArray<CompletionEntry> DirectiveCompletions(
        GameProfile game, ImmutableArray<string> keywords)
    {
        ImmutableArray<CompletionEntry>.Builder entries = ImmutableArray.CreateBuilder<CompletionEntry>();

        foreach ( string keyword in keywords )
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

    /// <summary>
    /// The names bound INSIDE this function: its parameters, then the locals assigned above the
    /// cursor.
    ///
    /// These were never offered at all. Nothing in the workspace lists is per-function, so the one
    /// category of name a script writes most — the variable three lines up — was the one category
    /// completion could not produce, and the editor's own word-based suggestions were what filled
    /// the gap until the server's lists (a median of 1,168 entries in statement scope) began
    /// out-scoring them.
    ///
    /// A local's introduction is an ASSIGNMENT, since GSC has no declaration form: the same
    /// definition <see cref="LocalDefinition"/> resolves go-to-definition against, so the two
    /// surfaces agree about what a name means here. Fields are excluded for the same reason they
    /// are there — `self.count = 1` writes to something that outlives the call, and a bare `count`
    /// does not reach it.
    /// </summary>
    /// <param name="position">
    /// The cursor. Assignments BELOW it are not offered: the value would not exist yet at the point
    /// being written, and 5016 reports exactly that read as unassigned. A completion list that leads
    /// to a diagnostic is worse than one entry short — the rule <c>vararg</c> is held to above.
    ///
    /// A loop variable passes on the same terms without a special case, since `foreach ( player in
    /// players )` binds it in the header, above every use in the body.
    /// </param>
    /// <param name="seen">
    /// Names already offered, and added to as this goes. Case-insensitive, like every other GSC
    /// name: `Count` and `count` are one variable, and offering both would make the list disagree
    /// with the language. Seeded with the enclosing class's members, whose declaration is the truer
    /// reading of a bare name a constructor assigns.
    /// </param>
    private static void CollectLocalScope(
        FunctionSymbol function,
        Position position,
        HashSet<string> seen,
        ImmutableArray<CompletionEntry>.Builder entries)
    {
        foreach ( ParameterSymbol parameter in function.Parameters )
        {
            if ( seen.Add(parameter.Name) )
            {
                entries.Add(new CompletionEntry(
                    parameter.Name, CompletionKind.Variable, parameter.ByRef ? "parameter (by ref)" : "parameter"));
            }
        }

        foreach ( AssignmentSymbol assignment in function.Assignments )
        {
            // An owner makes it a field on something, not a local.
            if ( assignment.OwnerName.Length > 0 )
            {
                continue;
            }

            if ( IsAfter(assignment.Range.Start, position) || !seen.Add(assignment.Name) )
            {
                continue;
            }

            entries.Add(new CompletionEntry(
                assignment.Name,
                CompletionKind.Variable,
                assignment.IsLoopVariable ? "loop variable" : "local"));
        }
    }

    /// <summary>Whether <paramref name="candidate"/> sits strictly after <paramref name="anchor"/>.</summary>
    private static bool IsAfter(Position candidate, Position anchor)
    {
        if ( candidate.Line != anchor.Line )
        {
            return candidate.Line > anchor.Line;
        }

        return candidate.Character > anchor.Character;
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

    /// <param name="enclosingFunction">
    /// The declaration the cursor is inside, or null at file scope. Null IS "not inside a function",
    /// so the two never disagree — and where it is not null it also names the parameters and locals
    /// that are in scope, which no other input to this method can answer.
    /// </param>
    private ImmutableArray<CompletionEntry> StatementScopeCompletions(
        ParseResult result, string contextId, int offset, Position position, FunctionSymbol? enclosingFunction,
        string callSuffix, GameProfile game, bool parameterHints)
    {
        ImmutableArray<CompletionEntry>.Builder entries = ImmutableArray.CreateBuilder<CompletionEntry>();
        bool insideFunction = enclosingFunction is not null;

        // A '#' has been typed at top level, so nothing but a directive can be meant. Returning
        // early also keeps functions and variables out of the list. Inside a function body the
        // caller has already answered '#' — with the body-legal directives and, where the dialect
        // has them, hash strings.
        if ( !insideFunction && IsAfterDirectiveHash(result.Text, offset) )
        {
            return DirectiveCompletions(game, GscKeywords.TopLevelKeywords);
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
        //
        // Both halves matter. Offering it from the plain keyword list would suggest it in every
        // function on the dialect, and in a function without `...` nothing binds it, so accepting
        // the suggestion earns a 5024. A completion list that leads to a diagnostic is worse than
        // one entry short.
        if ( game.HasVarargBinding && enclosingFunction is not null && enclosingFunction.HasVarargs )
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
                snippet.Documentation,
                RetriggerCompletion: snippet.Retrigger));
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

        // The class this cursor is inside, read from the live extraction's ranges rather than the
        // store, so a member or method typed a moment ago is offered before the record is
        // reindexed.
        string? enclosingClass = EnclosingClassAt(result, offset);

        // The names bound RIGHT HERE, nearest first and ahead of every workspace-wide list below:
        // the enclosing class's `var` members, then this function's parameters and locals.
        //
        // Members come first because in a class body a bare name IS the member — BO3's
        // AnimationAdjustmentInfoZ constructor writes `adjustMentStarted = false;`, and extraction
        // records that write as a local like any other. Both readings produce the same name, so
        // whichever runs first decides how the one row is labelled, and "member of
        // AnimationAdjustmentInfoZ" is the true answer where a class declares it.
        HashSet<string> boundNames = new(StringComparer.OrdinalIgnoreCase);

        if ( enclosingClass is not null )
        {
            foreach ( ClassMember member in MethodResolution.MembersOf(
                store, contextId, enclosingClass, result.Extraction.Classes) )
            {
                if ( boundNames.Add(member.Member.Name) )
                {
                    entries.Add(new CompletionEntry(
                        member.Member.Name, CompletionKind.Field, "member of " + member.OwnerClass.Name));
                }
            }
        }

        CollectLocalScope(enclosingFunction!, position, boundNames, entries);

        // Methods of the class this cursor is inside, own and inherited — a bare name written in a
        // class body means a method: all 525 such calls in the stock BO3 scripts do. They are added
        // ahead of the namespace functions and builtins so that when the editor's own ordering is a
        // wash, the thing the call would actually reach is the thing offered.
        if ( enclosingClass is not null )
        {
            foreach ( ClassMethod method in MethodResolution.MethodsOf(
                store, contextId, enclosingClass, result.Extraction.Classes) )
            {
                entries.Add(MethodEntry(method, callSuffix, parameterHints));
            }
        }

        // Every macro the preprocessor has in scope for this file, WHEREVER it was defined.
        //
        // The table is built per parse, from the root file and the headers it #inserts — that is
        // already the answer to "what can this file expand", so the file each definition came from
        // does not narrow it. Filtering to `SourceFile is null` kept only the root file's own,
        // which threw away the ones a header exists to supply: a script whose constants all live
        // in a shared .gsh got none of them, which is the normal arrangement rather than an
        // unusual one.
        foreach ( GSCode.Parser.Preprocessing.MacroDefinition macro in result.Preprocessed.Macros.All )
        {
            entries.Add(MacroEntry(macro, callSuffix, parameterHints));
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
}
