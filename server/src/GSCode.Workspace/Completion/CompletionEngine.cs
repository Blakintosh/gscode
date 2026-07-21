using System.Collections.Immutable;
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
    public ImmutableArray<CompletionEntry> Complete(ParseResult result, string contextId, Position position, bool includeLiterals = true)
    {
        ImmutableArray<Token> tokens = result.Lexed.Tokens;
        int offset = result.Text.GetOffset(position);

        // Inside a string/istring/hash literal: offer known literals of that kind (or nothing,
        // since statement-scope suggestions never make sense inside a string).
        int literalIndex = FindLiteralAtOffset(tokens, offset);
        if ( literalIndex >= 0 )
        {
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

        // #precache( "type" ...) — offer asset types as the first argument.
        if ( TryPrecacheContext(tokens, triggerIndex) )
        {
            return AssetTypeCompletions();
        }

        // #using / #insert path — offer path segments.
        ImmutableArray<CompletionEntry> pathCompletions = TryPathContext(result, contextId, tokens, currentIndex, offset);
        if ( !pathCompletions.IsDefault )
        {
            return pathCompletions;
        }

        // ns:: — offer functions in that namespace only.
        if ( triggerIndex >= 0 && tokens[triggerIndex].Kind == TokenKind.ScopeResolution )
        {
            int nsIndex = PreviousSignificant(tokens, triggerIndex);
            if ( nsIndex >= 0 && tokens[nsIndex].Kind == TokenKind.Identifier )
            {
                string ns = tokens[nsIndex].GetText(result.Text).ToString().ToLowerInvariant();
                return NamespaceFunctionCompletions(result, contextId, ns);
            }
        }

        // owner. — offer fields.
        if ( triggerIndex >= 0 && tokens[triggerIndex].Kind == TokenKind.Dot )
        {
            return FieldCompletions(result);
        }

        return StatementScopeCompletions(result, contextId, position);
    }

    // --- Contexts ---

    private ImmutableArray<CompletionEntry> AssetTypeCompletions()
    {
        ImmutableArray<CompletionEntry>.Builder entries = ImmutableArray.CreateBuilder<CompletionEntry>();
        foreach ( string name in PrecacheAssetTypes.AllNames.OrderBy(static n => n, StringComparer.Ordinal) )
        {
            entries.Add(new CompletionEntry(name, CompletionKind.AssetType, "precache asset type", "\"" + name + "\""));
        }

        return entries.ToImmutable();
    }

    private ImmutableArray<CompletionEntry> TryPathContext(ParseResult result, string contextId, ImmutableArray<Token> tokens, int currentIndex, int offset)
    {
        // Detect a #using/#insert earlier on the same line than the cursor.
        int probe = currentIndex >= 0 ? currentIndex : FirstAtOrAfter(tokens, offset);
        int scan = probe - 1;
        while ( scan >= 0 )
        {
            TokenKind kind = tokens[scan].Kind;
            if ( kind == TokenKind.Newline )
            {
                break;
            }

            if ( kind == TokenKind.UsingDirective || kind == TokenKind.InsertDirective )
            {
                return PathSegmentCompletions(result, contextId);
            }

            scan--;
        }

        return default;
    }

    private ImmutableArray<CompletionEntry> PathSegmentCompletions(ParseResult result, string contextId)
    {
        // Offer top-level script folders/files reachable from this file's context. Kept
        // simple: enumerate index targets and surface their leading path segment options.
        HashSet<string> segments = new(StringComparer.OrdinalIgnoreCase);
        foreach ( ScriptRecord record in _database.StoreFor(result.Language).AllRecords )
        {
            if ( record.RelativePath.Length > 0 && ScriptDatabase.CanSee(contextId, record.ContextId) )
            {
                // The script-relative path without extension is what #using expects.
                string withoutExtension = System.IO.Path.ChangeExtension(record.RelativePath, null) ?? record.RelativePath;
                segments.Add(withoutExtension.Replace('/', '\\'));
            }
        }

        ImmutableArray<CompletionEntry>.Builder entries = ImmutableArray.CreateBuilder<CompletionEntry>();
        foreach ( string segment in segments.OrderBy(static s => s, StringComparer.Ordinal) )
        {
            entries.Add(new CompletionEntry(segment, CompletionKind.PathSegment, "script path"));
        }

        return entries.ToImmutable();
    }

    private ImmutableArray<CompletionEntry> LiteralCompletions(ParseResult result, string contextId, SymbolKind literalKind)
    {
        LanguageStore store = _database.StoreFor(result.Language);

        // String literals are content-exact; hash/istring names are already lowercase-canonical,
        // so an ordinal set dedups every kind correctly.
        HashSet<string> seen = new(StringComparer.Ordinal);
        ImmutableArray<CompletionEntry>.Builder entries = ImmutableArray.CreateBuilder<CompletionEntry>();

        // The current file's live literals first, then every visible record's.
        CollectLiterals(result.Extraction.References, literalKind, seen, entries);
        foreach ( ScriptRecord record in store.AllRecords )
        {
            if ( ScriptDatabase.CanSee(contextId, record.ContextId) )
            {
                CollectLiterals(record.References, literalKind, seen, entries);
            }
        }

        return entries.ToImmutable();
    }

    private static void CollectLiterals(
        ImmutableArray<ReferenceEntry> references,
        SymbolKind literalKind,
        HashSet<string> seen,
        ImmutableArray<CompletionEntry>.Builder entries)
    {
        string detail = LiteralDetail(literalKind);
        foreach ( ReferenceEntry entry in references )
        {
            if ( entry.Kind != ReferenceKind.Literal || entry.Key.Kind != literalKind )
            {
                continue;
            }

            if ( entry.Key.Name.Length > 0 && seen.Add(entry.Key.Name) )
            {
                entries.Add(new CompletionEntry(entry.Key.Name, CompletionKind.Literal, detail));
            }
        }
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

    private ImmutableArray<CompletionEntry> NamespaceFunctionCompletions(ParseResult result, string contextId, string ns)
    {
        LanguageStore store = _database.StoreFor(result.Language);
        ImmutableArray<CompletionEntry>.Builder entries = ImmutableArray.CreateBuilder<CompletionEntry>();

        foreach ( FunctionSymbol function in DatabaseQueries.FunctionsInNamespace(store, contextId, result.FilePath, ns) )
        {
            entries.Add(FunctionEntry(function));
        }

        return entries.ToImmutable();
    }

    private ImmutableArray<CompletionEntry> FieldCompletions(ParseResult result)
    {
        ImmutableArray<CompletionEntry>.Builder entries = ImmutableArray.CreateBuilder<CompletionEntry>();
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);

        // Fields assigned anywhere in the current file (owner-agnostic for now).
        foreach ( FunctionSymbol function in result.Extraction.Functions )
        {
            foreach ( AssignmentSymbol assignment in function.Assignments )
            {
                if ( assignment.OwnerName.Length > 0 && seen.Add(assignment.Name) )
                {
                    entries.Add(new CompletionEntry(assignment.Name, CompletionKind.Field, "field"));
                }
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

    private ImmutableArray<CompletionEntry> StatementScopeCompletions(ParseResult result, string contextId, Position position)
    {
        ImmutableArray<CompletionEntry>.Builder entries = ImmutableArray.CreateBuilder<CompletionEntry>();

        bool insideFunction = IsInsideFunctionBody(result, position);

        foreach ( string keyword in insideFunction ? GscKeywords.StatementKeywords : GscKeywords.TopLevelKeywords )
        {
            // Documented keywords/directives (isdefined, notify, #using, …) carry their PDF blurb.
            string documentation = KeywordDocs.Find(keyword) ?? "";
            entries.Add(new CompletionEntry(keyword, CompletionKind.Keyword, "", "", documentation));
        }

        if ( !insideFunction )
        {
            return entries.ToImmutable();
        }

        LanguageStore store = _database.StoreFor(result.Language);

        // Macros defined in this file.
        foreach ( GSCode.Parser.Preprocessing.MacroDefinition macro in result.Preprocessed.Macros.All )
        {
            if ( macro.SourceFile is null )
            {
                entries.Add(new CompletionEntry(macro.Name, CompletionKind.Macro, "macro"));
            }
        }

        // Functions in the file's own namespaces.
        HashSet<string> namespaces = new(StringComparer.Ordinal);
        foreach ( NamespaceSpan span in result.Extraction.Namespaces )
        {
            namespaces.Add(span.KeyName);
        }

        foreach ( string ns in namespaces )
        {
            foreach ( FunctionSymbol function in DatabaseQueries.FunctionsInNamespace(store, contextId, result.FilePath, ns) )
            {
                entries.Add(FunctionEntry(function));
            }
        }

        // Visible classes (for `new C()` and `C::`).
        foreach ( ClassSymbol classSymbol in DatabaseQueries.AllVisibleClasses(store, contextId) )
        {
            entries.Add(new CompletionEntry(classSymbol.Name, CompletionKind.Class, "class"));
        }

        // Namespace-less builtins.
        foreach ( BuiltinFunction builtin in _builtins.For(result.Language).All )
        {
            entries.Add(new CompletionEntry(builtin.Name, CompletionKind.Function, "builtin", builtin.Name + "($0)"));
        }

        return entries.ToImmutable();
    }

    private static CompletionEntry FunctionEntry(FunctionSymbol function)
    {
        string detail = function.Namespace.Length > 0 ? function.Namespace + "::" + function.Name : function.Name;
        return new CompletionEntry(function.Name, CompletionKind.Function, detail, function.Name + "($0)");
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
        foreach ( FunctionSymbol function in result.Extraction.Functions )
        {
            if ( function.FullRange.Contains(position) )
            {
                return true;
            }
        }

        return false;
    }
}
