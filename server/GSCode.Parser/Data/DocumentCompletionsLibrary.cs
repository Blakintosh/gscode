using GSCode.Parser.Lexical;
using GSCode.Parser.SPA;
using GSCode.Parser.SPA.Sense;
using OmniSharp.Extensions.JsonRpc;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using Serilog;
using System;
using System.Collections.Generic;
using System.Linq;
using GSCode.Parser.SA;

namespace GSCode.Parser.Data;

public sealed class DocumentCompletionsLibrary(DocumentTokensLibrary tokens, string languageId)
{
    // Replaces the old static bool EnableFunctionCompletionParameterFilling.
    // The server layer (ServerConfiguration) assigns this delegate.
    public static Func<bool> ParameterFillResolver { get; set; } = static () => true;

    /// <summary>
    /// Library of tokens to quickly lookup a token at a given position.
    /// </summary>
    public DocumentTokensLibrary Tokens { get; } = tokens;

    private readonly ScriptAnalyserData _scriptAnalyserData = new(languageId);

    // Provider for script definitions (locals/imported via #using). Set by Script after analysis.
    private Func<DefinitionsTable?>? _definitionsProvider;
    public void SetDefinitionsProvider(Func<DefinitionsTable?> provider) => _definitionsProvider = provider;

    // Provider for macros (from preprocessor). Set by Script after preprocessing.
    private Func<IEnumerable<MacroOutlineItem>>? _macroProvider;
    public void SetMacroProvider(Func<IEnumerable<MacroOutlineItem>> provider) => _macroProvider = provider;

    public CompletionList GetCompletionsFromPosition(Position position)
    {
        Token? tokenAtCaret = Tokens.Get(position);
        if (tokenAtCaret is null)
        {
            return [];
        }

        Token token = GetEffectiveTokenForContext(tokenAtCaret);

        // Suppress completions until we're inside a block (e.g., a function body).
        // This prevents showing items while typing "function abc" or "function abc()".
        if (!IsInsideAnyBlock(token))
        {
            return [];
        }

        CompletionContext context = AnalyseCompletionContext(token, position);
        List<CompletionItem> completions = new();

        switch (context.Type)
        {
            case CompletionContextType.GlobalScope:
                completions = GetGlobalScopeCompletions(context);
                break;
        }

        // Generate completions from identifiers that occur inside of the file, as well.
        completions.AddRange(GetFileScopeCompletions(context));

        // Add local variable completions (scoped to current block/function).
        completions.AddRange(GetLocalVariableCompletions(context, token, completions));

        // Include functions defined locally and imported via #using.
        completions.AddRange(GetImportedFunctionCompletions(context, completions));

        // Include macros defined in this file or inserted headers.
        completions.AddRange(GetMacroCompletions(context, completions));

        return new CompletionList(completions);
    }

    private static bool IsInsideAnyBlock(Token token)
    {
        // Walk to the first token
        Token first = token;
        while (first.Previous is not null)
        {
            first = first.Previous;
        }

        // Track brace nesting up to the caret token
        int braceDepth = 0;
        Token? cur = first;
        while (cur is not null)
        {
            if (cur.Type == TokenType.OpenBrace)
            {
                braceDepth++;
            }
            else if (cur.Type == TokenType.CloseBrace && braceDepth > 0)
            {
                braceDepth--;
            }

            if (ReferenceEquals(cur, token))
            {
                break;
            }
            cur = cur.Next;
        }

        return braceDepth > 0;
    }

    private static bool IsTriviaOrDelimiter(Token t)
        => t.IsWhitespacey() || t.IsComment() || t.Type is TokenType.LineBreak or TokenType.Semicolon or TokenType.Comma or TokenType.CloseParen or TokenType.CloseBracket or TokenType.CloseBrace;

    private static Token GetEffectiveTokenForContext(Token t)
    {
        Token cur = t;
        // If on trivia/delimiter, step left to previous non-trivia
        if (IsTriviaOrDelimiter(cur))
        {
            Token? prev = cur.Previous;
            while (prev is not null && IsTriviaOrDelimiter(prev)) prev = prev.Previous;
            if (prev is not null) cur = prev;
        }
        return cur;
    }

    private List<CompletionItem> GetGlobalScopeCompletions(CompletionContext context)
    {
        List<ScrFunctionDefinition> functions = _scriptAnalyserData.GetApiFunctions(context.Filter);

        List<CompletionItem> completions = new();

        Log.Information("Found {Count} functions in global scope", functions.Count);

        foreach (ScrFunctionDefinition function in functions)
        {
            completions.Add(CreateCompletionItem(function));
        }

        return completions;
    }

    private List<CompletionItem> GetFileScopeCompletions(CompletionContext context)
    {
        List<CompletionItem> completions = new();
        HashSet<string> seenIdentifiers = new(StringComparer.OrdinalIgnoreCase);

        // This will be replaced later, but will suffice as a temporary completions solution.

        // Add GSC/CSC keywords
        string[] keywords = {
            "class", "return", "wait", "thread", "classes", "if", "else", "do", "while",
            "for", "foreach", "in", "new", "waittill", "waittillmatch", "waittillframeend",
            "switch", "case", "default", "break", "continue", "notify", "endon",
            "waitrealtime", "profilestart", "profilestop", "isdefined",
            // Additional keywords
            "true", "false", "undefined", "self", "level", "game", "world", "vararg", "anim",
            "var", "const", "function", "private", "autoexec", "constructor", "destructor"
        };

        foreach (string keyword in keywords)
        {
            if (seenIdentifiers.Add(keyword))
            {
                completions.Add(new CompletionItem()
                {
                    Kind = CompletionItemKind.Keyword,
                    Label = keyword,
                    InsertText = keyword
                });
            }
        }

        // Add GSC directives (only if filter starts with #)
        if ((context.Filter ?? "").StartsWith("#"))
        {
            string[] directives = {
                "#using", "#insert", "#namespace", "#using_animtree", "#precache",
                "#define", "#if", "#elif", "#else", "#endif"
            };

            foreach (string directive in directives)
            {
                if (seenIdentifiers.Add(directive))
                {
                    completions.Add(new CompletionItem()
                    {
                        Kind = CompletionItemKind.Keyword,
                        Label = directive,
                        InsertText = directive
                    });
                }
            }
        }

        return completions;
    }

    // ---------------- Local variable completion ----------------
    private IEnumerable<CompletionItem> GetLocalVariableCompletions(CompletionContext context, Token caretToken, IEnumerable<CompletionItem> existing)
    {
        // Provide variable completions in expression contexts (global-like typing or function call argument lists)
        if (context.Type is not CompletionContextType.GlobalScope and not CompletionContextType.FunctionCall)
            return Array.Empty<CompletionItem>();

        HashSet<string> existingLabels = new(existing.Select(c => c.Label ?? string.Empty), StringComparer.OrdinalIgnoreCase);

        var funcInfo = FindEnclosingFunction(caretToken);
        if (funcInfo.openBrace is null)
            return Array.Empty<CompletionItem>();

        Token openBrace = funcInfo.openBrace;
        Token? closeBrace = funcInfo.closeBrace; // may be null if malformed

        string? filter = context.Filter; bool hasFilter = !string.IsNullOrWhiteSpace(filter);
        Dictionary<string, Token> variables = new(StringComparer.OrdinalIgnoreCase);

        // Seed with parameters first
        foreach (var p in funcInfo.parameters)
        {
            if (!string.IsNullOrWhiteSpace(p)) variables[p] = openBrace; // placeholder token
        }

        Token? cur = openBrace.Next;
        // Limit scan to within the block (up to matching close brace if found)
        while (cur is not null && !ReferenceEquals(cur, closeBrace))
        {
            if (!cur.IsComment())
            {
                if (IsKeywordLexeme(cur, "var") || IsKeywordLexeme(cur, "const"))
                {
                    Token? nextId = NextNonTrivia(cur);
                    if (nextId is not null && nextId.Type == TokenType.Identifier && !nextId.IsKeyword())
                    {
                        TryAddVariable(nextId.Lexeme, nextId);
                    }
                }
                else if (cur.Type == TokenType.Identifier && !cur.IsKeyword())
                {
                    Token? next = NextNonTrivia(cur);
                    if (next is not null && next.Lexeme == "=")
                    {
                        TryAddVariable(cur.Lexeme, cur);
                    }
                }
                if (IsKeywordLexeme(cur, "for") || IsKeywordLexeme(cur, "foreach"))
                {
                    Token? afterFor = NextNonTrivia(cur);
                    if (afterFor is not null && afterFor.Lexeme == "(")
                    {
                        Token? maybeVar = NextNonTrivia(afterFor);
                        if (maybeVar is not null && maybeVar.Type == TokenType.Identifier && !maybeVar.IsKeyword())
                        {
                            TryAddVariable(maybeVar.Lexeme, maybeVar);
                        }
                    }
                }
            }
            cur = cur.Next;
        }

        List<CompletionItem> items = new();
        foreach (var kv in variables)
        {
            string name = kv.Key;
            if (existingLabels.Contains(name)) continue;
            if (hasFilter && !name.StartsWith(filter!, StringComparison.OrdinalIgnoreCase)) continue;

            items.Add(new CompletionItem
            {
                Label = name,
                InsertText = name,
                Kind = CompletionItemKind.Variable,
                SortText = "0_" + name.ToLowerInvariant(),
                InsertTextFormat = InsertTextFormat.PlainText
            });
        }
        return items;

        void TryAddVariable(string? name, Token token)
        {
            if (string.IsNullOrWhiteSpace(name)) return;
            variables[name] = token; // prefer the latest occurrence (shadowing)
        }

        static bool IsKeywordLexeme(Token token, string keyword)
            => token.Type == TokenType.Identifier && string.Equals(token.Lexeme, keyword, StringComparison.OrdinalIgnoreCase);

        static Token? NextNonTrivia(Token start)
        {
            Token? n = start.Next;
            while (n is not null && (n.IsWhitespacey() || n.IsComment())) n = n.Next;
            return n;
        }
    }

    private static (Token? openBrace, Token? closeBrace, List<string> parameters) FindEnclosingFunction(Token caret)
    {
        // Walk backwards from caret to find the nearest 'function' keyword token.
        Token? cur = caret;
        while (cur is not null && cur.Type != TokenType.Function && cur.Type != TokenType.Sof)
        {
            cur = cur.Previous;
        }

        if (cur is null || cur.Type != TokenType.Function)
        {
            return (null, null, new List<string>());
        }

        Token functionToken = cur;

        static Token? NextNonTrivia(Token? t)
        {
            t = t?.Next;
            while (t is not null && (t.IsWhitespacey() || t.IsComment())) t = t.Next;
            return t;
        }

        // Function name token
        Token? nameTok = NextNonTrivia(functionToken);
        if (nameTok is null || nameTok.Type != TokenType.Identifier)
        {
            return (null, null, new List<string>());
        }

        // Expect '('
        Token? openParen = NextNonTrivia(nameTok);
        if (openParen is null || openParen.Type != TokenType.OpenParen)
        {
            return (null, null, new List<string>());
        }

        // Parse parameters until matching ')'
        List<string> parameters = new();
        int parenDepth = 1;
        Token? walker = openParen.Next;
        Token? closeParen = null;
        while (walker is not null)
        {
            if (walker.Type == TokenType.OpenParen) parenDepth++;
            else if (walker.Type == TokenType.CloseParen)
            {
                parenDepth--;
                if (parenDepth == 0)
                {
                    closeParen = walker;
                    break;
                }
            }
            else if (parenDepth == 1 && walker.Type == TokenType.Identifier && !walker.IsKeyword())
            {
                if (!parameters.Contains(walker.Lexeme, StringComparer.OrdinalIgnoreCase)) parameters.Add(walker.Lexeme);
            }
            walker = walker.Next;
        }

        if (closeParen is null)
        {
            return (null, null, new List<string>());
        }

        // Expect body open brace
        Token? openBrace = NextNonTrivia(closeParen);
        if (openBrace is null || openBrace.Type != TokenType.OpenBrace)
        {
            return (null, null, new List<string>());
        }

        // Find matching close brace
        int depth = 1;
        Token? bodyWalker = openBrace.Next;
        Token? closeBrace = null;
        while (bodyWalker is not null)
        {
            if (bodyWalker.Type == TokenType.OpenBrace) depth++;
            else if (bodyWalker.Type == TokenType.CloseBrace)
            {
                depth--;
                if (depth == 0)
                {
                    closeBrace = bodyWalker;
                    break;
                }
            }
            bodyWalker = bodyWalker.Next;
        }

        // Caret must be inside body: after openBrace start and (before closeBrace end if exists)
        bool caretAfterOpen = ComparePos(caret.Range.Start, openBrace.Range.Start) >= 0;
        bool caretBeforeClose = closeBrace is null || ComparePos(caret.Range.Start, closeBrace.Range.End) < 0;
        if (!(caretAfterOpen && caretBeforeClose))
        {
            return (null, null, new List<string>());
        }

        return (openBrace, closeBrace, parameters);

        static int ComparePos(Position a, Position b)
        {
            int ld = a.Line - b.Line;
            if (ld != 0) return ld;
            return a.Character - b.Character;
        }
    }

    private static CompletionItem CreateCompletionItem(ScrFunctionDefinition function)
    {
        bool fill = ParameterFillResolver();
        string insertText = function.Name;
        InsertTextFormat format = InsertTextFormat.PlainText;
        if (fill)
        {
            if (function.Overloads.First().Parameters.Count > 0)
            {
                List<string> paramSnippets = new();
                int tabIndex = 1;
                foreach (var param in function.Overloads.First().Parameters)
                {
                    if (param.Mandatory.GetValueOrDefault(false))
                    {
                        paramSnippets.Add($"${{{tabIndex}:{param.Name ?? $"param{tabIndex}"}}}");
                    }
                    else
                    {
                        // Optional parameter: bracketed
                        paramSnippets.Add($"${{{tabIndex}:[{param.Name ?? $"optionalParam{tabIndex}"}]}}".Replace("]}}", "]}"));
                    }
                    tabIndex++;
                }
                insertText += "(" + string.Join(", ", paramSnippets) + ")$0";
            }
            else
            {
                insertText += "()$0";
            }
            format = InsertTextFormat.Snippet;
        }
        return new CompletionItem()
        {
            Label = function.Name,
            Detail = function.Description,
            Documentation = new StringOrMarkupContent(new MarkupContent()
            {
                Kind = MarkupKind.Markdown,
                Value = function.Documentation
            }),
            InsertText = insertText,
            InsertTextFormat = format,
            Kind = CompletionItemKind.Function,
            // Add sorting information to keep API functions organized
            SortText = function.Name.ToLowerInvariant(),
            // Add commit characters to automatically complete when typing these
            CommitCharacters = new Container<string>(new[] { "(", ")", ";" })
        };
    }

    private static CompletionItem CreateLocalFunctionCompletionItem(string ns, string name, IReadOnlyList<string> parameters, bool hasVararg, string? doc, string? filePath, bool isLocal)
    {
        bool fill = ParameterFillResolver();
        string insertText = name;
        InsertTextFormat format = InsertTextFormat.PlainText;
        if (fill)
        {
            insertText += "(";
            List<string> paramSnippets = new();
            int tab = 1;
            foreach (var p in parameters)
            {
                string clean = StripDefault(p);
                bool optional = p?.Contains('=') == true;
                string piece = optional
                    ? $"${{{tab}:[{clean}]}}"
                    : $"${{{tab}:{clean}}}";
                paramSnippets.Add(piece);
                tab++;
            }
            insertText += string.Join(", ", paramSnippets) + ")$0";
            format = InsertTextFormat.Snippet;
        }

        string? detail = null;
        if (!isLocal)
        {
            detail = string.IsNullOrEmpty(ns) ? "Imported function" : $"Imported function ({ns})";
            if (!string.IsNullOrWhiteSpace(filePath))
            {
                try { detail += $" — {System.IO.Path.GetFileName(filePath)}"; } catch { }
            }
        }

        string? documentation = string.IsNullOrWhiteSpace(doc) ? null : doc;

        return new CompletionItem
        {
            Label = name,
            Detail = detail,
            Documentation = documentation is null ? null : new StringOrMarkupContent(new MarkupContent { Kind = MarkupKind.Markdown, Value = documentation }),
            InsertText = insertText,
            InsertTextFormat = format,
            Kind = CompletionItemKind.Function,
            SortText = name.ToLowerInvariant(),
            CommitCharacters = new Container<string>(new[] { "(", ")", ";" })
        };

        static string StripDefault(string? n)
        {
            if (string.IsNullOrWhiteSpace(n)) return string.Empty;
            int idx = n.IndexOf('=');
            return idx >= 0 ? n[..idx].Trim() : n.Trim();
        }
    }

    private List<CompletionItem> GetImportedFunctionCompletions(CompletionContext context, List<CompletionItem> existing)
    {
        DefinitionsTable? defs = _definitionsProvider?.Invoke();
        if (defs is null)
        {
            return [];
        }

        string currentNs = defs.CurrentNamespace ?? string.Empty;

        HashSet<string> existingLabels = new(existing.Select(c => c.Label ?? string.Empty), StringComparer.OrdinalIgnoreCase);

        string filter = context.Filter ?? string.Empty;
        bool hasFilter = !string.IsNullOrWhiteSpace(filter);

        var allParams = defs.GetAllFunctionParameters().ToList();
        var allLocs = defs.GetAllFunctionLocations().ToDictionary(k => k.Key, v => v.Value);
        var allVarargs = defs.GetAllFunctionVarargs().ToDictionary(k => k.Key, v => v.Value);
        var allDocs = defs.GetAllFunctionDocs().ToDictionary(k => k.Key, v => v.Value, new KeyComparer());

        List<CompletionItem> items = new();
        foreach (var kv in allParams)
        {
            var key = kv.Key; // (Namespace, Name)
            string ns = key.Namespace;
            string name = key.Name;
            string label = name;

            if (hasFilter)
            {
                if (!StartsWithIgnoreCase(name, filter) && !StartsWithIgnoreCase(ns + "::" + name, filter))
                {
                    continue;
                }
            }

            if (!existingLabels.Add(label))
            {
                continue;
            }

            string[] parameters = kv.Value ?? Array.Empty<string>();
            bool hasVararg = allVarargs.TryGetValue(key, out bool vv) && vv;
            string? doc = allDocs.TryGetValue(key, out var d) ? d : null;
            bool isLocal = string.Equals(ns, currentNs, StringComparison.OrdinalIgnoreCase);

            items.Add(CreateLocalFunctionCompletionItem(ns, name, parameters, hasVararg, doc, allLocs.TryGetValue(key, out var loc) ? loc.FilePath : null, isLocal));
        }

        foreach (var kv in defs.GetAllFunctionLocations())
        {
            var key = kv.Key;
            string ns = key.Namespace;
            string name = key.Name;
            string label = name;

            if (hasFilter)
            {
                if (!StartsWithIgnoreCase(name, filter) && !StartsWithIgnoreCase(ns + "::" + name, filter))
                {
                    continue;
                }
            }

            if (!existingLabels.Add(label))
            {
                continue;
            }

            bool hasVararg = allVarargs.TryGetValue(key, out bool vv2) && vv2;
            string? doc = allDocs.TryGetValue(key, out var d2) ? d2 : null;
            bool isLocal = string.Equals(ns, currentNs, StringComparison.OrdinalIgnoreCase);
            items.Add(CreateLocalFunctionCompletionItem(ns, name, Array.Empty<string>(), hasVararg, doc, kv.Value.FilePath, isLocal));
        }

        return items;

        static bool StartsWithIgnoreCase(string value, string prefix)
            => value?.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ?? false;
    }

    private IEnumerable<CompletionItem> GetMacroCompletions(CompletionContext context, IEnumerable<CompletionItem> existing)
    {
        var prov = _macroProvider;
        if (prov is null)
        {
            return Array.Empty<CompletionItem>();
        }

        string filter = context.Filter ?? string.Empty;
        bool hasFilter = !string.IsNullOrWhiteSpace(filter);

        HashSet<string> existingLabels = new(existing.Select(c => c.Label ?? string.Empty), StringComparer.OrdinalIgnoreCase);
        var macros = prov.Invoke();
        if (macros is null)
        {
            return Array.Empty<CompletionItem>();
        }

        List<CompletionItem> items = new();
        foreach (var m in macros)
        {
            string name = m.Name;
            if (string.IsNullOrWhiteSpace(name)) continue;
            if (hasFilter && !name.StartsWith(filter, StringComparison.OrdinalIgnoreCase)) continue;
            if (!existingLabels.Add(name)) continue;

            string? detail = string.IsNullOrEmpty(m.SourceDisplay) ? "#define" : m.SourceDisplay;

            items.Add(new CompletionItem
            {
                Label = name,
                InsertText = name,
                Kind = CompletionItemKind.Constant,
                Detail = detail,
                SortText = name.ToLowerInvariant(),
                InsertTextFormat = InsertTextFormat.PlainText
            });
        }
        return items;
    }

    private sealed class KeyComparer : IEqualityComparer<(string Namespace, string Name)>
    {
        public bool Equals((string Namespace, string Name) x, (string Namespace, string Name) y)
            => string.Equals(x.Namespace, y.Namespace, StringComparison.OrdinalIgnoreCase)
            && string.Equals(x.Name, y.Name, StringComparison.OrdinalIgnoreCase);
        public int GetHashCode((string Namespace, string Name) obj)
            => HashCode.Combine(obj.Namespace.ToLowerInvariant(), obj.Name.ToLowerInvariant());
    }

    private CompletionContext AnalyseCompletionContext(Token token, Position position)
    {
        var context = new CompletionContext { Position = position };

        TokenType currentType = token.Type;
        TokenType? previousType = token.Previous?.Type;
        TokenType? previousPreviousType = token.Previous?.Previous?.Type;

        // Namespaced function pattern: namespace::__|
        if (previousType == TokenType.ScopeResolution && previousPreviousType == TokenType.Identifier)
        {
            context.Type = CompletionContextType.FunctionCall;
            context.Namespace = token.Previous!.Previous!.Lexeme;
        }
        // Default to global scope when on identifiers or after whitespace
        else if (currentType == TokenType.Identifier || previousType == TokenType.Whitespace)
        {
            context.Type = CompletionContextType.GlobalScope;
        }

        // If current token is an identifier, use it as filter
        if (currentType == TokenType.Identifier)
        {
            context.Filter = token.Lexeme;
        }
        else if (previousType == TokenType.Identifier)
        {
            // Caret after identifier but on punctuation (e.g., EOL/;), use previous identifier as filter
            context.Filter = token.Previous!.Lexeme;
        }

        return context;
    }
}


public record struct CompletionContext
{
    public CompletionContextType Type { get; set; }
    public string? Namespace { get; set; }
    public string? Filter { get; set; }
    public string? ObjectType { get; set; }
    public Position Position { get; set; }
}

public enum CompletionContextType
{
    None,
    FunctionCall,
    MemberAccess,
    GlobalScope,
    TypeName
}