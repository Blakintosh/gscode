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

        ImmutableArray<string> declaredNamespaces = result.Extraction.DeclaredNamespaces;

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

            // #include is the merge dialects' import and takes the same script path as #using. It is
            // not an #insert: a header is a Treyarch thing, and those dialects have none.
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

        // Segment -> whether it is a folder (has more path below it). Read from the folder index
        // rather than by rewriting every record's path and testing it against the typed folder,
        // which made this per-keystroke list grow with the workspace.
        Dictionary<string, bool> segments = new(StringComparer.OrdinalIgnoreCase);
        foreach ( (string Segment, bool IsFolder) child in PathChildren(result, isInsert, directory, contextId) )
        {
            segments[child.Segment] = segments.TryGetValue(child.Segment, out bool existing) ? existing || child.IsFolder : child.IsFolder;
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
    /// What a path directive may name under one folder: headers for <c>#insert</c>, this file's own
    /// language for <c>#using</c>. A <c>.gsc</c> never includes a <c>.csc</c> or vice versa.
    ///
    /// <c>#insert</c> writes the extension, <c>#using</c> does not — an asymmetry of the language,
    /// and unanimous across the stock scripts: all 2,137 <c>#insert</c>s end in <c>.gsh</c> and all
    /// 7,738 <c>#using</c>s are bare. The two indexes keep their paths in those two forms.
    /// </summary>
    private List<(string Segment, bool IsFolder)> PathChildren(
        ParseResult result, bool isInsert, string directory, string contextId)
    {
        if ( isInsert )
        {
            return _database.GshPathChildren(directory, contextId);
        }

        return _database.StoreFor(result.Language).PathChildren(directory, contextId);
    }

    /// <summary>
    /// The literals of one kind this file uses, and the workspace's cut to what has been typed — see
    /// <see cref="VocabularyCut"/> for which ones and why a list of every one is not sent.
    /// </summary>
    /// <param name="typed">What has been typed inside the literal so far, or "" before any of it.</param>
    /// <param name="quoted">
    /// Whether to insert the surrounding quotes. True when only the sigil has been typed — at
    /// `notify(#` the cursor is not inside a string yet, so the entry has to supply them.
    /// </param>
    private ImmutableArray<CompletionEntry> LiteralCompletions(
        ParseResult result, string contextId, SymbolKind literalKind, string typed, bool quoted = false)
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

        // The workspace's DISTINCT literals, from the store's vocabulary, rather than every
        // reference of every record — see VocabularyIndex for what that walk cost at scale.
        string detail = LiteralDetail(literalKind);
        VocabularyCut cut = new(typed);
        store.VisibleLiterals(literalKind, contextId, name =>
        {
            if ( IsNameLike(name.Name) && !seen.Contains(name.Name) )
            {
                cut.Offer(new VocabularyCandidate(name.Name, name.Files, detail));
            }
        });

        foreach ( VocabularyCandidate candidate in cut.Best() )
        {
            AddLiteral(candidate.Name, detail, seen, entries, quoted);
        }

        return entries.ToImmutable();
    }

    /// <summary>
    /// The most workspace names one literal or field list carries, beyond the file's own.
    ///
    /// Not a comfort setting. cod4 at 50,000 files has 18,144 distinct string literals, and sending
    /// them all put 2.3 million characters of JSON on the wire for every completion inside a string:
    /// 70 ms p50 to serialize and 16 MB of garbage, against 6.6 ms to build the list. The user picks
    /// from what is on screen, and the list is marked incomplete (CompletionEntry.Narrowed), so the
    /// next keystroke re-asks with more text rather than filtering this page.
    /// </summary>
    private const int MaximumVocabularyCandidates = 200;

    /// <summary>A workspace name a literal or field list may offer, before the list is cut to what was typed.</summary>
    private readonly record struct VocabularyCandidate(string Name, int Files, string Detail, string Documentation = "");

    /// <summary>
    /// The best <see cref="MaximumVocabularyCandidates"/> of the names offered to it that contain the
    /// typed text, ignoring case. Names the text BEGINS come ahead of names it only appears in; then
    /// the more files write a name the earlier it comes; then by name, so the cut never depends on the
    /// index's order.
    ///
    /// Contains rather than begins-with because literals are paths as often as names: typing
    /// <c>misc</c> is reaching for <c>fx/misc/smoke</c> as much as for <c>misc_model</c>. With nothing
    /// typed every name matches and the ranking alone decides — the most widely used ones.
    ///
    /// Offered one name at a time, straight from the index walk, so a list of every candidate never
    /// exists: a heap holds the best so far with the worst on top, and each newcomer either replaces
    /// that one or is dropped. Sorting all of cod4's 18,144 literals to keep 200 cost more than the
    /// walk that found them.
    /// </summary>
    private sealed class VocabularyCut
    {
        private readonly string _typed;
        private readonly PriorityQueue<VocabularyCandidate, RankKey> _best = new(WorstFirst.Instance);

        public VocabularyCut(string typed)
        {
            _typed = typed;
        }

        public void Offer(VocabularyCandidate candidate)
        {
            bool prefix = false;
            if ( _typed.Length > 0 )
            {
                int at = candidate.Name.IndexOf(_typed, StringComparison.OrdinalIgnoreCase);
                if ( at < 0 )
                {
                    return;
                }

                prefix = at == 0;
            }

            RankKey rank = new(prefix, candidate.Files, candidate.Name);
            if ( _best.Count < MaximumVocabularyCandidates )
            {
                _best.Enqueue(candidate, rank);
                return;
            }

            _best.TryPeek(out _, out RankKey worst);
            if ( Compare(rank, worst) < 0 )
            {
                _best.DequeueEnqueue(candidate, rank);
            }
        }

        /// <summary>The kept names, best first.</summary>
        public List<VocabularyCandidate> Best()
        {
            List<VocabularyCandidate> kept = new(_best.Count);
            while ( _best.TryDequeue(out VocabularyCandidate candidate, out _) )
            {
                kept.Add(candidate);
            }

            // Dequeued worst first.
            kept.Reverse();
            return kept;
        }

        /// <summary>Negative when <paramref name="left"/> ranks ahead of <paramref name="right"/>.</summary>
        private static int Compare(RankKey left, RankKey right)
        {
            if ( left.Prefix != right.Prefix )
            {
                return left.Prefix ? -1 : 1;
            }

            int files = right.Files.CompareTo(left.Files);
            if ( files != 0 )
            {
                return files;
            }

            int name = string.Compare(left.Name, right.Name, StringComparison.OrdinalIgnoreCase);
            return name != 0 ? name : string.CompareOrdinal(left.Name, right.Name);
        }

        private readonly record struct RankKey(bool Prefix, int Files, string Name);

        /// <summary>Orders the heap so its top is the WORST kept name — the one a better newcomer evicts.</summary>
        private sealed class WorstFirst : IComparer<RankKey>
        {
            public static readonly WorstFirst Instance = new();

            public int Compare(RankKey left, RankKey right)
            {
                return VocabularyCut.Compare(right, left);
            }
        }
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
            // Literals inside a macro body are skipped: the range is the invocation site, and the
            // text is the macro author's, not a name this file uses.
            if ( entry.Kind != ReferenceKind.Literal || entry.Key.Kind != literalKind || entry.FromMacro )
            {
                continue;
            }

            AddLiteral(entry.Key.Name, detail, seen, entries, quoted);
        }
    }

    private static void AddLiteral(
        string name, string detail, HashSet<string> seen, ImmutableArray<CompletionEntry>.Builder entries, bool quoted)
    {
        if ( !IsNameLike(name) || !seen.Add(name) )
        {
            return;
        }

        entries.Add(new CompletionEntry(
            name,
            CompletionKind.Literal,
            detail,
            quoted ? "\"" + name + "\"" : "",
            Narrowed: true));
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

        foreach ( FunctionSymbol function in DatabaseQueries.FunctionsInNamespace(store, contextId, result.FilePath, ns, result.Extraction.DeclaredNamespaces) )
        {
            if ( seen.Add(function.KeyName) )
            {
                entries.Add(FunctionEntry(function, callSuffix, parameterHints));
            }
        }

        // `cScene::` names a class, not a namespace, so the namespace query alone finds none of its
        // methods.
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
    /// What may follow an inline path qualifier (<c>maps\mp\_utility::</c>) on a merge dialect: the
    /// functions of the ONE file that path names, by relative path rather than by bare name stem.
    ///
    /// Namespace matching would ask for functions by the qualifier's LAST segment alone
    /// (<c>_utility</c>), which reaches every file sharing that stem — exactly MW2's own shape,
    /// where <c>maps\_utility.gsc</c> and <c>maps\mp\_utility.gsc</c> both default their functions'
    /// namespace to the name they share.
    /// </summary>
    private ImmutableArray<CompletionEntry> InlinePathFunctionCompletions(
        ParseResult result, string contextId, string writtenPath, string callSuffix, bool parameterHints)
    {
        LanguageStore store = _database.StoreFor(result.Language);
        string normalizedWritten = RelativePathIndex.Normalize(writtenPath);

        ImmutableArray<CompletionEntry>.Builder entries = ImmutableArray.CreateBuilder<CompletionEntry>();
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);

        // The files at that path, from the relative-path index, keyed on the same normalization.
        foreach ( ScriptRecord record in DatabaseQueries.RecordsAt(store, [normalizedWritten]) )
        {
            if ( !ScriptDatabase.CanSee(contextId, record.ContextId) )
            {
                continue;
            }

            foreach ( FunctionSymbol function in record.Functions )
            {
                if ( seen.Add(function.KeyName) )
                {
                    entries.Add(FunctionEntry(function, callSuffix, parameterHints));
                }
            }
        }

        return entries.ToImmutable();
    }

    /// <summary>
    /// How much has to be typed before functions this file cannot call yet are offered.
    ///
    /// Not a comfort setting. Statement scope already returns a median of 1,930 entries, and the
    /// candidates here come from the whole workspace rather than from what is in scope — so
    /// offering them from the first character would bury the names that ARE in scope under names
    /// that cost a directive, in the position where the user is most likely typing a local.
    /// Three characters is the point where a name is being reached for rather than begun.
    /// </summary>
    private const int MinimumImportPrefix = 3;

    /// <summary>
    /// The most unimported candidates one list carries. A cap rather than a full answer because the
    /// user picks from what is on screen — and because the handler marks the list incomplete when
    /// this truncates, so the next keystroke re-asks with a narrower prefix instead of the editor
    /// filtering a stale page.
    /// </summary>
    private const int MaximumImportCandidates = 50;

    /// <summary>
    /// Functions in files this one has not imported, offered with the directive they need.
    ///
    /// Skipped for any name already in the list: something in scope under that name is what the
    /// user meant, and a second row that costs an import would be the same word twice.
    /// </summary>
    private void AddUnimportedFunctions(
        ParseResult result,
        string contextId,
        GameProfile game,
        string callSuffix,
        bool parameterHints,
        string typedWord,
        HashSet<string> seenFunctions,
        ImmutableArray<CompletionEntry>.Builder entries)
    {
        if ( typedWord.Length < MinimumImportPrefix )
        {
            return;
        }

        LanguageStore store = _database.StoreFor(result.Language);
        foreach ( UnimportedFunction candidate in DatabaseQueries.UnimportedFunctions(
            store, contextId, result.FilePath, result, typedWord, MaximumImportCandidates, game) )
        {
            if ( seenFunctions.Add(candidate.Function.KeyName) )
            {
                entries.Add(UnimportedFunctionEntry(candidate, game, callSuffix, parameterHints));
            }
        }
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

    /// <summary>
    /// The fields this file assigns and <c>.size</c>, always; and the workspace's assigned fields, the
    /// engine's object fields and the radiant map keys, cut to what has been typed after the dot —
    /// see <see cref="VocabularyCut"/>.
    /// </summary>
    /// <param name="typed">What has been typed of the field name so far, or "" right after the dot.</param>
    private ImmutableArray<CompletionEntry> FieldCompletions(
        ParseResult result,
        string contextId,
        string ownerName,
        FieldScope fieldScope,
        string typed)
    {
        ImmutableArray<CompletionEntry>.Builder entries = ImmutableArray.CreateBuilder<CompletionEntry>();
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);

        // Scope only when asked AND the owner is known; otherwise every owner contributes.
        bool scopeToOwner = fieldScope == FieldScope.Owner && ownerName.Length > 0;

        // Every spelling the workspace writes, from the store's vocabulary rather than every
        // function of every record — see VocabularyIndex — grouped by the name the engine sees.
        LanguageStore fieldStore = _database.StoreFor(result.Language);
        Dictionary<string, List<VocabularyName>> spellings =
            SpellingsByName(fieldStore.VisibleFieldNames(scopeToOwner ? ownerName : null, contextId));

        // The live file first, so unsaved edits are offered immediately and in the file's own
        // spelling, then every visible record's — a field assigned on `level` in one file is
        // reachable from all of them.
        CollectAssignedFields(result.Extraction.Functions, scopeToOwner, ownerName, spellings, seen, entries);

        // The .size pseudo-member, whatever has been typed: it is what an array is asked for.
        if ( seen.Add("size") )
        {
            entries.Add(new CompletionEntry("size", CompletionKind.Field, "int (read-only)", Narrowed: true));
        }

        VocabularyCut cut = new(typed);
        foreach ( KeyValuePair<string, List<VocabularyName>> name in spellings )
        {
            // The most-used spelling labels the row; SpellingsByName put it first.
            string label = name.Value[0].Name;
            if ( seen.Add(label) )
            {
                cut.Offer(new VocabularyCandidate(label, name.Value[0].Files, FieldDetail(name.Value, label)));
            }
        }

        // Engine object fields. The owner's entity kind isn't known at this point, so every
        // documented field name is offered with its type when the declaring kinds agree. No file
        // count: they rank after the workspace's own fields unless the typed text reaches them.
        foreach ( string fieldName in _objectFields.FieldNames() )
        {
            if ( !seen.Add(fieldName) )
            {
                continue;
            }

            // A name can be both; take the radiant comment as documentation so the doc is not
            // lost to the de-duplication below.
            RadiantKey? alsoAKey = _objectFields.FindRadiantKey(fieldName, result.Language);
            cut.Offer(new VocabularyCandidate(
                fieldName, 0, DescribeField(_objectFields.FindField(fieldName)), alsoAKey?.Comment ?? ""));
        }

        // Radiant map-entity KVP keys, which scripts read straight off spawned entities.
        foreach ( RadiantKey key in _objectFields.RadiantKeysFor(result.Language) )
        {
            if ( !seen.Add(key.Name) )
            {
                continue;
            }

            cut.Offer(new VocabularyCandidate(key.Name, 0, key.Type + " (map key)", key.Comment));
        }

        foreach ( VocabularyCandidate candidate in cut.Best() )
        {
            entries.Add(new CompletionEntry(
                candidate.Name, CompletionKind.Field, candidate.Detail, "", candidate.Documentation, Narrowed: true));
        }

        return entries.ToImmutable();
    }

    /// <summary>
    /// The workspace's field spellings grouped by the name the engine sees, which ignores case, each
    /// group most-used first — by files writing it, then ordinally, so the order never depends on
    /// the index's.
    ///
    /// Kept as WRITTEN rather than folded to one case. A workspace that writes both <c>level.foo</c>
    /// and <c>level.Foo</c> has one field, but which spelling a row shows was an accident of index
    /// order, and the other spelling is worth seeing: it is how someone else wrote the same field.
    /// </summary>
    private static Dictionary<string, List<VocabularyName>> SpellingsByName(List<VocabularyName> names)
    {
        Dictionary<string, List<VocabularyName>> byName = new(StringComparer.OrdinalIgnoreCase);
        foreach ( VocabularyName name in names )
        {
            if ( !byName.TryGetValue(name.Name, out List<VocabularyName>? written) )
            {
                written = [];
                byName[name.Name] = written;
            }

            written.Add(name);
        }

        foreach ( List<VocabularyName> written in byName.Values )
        {
            written.Sort(static (left, right) =>
            {
                int files = right.Files.CompareTo(left.Files);
                return files != 0 ? files : string.CompareOrdinal(left.Name, right.Name);
            });
        }

        return byName;
    }

    /// <summary>
    /// "field", naming every OTHER spelling the workspace writes this field in —
    /// <c>field, also written foo</c> on a row labelled <c>Foo</c>.
    /// </summary>
    private static string FieldDetail(List<VocabularyName>? written, string label)
    {
        if ( written is null )
        {
            return "field";
        }

        List<string> others = [];
        foreach ( VocabularyName spelling in written )
        {
            if ( !string.Equals(spelling.Name, label, StringComparison.Ordinal) )
            {
                others.Add(spelling.Name);
            }
        }

        return others.Count == 0 ? "field" : "field, also written " + string.Join(", ", others);
    }

    /// <summary>
    /// Adds field names written as `owner.name = ...`, optionally only for one owner, in this file's
    /// own spelling — the first it writes, where it writes more than one.
    /// </summary>
    private static void CollectAssignedFields(
        ImmutableArray<FunctionSymbol> functions,
        bool scopeToOwner,
        string ownerName,
        Dictionary<string, List<VocabularyName>> spellings,
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
                    spellings.TryGetValue(assignment.Name, out List<VocabularyName>? written);
                    entries.Add(new CompletionEntry(
                        assignment.Name, CompletionKind.Field, FieldDetail(written, assignment.Name), Narrowed: true));
                }
            }
        }
    }

    /// <summary>
    /// The names bound INSIDE this function: its parameters, then the locals assigned above the
    /// cursor.
    ///
    /// Nothing in the workspace lists is per-function, so without this the one category of name a script
    /// writes most — the variable three lines up — is the one completion cannot produce, and the
    /// server's lists (a median of 1,168 entries in statement scope) out-score the editor's own word
    /// suggestions that would otherwise fill the gap.
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
    /// <param name="typedWord">
    /// What has been typed of the word under the cursor, or "" when nothing has been or auto-import
    /// is off. Only the unimported-function arm reads it — see <see cref="MinimumImportPrefix"/>.
    /// </param>
    private ImmutableArray<CompletionEntry> StatementScopeCompletions(
        ParseResult result, string contextId, int offset, Position position, FunctionSymbol? enclosingFunction,
        string callSuffix, GameProfile game, bool parameterHints, string typedWord = "")
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
        //
        // File scope takes BOTH lists, because it is not the declarations-only position it looks
        // like. A top-level macro invocation is a CALL, so it opens an expression outside every
        // function body: the shipped BO3 scripts pass `undefined` to REGISTER_SYSTEM 467 times, and
        // `undefined` is a statement-scope word that file scope could not complete. Splitting the
        // statement list into expression atoms and control flow would buy nothing but a rule to
        // maintain — `if` offered at file scope is noise, not a wrong answer.
        List<string> words = [];
        if ( insideFunction )
        {
            words.AddRange(GscKeywords.StatementKeywords);
        }
        else
        {
            words.AddRange(GscKeywords.TopLevelKeywords);
            words.AddRange(GscKeywords.StatementKeywords);
        }

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

        // Everything below applies at file scope too. The macros a header supplies, the file's own
        // functions and its classes are not per-CURSOR facts — the macro table is built per parse
        // from this file plus the headers it #inserts, and a function is in scope for the file, not
        // for a body — so the two scopes differ only where a name is BOUND differently.
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

        // Parameters and locals are the one category that genuinely IS per-cursor: outside a
        // declaration nothing is bound, so there is nothing to collect rather than a list to
        // suppress.
        if ( enclosingFunction is not null )
        {
            CollectLocalScope(enclosingFunction, position, boundNames, entries);
        }

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
        // does not narrow it. Filtering to `SourceFile is null` would keep only the root file's own
        // and throw away the ones a header exists to supply: a script whose constants all live in a
        // shared .gsh would get none of them, and that is the normal arrangement.
        // Gated on the dialect, like every other category here. The preprocessor records a #define
        // whatever game is active, but only BO3 HAS one: in the IW line the single #define in the
        // corpus is a commented-out block of C in _hud.gsc, and completing its name would offer an
        // expansion the engine will never perform.
        if ( game.HasMacros )
        {
            foreach ( GSCode.Parser.Preprocessing.MacroDefinition macro in result.Preprocessed.Macros.All )
            {
                entries.Add(MacroEntry(macro, callSuffix, parameterHints));
            }
        }

        // The declared set rather than the namespace spans, which carry a leading region named after
        // the file whenever its imports sit above its #namespace line — a phantom that cost a full
        // store scan per keystroke to return nothing.
        ImmutableArray<string> ownNamespaces = result.Extraction.DeclaredNamespaces;

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
        //
        // The file's own functions come from the live extraction FIRST — same treatment as the
        // classes just below — so one typed a moment ago completes before the record is
        // reindexed. Both branches below still read the STORE for "this file's own functions" too
        // (a namespace dialect through its own-namespace loop, a merge dialect through
        // FunctionsInIncludeScope's same-file arm), so seenFunctions keeps that from adding a
        // second, stale-shaped row for a name the live extraction already offered.
        HashSet<string> seenFunctions = new(StringComparer.OrdinalIgnoreCase);
        foreach ( FunctionSymbol function in result.Extraction.Functions )
        {
            if ( seenFunctions.Add(function.KeyName) )
            {
                entries.Add(FunctionEntry(function, callSuffix, parameterHints));
            }
        }

        if ( game.ResolvesByNamespace )
        {
            foreach ( string ns in ownNamespaces )
            {
                foreach ( FunctionSymbol function in DatabaseQueries.FunctionsInNamespace(store, contextId, result.FilePath, ns, ownNamespaces) )
                {
                    if ( seenFunctions.Add(function.KeyName) )
                    {
                        entries.Add(FunctionEntry(function, callSuffix, parameterHints));
                    }
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

                // Not gated on seenFunctions: this is a DIFFERENT reachability path (through an
                // import, inserted qualified) from the bare-name entry above, even for the same
                // function name.
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
                if ( seenFunctions.Add(function.KeyName) )
                {
                    entries.Add(FunctionEntry(function, callSuffix, parameterHints));
                }
            }
        }

        AddUnimportedFunctions(result, contextId, game, callSuffix, parameterHints, typedWord, seenFunctions, entries);

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

        AddBuiltins(result, callSuffix, parameterHints, entries);

        return entries.ToImmutable();
    }

    /// <summary>
    /// What may follow <c>sys::</c>: the engine's library and nothing else. Not the script functions
    /// that share a builtin's name — stepping past those is the one thing the qualifier is for.
    /// </summary>
    private ImmutableArray<CompletionEntry> BuiltinQualifiedCompletions(
        ParseResult result, string callSuffix, bool parameterHints)
    {
        ImmutableArray<CompletionEntry>.Builder entries = ImmutableArray.CreateBuilder<CompletionEntry>();
        AddBuiltins(result, callSuffix, parameterHints, entries);
        return entries.ToImmutable();
    }

    /// <summary>
    /// The namespace-less builtins, for statement scope and for <c>sys::</c> alike, so the two lists
    /// cannot come to differ in what they offer.
    /// </summary>
    private void AddBuiltins(
        ParseResult result, string callSuffix, bool parameterHints, ImmutableArray<CompletionEntry>.Builder entries)
    {
        foreach ( BuiltinFunction builtin in _builtins.For(result.Language).All )
        {
            entries.Add(BuiltinEntry(builtin, callSuffix, parameterHints));
        }
    }
}
