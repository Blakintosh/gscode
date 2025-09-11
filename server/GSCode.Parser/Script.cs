using GSCode.Data;
using GSCode.Parser.AST;
using GSCode.Parser.Data;
using GSCode.Parser.Lexical;
using GSCode.Parser.Pre;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using OmniSharp.Extensions.LanguageServer.Protocol;
using OmniSharp.Extensions.LanguageServer.Protocol.Document;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using GSCode.Parser.SA;
using GSCode.Parser.Misc;
using System.IO;
using GSCode.Parser.SPA;
using System.Text.RegularExpressions;
using GSCode.Parser.DFA;
using System.Runtime.CompilerServices;

namespace GSCode.Parser;

using SymbolKindSA = GSCode.Parser.SA.SymbolKind;

public class Script(DocumentUri ScriptUri, string languageId)
{
    public bool Failed { get; private set; } = false;
    public bool Parsed { get; private set; } = false;
    public bool Analysed { get; private set; } = false;

    internal ParserIntelliSense Sense { get; private set; } = default!;

    public string LanguageId { get; } = languageId;

    private Task? ParsingTask { get; set; } = null;

    private ScriptNode? RootNode { get; set; } = null;

    public DefinitionsTable? DefinitionsTable { get; private set; } = default;

    public IEnumerable<Uri> Dependencies => DefinitionsTable?.Dependencies ?? [];

    // Expose macro outlines for outliner without exposing Sense outside assembly
    public IReadOnlyList<MacroOutlineItem> MacroOutlines => Sense == null ? Array.Empty<MacroOutlineItem>() : (IReadOnlyList<MacroOutlineItem>)Sense.MacroOutlines;

    // Reference index: map from symbol key to all ranges in this file
    private readonly Dictionary<SymbolKey, List<Range>> _references = new();
    public IReadOnlyDictionary<SymbolKey, List<Range>> References => _references;

    // Cache for language API to avoid repeated construction in hot paths
    private ScriptAnalyserData? _api;
    private ScriptAnalyserData? TryGetApi()
    {
        if (_api is not null) return _api;
        try { _api = new(LanguageId); } catch { _api = null; }
        return _api;
    }
    private bool IsBuiltinFunction(string name)
    {
        var api = TryGetApi();
        return api is not null && api.GetApiFunction(name) is not null;
    }

    public async Task ParseAsync(string documentText)
    {
        ParsingTask = DoParseAsync(documentText);
        await ParsingTask;
    }

    public Task DoParseAsync(string documentText)
    {
        Token startToken;
        Token endToken;
        try
        {
            // Transform the document text into a token sequence
            Lexer lexer = new(documentText.AsSpan());
            (startToken, endToken) = lexer.Transform();
        }
        catch (Exception ex)
        {
            // Failed to parse the script
            Failed = true;
            Console.Error.WriteLine($"Failed to tokenise script: {ex.Message}");

            // Create a dummy IntelliSense container so we can provide an error to the IDE.
            Sense = new(0, ScriptUri, LanguageId);
            Sense.AddIdeDiagnostic(RangeHelper.From(0, 0, 0, 1), GSCErrorCodes.UnhandledLexError, ex.GetType().Name);

            return Task.CompletedTask;
        }

        ParserIntelliSense sense = Sense = new(endLine: endToken.Range.End.Line, ScriptUri, LanguageId);

        // Preprocess the tokens.
        Preprocessor preprocessor = new(startToken, sense);
        try
        {
            preprocessor.Process();
        }
        catch (Exception ex)
        {
            Failed = true;
            Console.Error.WriteLine($"Failed to preprocess script: {ex.Message}");

            Sense.AddIdeDiagnostic(RangeHelper.From(0, 0, 0, 1), GSCErrorCodes.UnhandledMacError, ex.GetType().Name);
            return Task.CompletedTask;
        }

        // Build a library of tokens so IntelliSense can quickly lookup a token at a given position.
        Sense.CommitTokens(startToken);

        // Build the AST.
        AST.Parser parser = new(startToken, sense, LanguageId);

        try
        {
            RootNode = parser.Parse();
        }
        catch (Exception ex)
        {
            Failed = true;
            Console.Error.WriteLine($"Failed to AST-gen script: {ex.Message}");

            Sense.AddIdeDiagnostic(RangeHelper.From(0, 0, 0, 1), GSCErrorCodes.UnhandledAstError, ex.GetType().Name);
            return Task.CompletedTask;
        }

        // Gather signatures for all functions and classes.
        string initialNamespace = Path.GetFileNameWithoutExtension(ScriptUri.ToUri().LocalPath);
        DefinitionsTable = new(initialNamespace);

        SignatureAnalyser signatureAnalyser = new(RootNode, DefinitionsTable, Sense);
        try
        {
            signatureAnalyser.Analyse();
        }
        catch (Exception ex)
        {
            Failed = true;
            Console.Error.WriteLine($"Failed to signature analyse script: {ex.Message}");

            Sense.AddIdeDiagnostic(RangeHelper.From(0, 0, 0, 1), GSCErrorCodes.UnhandledSaError, ex.GetType().Name);
            return Task.CompletedTask;
        }

        // Analyze folding ranges from the token stream
        UserRegionsAnalyser foldingRangeAnalyser = new(startToken, Sense);
        try
        {
            foldingRangeAnalyser.Analyse();
        }
        catch (Exception ex)
        {
            Failed = true;
            Console.Error.WriteLine($"Failed to analyse folding ranges: {ex.Message}");

            Sense.AddIdeDiagnostic(RangeHelper.From(0, 0, 0, 1), GSCErrorCodes.UnhandledSaError, ex.GetType().Name);
            return Task.CompletedTask;
        }

        // Build references index from token stream
        BuildReferenceIndex();


        Parsed = true;
        return Task.CompletedTask;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Token? PreviousNonTrivia(Token? token)
    {
        Token? t = token?.Previous;
        while (t is not null && (t.IsWhitespacey() || t.IsComment()))
        {
            t = t.Previous;
        }
        return t;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsAddressOfIdentifier(Token identifier)
    {
        // identifier may be part of ns::name; find left-most identifier
        Token leftMost = identifier;
        if (identifier.Previous is { Type: TokenType.ScopeResolution } scope && scope.Previous is { Type: TokenType.Identifier } ns)
        {
            leftMost = ns;
        }
        Token? prev = PreviousNonTrivia(leftMost);
        return prev is not null && prev.Type == TokenType.BitAnd;
    }

    private static string NormalizeDocComment(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return string.Empty;
        string s = raw.Trim();
        // Strip block wrappers /@ @/ or /* */
        if (s.StartsWith("/@"))
        {
            if (s.EndsWith("@/")) s = s.Substring(2, s.Length - 4);
            else s = s.Substring(2);
        }
        else if (s.StartsWith("/*"))
        {
            if (s.EndsWith("*/")) s = s.Substring(2, s.Length - 4);
            else s = s.Substring(2);
        }
        // Normalize lines: remove leading * and surrounding quotes
        var lines = s.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        if (lines.Length == 1)
        {
            string l = lines[0].Trim();
            if (l.StartsWith("*")) l = l.TrimStart('*').TrimStart();
            if (l.Length >= 2 && l[0] == '"' && l[^1] == '"') l = l.Substring(1, l.Length - 2);
            return l.Length == 0 ? string.Empty : l;
        }
        List<string> cleaned = new();
        foreach (var line in lines)
        {
            string l = line.Trim();
            if (l.StartsWith("*")) l = l.TrimStart('*').TrimStart();
            // Remove starting and ending quotes if present
            if (l.Length >= 2 && l[0] == '"' && l[^1] == '"')
            {
                l = l.Substring(1, l.Length - 2);
            }
            if (l.Length == 0) continue;
            cleaned.Add(l);
        }
        return string.Join("\n", cleaned);
    }

    private void BuildReferenceIndex()
    {
        _references.Clear();
        var api = TryGetApi();
        foreach (var token in Sense.Tokens.GetAll())
        {
            if (token.Type != TokenType.Identifier) continue;

            // Recognize definition identifiers
            if (token.SenseDefinition is ScrFunctionSymbol)
            {
                var defNamespace = DefinitionsTable?.CurrentNamespace ?? Path.GetFileNameWithoutExtension(ScriptUri.ToUri().LocalPath);
                AddRef(new SymbolKey(SymbolKindSA.Function, defNamespace, token.Lexeme), token.Range);
                continue;
            }
            if (token.SenseDefinition is ScrClassSymbol)
            {
                var defNamespace = DefinitionsTable?.CurrentNamespace ?? Path.GetFileNameWithoutExtension(ScriptUri.ToUri().LocalPath);
                AddRef(new SymbolKey(SymbolKindSA.Class, defNamespace, token.Lexeme), token.Range);
                continue;
            }

            // Recognize call-site or qualified references, or address-of '&name' / '&ns::name'
            Token? next = token.Next;
            while (next is not null && next.IsWhitespacey()) next = next.Next;
            bool looksLikeCall = next is not null && next.Type == TokenType.OpenParen;
            bool isQualified = token.Previous is not null && token.Previous.Type == TokenType.ScopeResolution && token.Previous.Previous is not null && token.Previous.Previous.Type == TokenType.Identifier;
            bool isAddressOf = IsAddressOfIdentifier(token);
            if (!looksLikeCall && !isQualified && !isAddressOf) continue;

            var (qual, name) = ParseNamespaceQualifiedIdentifier(token);

            // Skip builtin
            if (api is not null)
            {
                try { if (api.GetApiFunction(name) is not null) continue; } catch { }
            }

            // Resolve to a namespace
            string resolvedNamespace = qual ?? (DefinitionsTable?.CurrentNamespace ?? Path.GetFileNameWithoutExtension(ScriptUri.ToUri().LocalPath));
            // Index as function reference for now (method support can be added later)
            AddRef(new SymbolKey(SymbolKindSA.Function, resolvedNamespace, name), token.Range);
        }

        void AddRef(SymbolKey key, Range range)
        {
            if (!_references.TryGetValue(key, out var list))
            {
                list = new List<Range>();
                _references[key] = list;
            }
            list.Add(range);
        }
    }

    public async Task<string?> GetEnclosingFunctionScopeIdAsync(Position position, CancellationToken cancellationToken = default)
    {
        await WaitUntilParsedAsync(cancellationToken);
        if (RootNode is null) return null;

        foreach (var fn in EnumerateFunctions(RootNode))
        {
            if (fn.Name is not Token nameTok) continue;
            var bodyRange = GetStmtListRange(fn.Body);
            if (IsPositionInsideRange(position, bodyRange))
            {
                string ns = DefinitionsTable?.CurrentNamespace ?? Path.GetFileNameWithoutExtension(ScriptUri.ToUri().LocalPath);
                return $"{ns}::{nameTok.Lexeme}";
            }
        }
        return null;
    }

    private static Range GetStmtListRange(StmtListNode body)
    {
        if (body.Statements.Count == 0)
        {
            return RangeHelper.From(0, 0, 0, 0);
        }
        Range? start = null;
        Range? end = null;
        foreach (var st in body.Statements)
        {
            if (TryGetRange(st, out var r))
            {
                if (start is null) start = r;
                end = r;
            }
        }
        if (start is null || end is null)
        {
            return RangeHelper.From(0, 0, 0, 0);
        }
        return RangeHelper.From(start!.Start, end!.End);
    }

    private static bool IsPositionInsideRange(Position pos, Range range)
    {
        int cmpStart = ComparePosition(pos, range.Start);
        int cmpEnd = ComparePosition(range.End, pos);
        return cmpStart >= 0 && cmpEnd >= 0;
    }

    private static int ComparePosition(Position a, Position b)
    {
        if (a.Line != b.Line) return a.Line.CompareTo(b.Line);
        return a.Character.CompareTo(b.Character);
    }

    private static bool TryGetRange(AstNode node, out Range range)
    {
        // Fast paths for nodes that carry a Range
        switch (node)
        {
            case ExprNode e:
                range = e.Range; return true;
            case ControlFlowActionNode cfan:
                range = cfan.Range; return true;
            case ConstStmtNode cst:
                range = cst.Range; return true;
            case ExprStmtNode es when es.Expr is not null:
                range = es.Expr.Range; return true;
            case ReturnStmtNode rt when rt.Value is not null:
                range = rt.Value.Range; return true;
            case ArgsListNode al:
                range = al.Range; return true;
        }

        // Composite statements: compute union of child ranges
        bool ok = false;
        Range start = default, end = default;
        void Acc(Range r)
        {
            if (!ok) { start = r; end = r; ok = true; }
            else { start = RangeHelper.From(start.Start, r.Start); end = RangeHelper.From(end.Start, r.End); }
        }

        switch (node)
        {
            case IfStmtNode iff:
                if (iff.Condition is not null && TryGetRange(iff.Condition, out var rc)) Acc(rc);
                if (iff.Then is not null && TryGetRange(iff.Then, out var rt)) Acc(rt);
                if (iff.Else is not null && TryGetRange(iff.Else, out var re)) Acc(re);
                break;
            case DoWhileStmtNode dw:
                if (dw.Then is not null && TryGetRange(dw.Then, out var rdw)) Acc(rdw);
                if (dw.Condition is not null && TryGetRange(dw.Condition, out var cdw)) Acc(cdw);
                break;
            case WhileStmtNode wl:
                if (wl.Then is not null && TryGetRange(wl.Then, out var rwl)) Acc(rwl);
                if (wl.Condition is not null && TryGetRange(wl.Condition, out var cwl)) Acc(cwl);
                break;
            case ForStmtNode fr:
                if (fr.Init is not null && TryGetRange(fr.Init, out var ri)) Acc(ri);
                if (fr.Condition is not null && TryGetRange(fr.Condition, out var rc2)) Acc(rc2);
                if (fr.Increment is not null && TryGetRange(fr.Increment, out var rinc)) Acc(rinc);
                if (fr.Then is not null && TryGetRange(fr.Then, out var rthen)) Acc(rthen);
                break;
            case ForeachStmtNode fe:
                if (fe.Collection is not null && TryGetRange(fe.Collection, out var rcol)) Acc(rcol);
                if (fe.Then is not null && TryGetRange(fe.Then, out var rfe)) Acc(rfe);
                break;
            case FunDevBlockNode fdb:
                var rbody = GetStmtListRange(fdb.Body); Acc(rbody);
                break;
            case StmtListNode sl:
                foreach (var st in sl.Statements)
                {
                    if (TryGetRange(st, out var rs)) Acc(rs);
                }
                break;
            case SwitchStmtNode sw:
                if (sw.Expression is not null && TryGetRange(sw.Expression, out var rexp)) Acc(rexp);
                if (TryGetRange(sw.Cases, out var rcases)) Acc(rcases);
                break;
            case CaseListNode cl:
                foreach (var cs in cl.Cases)
                {
                    if (TryGetRange(cs, out var rcs)) Acc(rcs);
                }
                break;
            case CaseStmtNode cs:
                foreach (var lbl in cs.Labels)
                {
                    if (TryGetRange(lbl, out var rlbl)) Acc(rlbl);
                }
                var rb = GetStmtListRange(cs.Body); Acc(rb);
                break;
            case CaseLabelNode cln when cln.Value is not null:
                range = cln.Value.Range; return true;
            case ClassBodyListNode cbl:
                foreach (var d in cbl.Definitions)
                {
                    if (TryGetRange(d, out var rd)) Acc(rd);
                }
                break;
        }

        if (ok)
        {
            range = RangeHelper.From(start.Start, end.End);
            return true;
        }
        range = default;
        return false;
    }
    public async Task AnalyseAsync(IEnumerable<IExportedSymbol> exportedSymbols, CancellationToken cancellationToken = default)
    {
        await WaitUntilParsedAsync(cancellationToken);

        // Basic SPA diagnostics
        try
        {
            EmitUnusedParameterDiagnostics();
            EmitCallArityDiagnostics();
            EmitUnknownNamespaceDiagnostics();
            EmitUnusedUsingDiagnostics();
            EmitUnusedVariableDiagnostics();
            EmitSwitchCaseDiagnostics();
            EmitAssignOnThreadDiagnostics();
        }
        catch (Exception ex)
        {
            // Do not fail analysis entirely; surface as SPA failure
            Sense.AddIdeDiagnostic(RangeHelper.From(0, 0, 0, 1), GSCErrorCodes.UnhandledSpaError, ex.GetType().Name);
        }

        Analysed = true;
    }

    public async Task<List<Diagnostic>> GetDiagnosticsAsync(CancellationToken cancellationToken)
    {
        // TODO: maybe a mechanism to check if analysed if that's a requirement

        // We still expose diagnostics even if the script failed to parse
        await WaitUntilParsedAsync(cancellationToken);
        return Sense.Diagnostics;
    }

    public async Task PushSemanticTokensAsync(SemanticTokensBuilder builder, CancellationToken cancellationToken)
    {
        await WaitUntilParsedAsync(cancellationToken);

        foreach (ISemanticToken token in Sense.SemanticTokens)
        {
            builder.Push(token.Range, token.SemanticTokenType, token.SemanticTokenModifiers);
        }
    }

    public async Task<Hover?> GetHoverAsync(Position position, CancellationToken cancellationToken)
    {
        await WaitUntilParsedAsync(cancellationToken);

        IHoverable? result = Sense.HoverLibrary.Get(position);
        if (result is not null)
        {
            return result.GetHover();
        }

        Token? token = Sense.Tokens.Get(position);
        if (token is null)
        {
            return null;
        }

        // If cursor is inside a call's argument list, synthesize a signature-like hover with current parameter highlighted
        if (TryGetCallInfo(token, out Token idToken, out int activeParam))
        {
            var (q, funcName) = ParseNamespaceQualifiedIdentifier(idToken);
            string? md = BuildSignatureMarkdown(funcName, q, activeParam);
            if (md is not null)
            {
                return new Hover
                {
                    Range = idToken.Range,
                    Contents = new MarkedStringsOrMarkupContent(new MarkupContent
                    {
                        Kind = MarkupKind.Markdown,
                        Value = md
                    })
                };
            }
        }

        // No precomputed hover — try to synthesize one for local/external (non-builtin) function identifiers
        // Only for identifiers (namespace::name or plain name)
        if (token.Type != TokenType.Identifier)
        {
            return null;
        }

        var (qualifier, name) = ParseNamespaceQualifiedIdentifier(token);

        // Exclude builtin API functions
        if (IsBuiltinFunction(name))
        {
            return null; // let existing hover (if any) handle API
        }

        // Find function/method in current script tables
        string ns = qualifier ?? (DefinitionsTable?.CurrentNamespace ?? Path.GetFileNameWithoutExtension(ScriptUri.ToUri().LocalPath));
        string? doc = DefinitionsTable?.GetFunctionDoc(ns, name);
        string[]? parameters = DefinitionsTable?.GetFunctionParameters(ns, name);

        if (doc is null && parameters is null)
        {
            // Try any namespace in this file
            var any = DefinitionsTable?.GetFunctionLocationAnyNamespace(name);
            if (any is not null)
            {
                // unknown params/doc; still show a basic prototype
                string proto = $"function {name}()";
                return new Hover
                {
                    Range = token.Range,
                    Contents = new MarkedStringsOrMarkupContent(new MarkupContent
                    {
                        Kind = MarkupKind.Markdown,
                        Value = $"```gsc\n{proto}\n```"
                    })
                };
            }
            return null;
        }

        string[] cleanParams = parameters is null ? Array.Empty<string>() : parameters.Select(StripDefault).ToArray();
        string protoWithParams = cleanParams.Length == 0
            ? $"function {name}()"
            : $"function {name}({string.Join(", ", cleanParams)})";

        string formattedDoc = doc is not null ? NormalizeDocComment(doc) : string.Empty;
        string value = string.IsNullOrEmpty(formattedDoc)
            ? $"```gsc\n{protoWithParams}\n```"
            : $"```gsc\n{protoWithParams}\n```\n---\n{formattedDoc}";

        return new Hover
        {
            Range = token.Range,
            Contents = new MarkedStringsOrMarkupContent(new MarkupContent
            {
                Kind = MarkupKind.Markdown,
                Value = value
            })
        };
    }

    private string? BuildSignatureMarkdown(string name, string? qualifier, int activeParam)
    {
        // Built-in API: try first
        var api = TryGetApi();
        if (api is not null)
        {
            try
            {
                var apiFn = api.GetApiFunction(name);
                if (apiFn is not null)
                {
                    var overload = apiFn.Overloads.FirstOrDefault();
                    var paramSeq = overload != null ? overload.Parameters : new List<GSCode.Parser.SPA.Sense.ScrFunctionParameter>();
                    string[] names = paramSeq.Select(p => StripDefault(p.Name)).ToArray();
                    string sig = FormatSignature(name, names, activeParam, qualifier);
                    string desc = apiFn.Description ?? string.Empty;
                    return string.IsNullOrEmpty(desc) ? sig : $"{sig}\n---\n{desc}";
                }
            }
            catch { }
        }

        // Script-defined (local or imported)
        string ns = qualifier ?? (DefinitionsTable?.CurrentNamespace ?? Path.GetFileNameWithoutExtension(ScriptUri.ToUri().LocalPath));
        string[]? parms = DefinitionsTable?.GetFunctionParameters(ns, name);
        string? doc = DefinitionsTable?.GetFunctionDoc(ns, name);
        if (parms is not null)
        {
            string sig = FormatSignature(name, parms.Select(StripDefault).ToArray(), activeParam, qualifier);
            string formattedDoc = doc is not null ? NormalizeDocComment(doc) : string.Empty;
            return string.IsNullOrEmpty(formattedDoc) ? sig : $"{sig}\n---\n{formattedDoc}";
        }

        // Fallback: show empty params signature if symbol exists somewhere
        var any = DefinitionsTable?.GetFunctionLocationAnyNamespace(name);
        if (any is not null)
        {
            return FormatSignature(name, Array.Empty<string>(), activeParam, qualifier);
        }
        return null;
    }

    private static string FormatSignature(string name, IReadOnlyList<string> parameters, int activeParam, string? qualifier)
    {
        string nsPrefix = string.IsNullOrEmpty(qualifier) ? string.Empty : qualifier + "::";
        if (parameters.Count == 0)
        {
            return $"```gsc\nfunction {nsPrefix}{name}()\n```";
        }
        var sb = new StringBuilder();
        sb.Append("```gsc\nfunction ").Append(nsPrefix).Append(name).Append('(');
        for (int i = 0; i < parameters.Count; i++)
        {
            if (i > 0) sb.Append(", ");
            string p = StripDefault(parameters[i]);
            if (i == activeParam)
            {
                sb.Append('<').Append(p).Append('>');
            }
            else
            {
                sb.Append(p);
            }
        }
        sb.Append(")\n```");
        return sb.ToString();
    }

    private static bool TryGetCallInfo(Token token, out Token idToken, out int activeParam)
    {
        idToken = default!;
        activeParam = 0;

        // Find the nearest '(' that starts the current argument list
        Token? cursor = token;
        int parenDepth = 0;
        while (cursor is not null)
        {
            if (cursor.Type == TokenType.CloseParen) parenDepth++;
            if (cursor.Type == TokenType.OpenParen)
            {
                if (parenDepth == 0) break;
                parenDepth--;
            }
            cursor = cursor.Previous;
        }
        if (cursor is null)
            return false; // not in a call

        // The identifier before this '('
        Token? id = cursor.Previous;
        while (id is not null && (id.IsWhitespacey() || id.IsComment())) id = id.Previous;
        if (id is null || id.Type != TokenType.Identifier)
            return false;

        idToken = id;

        // Count commas to determine parameter index; ignore nested parens/brackets/braces
        Token? walker = cursor.Next;
        int depthParen = 0, depthBracket = 0, depthBrace = 0;
        int index = 0;
        while (walker is not null && walker != token.Next)
        {
            if (walker.Type == TokenType.OpenParen) depthParen++;
            else if (walker.Type == TokenType.CloseParen)
            {
                if (depthParen == 0) break;
                depthParen--;
            }
            else if (walker.Type == TokenType.OpenBracket) depthBracket++;
            else if (walker.Type == TokenType.CloseBracket && depthBracket > 0) depthBracket--;
            else if (walker.Type == TokenType.OpenBrace) depthBrace++;
            else if (walker.Type == TokenType.CloseBrace && depthBrace > 0) depthBrace--;
            else if (walker.Type == TokenType.Comma && depthParen == 0 && depthBracket == 0 && depthBrace == 0)
            {
                index++;
            }
            walker = walker.Next;
        }
        activeParam = index;
        return true;
    }

    public async Task<CompletionList?> GetCompletionAsync(Position position, CancellationToken cancellationToken)
    {
        await WaitUntilParsedAsync(cancellationToken);
        return Sense.Completions.GetCompletionsFromPosition(position);
    }

    public async Task<IEnumerable<FoldingRange>> GetFoldingRangesAsync(CancellationToken cancellationToken = default)
    {
        await WaitUntilParsedAsync(cancellationToken);
        return Sense.FoldingRanges;
    }

    private async Task WaitUntilParsedAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (ParsingTask is null)
        {
            throw new InvalidOperationException("The script has not been parsed yet.");
        }
        await ParsingTask;
        cancellationToken.ThrowIfCancellationRequested();
    }

    public async Task<IEnumerable<IExportedSymbol>> IssueExportedSymbolsAsync(CancellationToken cancellationToken = default)
    {
        await WaitUntilParsedAsync(cancellationToken);

        return DefinitionsTable!.ExportedFunctions ?? [];
    }

    /// <summary>
    /// Helper to parse namespace-qualified identifiers: namespace::name or just name.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static (string? qualifier, string name) ParseNamespaceQualifiedIdentifier(Token token)
    {
        // If the previous token is '::' and the one before is an identifier, treat as namespace::name
        if (token.Previous is { Lexeme: "::" } sep && sep.Previous is { Type: TokenType.Identifier } nsToken)
        {
            return (nsToken.Lexeme, token.Lexeme);
        }
        // Otherwise, no qualifier
        return (null, token.Lexeme);
    }

    private static string NormalizeFilePathForUri(string filePath)
    {
        if (string.IsNullOrEmpty(filePath)) return filePath;

        // Some paths are produced like "/g:/path/..." on Windows; remove leading slash if followed by drive letter
        if (filePath.Length >= 3 && filePath[0] == '/' && char.IsLetter(filePath[1]) && filePath[2] == ':')
        {
            filePath = filePath.Substring(1);
        }

        // Convert forward slashes to platform directory separator to be safe
        if (Path.DirectorySeparatorChar == '\\')
        {
            filePath = filePath.Replace('/', Path.DirectorySeparatorChar);
        }

        // Return full path if possible
        try
        {
            return Path.GetFullPath(filePath);
        }
        catch
        {
            return filePath;
        }
    }

    public async Task<Location?> GetDefinitionAsync(Position position, CancellationToken cancellationToken = default)
    {
        await WaitUntilParsedAsync(cancellationToken);

        // First, allow preprocessor macro definitions/usages to resolve even if the original macro token was removed
        IHoverable? hoverable = Sense.HoverLibrary.Get(position);
        if (hoverable is Pre.MacroDefinition macroDef && !macroDef.IsFromPreprocessor)
        {
            string normalized = NormalizeFilePathForUri(ScriptUri.ToUri().LocalPath);
            return new Location() { Uri = new Uri(normalized), Range = macroDef.Range };
        }
        if (hoverable is Pre.ScriptMacro scriptMacro)
        {
            string normalized = NormalizeFilePathForUri(ScriptUri.ToUri().LocalPath);
            return new Location() { Uri = new Uri(normalized), Range = scriptMacro.DefineSource.Range };
        }

        Token? token = Sense.Tokens.Get(position);
        if (token is null)
            return null;

        // If the token has an IntelliSense definition pointing at a dependency, return that file location.
        if (token.SenseDefinition is ScrDependencySymbol dep)
        {
            string resolvedPath = dep.Path;
            if (!File.Exists(resolvedPath))
                return null;
            string normalized = NormalizeFilePathForUri(resolvedPath);
            var targetUri = new Uri(normalized);
            // Navigate to start of file in the target document
            return new Location() { Uri = targetUri, Range = RangeHelper.From(0, 0, 0, 0) };
        }

        if (IsOnUsingLine(token, out string? usingPath, out Range? usingRange))
        {
            string? resolved = Sense.GetDependencyPath(usingPath!, usingRange!);
            if (resolved is not null && File.Exists(resolved))
            {
                string normalized = NormalizeFilePathForUri(resolved);
                var targetUri = new Uri(normalized);
                return new Location() { Uri = targetUri, Range = RangeHelper.From(0, 0, 0, 0) };
            }
        }

        // If on an #insert line and we recorded a hover for it, go to the inserted file
        IHoverable? h = Sense.HoverLibrary.Get(position);
        if (h is Pre.InsertDirectiveHover ih)
        {
            string? resolved = Sense.ResolveInsertPath(ih.RawPath, ih.Range);
            if (resolved is not null && File.Exists(resolved))
            {
                string normalized = NormalizeFilePathForUri(resolved);
                var targetUri = new Uri(normalized);
                return new Location() { Uri = targetUri, Range = RangeHelper.From(0, 0, 0, 0) };
            }
        }

        // Ensure the token is a function-like identifier before attempting Go-to-Definition.
        if (token.Type != TokenType.Identifier)
        {
            return null;
        }
        // Helper: get next non-whitespace/comment token
        Token? nextNonWs = token.Next;
        while (nextNonWs is not null && nextNonWs.IsWhitespacey())
        {
            nextNonWs = nextNonWs.Next;
        }
        bool looksLikeCall = nextNonWs is not null && nextNonWs.Type == TokenType.OpenParen;
        bool isQualified = token.Previous is not null && token.Previous.Type == TokenType.ScopeResolution && token.Previous.Previous is not null && token.Previous.Previous.Type == TokenType.Identifier;
        bool hasDefinitionSymbol = token.SenseDefinition is ScrFunctionSymbol || token.SenseDefinition is ScrMethodSymbol || token.SenseDefinition is ScrClassSymbol;
        bool isAddressOf = IsAddressOfIdentifier(token);
        if (!looksLikeCall && !isQualified && !hasDefinitionSymbol && !isAddressOf)
        {
            return null;
        }
        var (qualifier, name) = ParseNamespaceQualifiedIdentifier(token);
        if (IsBuiltinFunction(name))
        {
            return null;
        }
        if (qualifier is not null && DefinitionsTable is not null)
        {
            var loc = DefinitionsTable.GetFunctionLocation(qualifier, name)
                   ?? DefinitionsTable.GetClassLocation(qualifier, name);
            if (loc is not null)
            {
                string normalized = NormalizeFilePathForUri(loc.Value.FilePath);
                var targetUri = new Uri(normalized); return new Location() { Uri = targetUri, Range = loc.Value.Range };
            }
        }
        string ns = DefinitionsTable?.CurrentNamespace ?? Path.GetFileNameWithoutExtension(ScriptUri.ToUri().LocalPath);
        var localLoc = DefinitionsTable?.GetFunctionLocation(ns, name)
                    ?? DefinitionsTable?.GetClassLocation(ns, name);
        if (localLoc is not null)
        {
            string normalized = NormalizeFilePathForUri(localLoc.Value.FilePath);
            var targetUri = new Uri(normalized);
            return new Location() { Uri = targetUri, Range = localLoc.Value.Range };
        }
        var anyLoc = DefinitionsTable?.GetFunctionLocationAnyNamespace(name)
                  ?? DefinitionsTable?.GetClassLocationAnyNamespace(name);
        if (anyLoc is not null)
        {
            string normalized = NormalizeFilePathForUri(anyLoc.Value.FilePath);
            var targetUri = new Uri(normalized);
            return new Location() { Uri = targetUri, Range = anyLoc.Value.Range };
        }
        return null;
    }

    private static bool IsOnUsingLine(Token token, out string? usingPath, out Range? usingRange)
    {
        usingPath = null;
        usingRange = null;

        int line = token.Range.Start.Line;

        // Move to the first token of the line
        Token? cursor = token;
        while (cursor.Previous is not null && cursor.Previous.Range.End.Line == line)
        {
            cursor = cursor.Previous;
        }

        // Find '#using' token on the next line (since this is EOL)
        Token? usingToken = null;
        Token? iter = cursor.Next;
        int guard = 0;
        while (iter is not null && iter.Range.Start.Line == line)
        {
            if (iter.Lexeme == "#using")
            {
                usingToken = iter;
                break;
            }
            // Advance; break if no progress or if guard trips to avoid infinite loops
            Token? prev = iter;
            iter = iter.Next;
            if (ReferenceEquals(iter, prev))
            {
                break;
            }
            if (++guard > 10000)
            {
                break;
            }
        }

        if (usingToken is null)
        {
            return false;
        }

        // Collect tokens after '#using' up to ';' or EOL
        Token? start = usingToken.Next;
        while (start is not null && start.IsWhitespacey()) start = start.Next;
        if (start is null || start.Range.Start.Line != line)
        {
            return false;
        }
        Token? end = start;
        Token? walker = start;
        while (walker is not null && walker.Range.Start.Line == line)
        {
            if (walker.Type == TokenType.Semicolon || walker.Type == TokenType.LineBreak)
            {
                break;
            }
            end = walker;
            walker = walker.Next;
        }
        if (end is null)
        {
            return false;
        }

        // Build path using raw source between start and end
        var list = new TokenList(start, end);
        string raw = list.ToRawString();
        if (string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }
        usingPath = raw.Trim();
        usingRange = RangeHelper.From(start.Range.Start, end.Range.End);
        return true;
    }

    public async Task<(string? qualifier, string name)?> GetQualifiedIdentifierAtAsync(Position position, CancellationToken cancellationToken = default)
    {
        await WaitUntilParsedAsync(cancellationToken);

        Token? token = Sense.Tokens.Get(position);
        if (token is null)
        {
            return null;
        }

        return ParseNamespaceQualifiedIdentifier(token);
    }

    private static string? ExtractParameterDocFromDoc(string? doc, string paramName, int paramIndex)
    {
        if (string.IsNullOrWhiteSpace(doc) || string.IsNullOrWhiteSpace(paramName)) return null;
        string Normalize(string s) => s.Trim().Trim('<', '>', '[', ']').Trim();

        // Try to parse prototype line to map by position
        string[] ParseDocPrototypeParams(string d)
        {
            var lines = d.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            var line = lines.Length > 1 ? lines[1].Trim() : string.Empty; // second line from ```gsc\r\nfoo(bar)...```
            int lp = line.IndexOf('(');
            int rp = line.LastIndexOf(')');
            if (lp < 0 || rp < lp) return Array.Empty<string>();
            string inside = line.Substring(lp + 1, rp - lp - 1);
            if (string.IsNullOrWhiteSpace(inside)) return Array.Empty<string>();
            var parts = inside.Split(',');
            var list = new List<string>(parts.Length);
            foreach (var p in parts)
            {
                var v = Normalize(p);
                if (v.Length > 0) list.Add(v);
            }
            return list.ToArray();
        }

        string[] protoParams = ParseDocPrototypeParams(doc);
        List<string> candidates = new() { Normalize(paramName) };
        if (paramIndex >= 0 && paramIndex < protoParams.Length)
        {
            candidates.Add(Normalize(protoParams[paramIndex]));
        }

        // Scan parameter description lines (``<param>`` — desc)
        string[] linesDoc = doc.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        foreach (var raw in linesDoc)
        {
            string line = raw.Trim();
            if (line.Length == 0) continue;
            int b1 = line.IndexOf('`');
            if (b1 < 0) continue;
            int b2 = line.IndexOf('`', b1 + 1);
            if (b2 < 0) continue;
            string token = Normalize(line.Substring(b1 + 1, b2 - b1 - 1));
            bool match = false;
            foreach (var c in candidates)
            {
                if (string.Equals(c, token, StringComparison.OrdinalIgnoreCase)) { match = true; break; }
            }
            if (!match) continue;
            int dash = line.IndexOf('—', b2 + 1); // em dash
            if (dash < 0) dash = line.IndexOf('-', b2 + 1); // fallback
            string desc = dash >= 0 && dash + 1 < line.Length ? line[(dash + 1)..].Trim() : string.Empty;
            return desc;
        }
        return null;
    }

    public async Task<SignatureHelp?> GetSignatureHelpAsync(Position position, CancellationToken cancellationToken)
    {
        await WaitUntilParsedAsync(cancellationToken);

        Token? token = Sense.Tokens.Get(position);
        if (token is null)
            return null;

        // Determine if we're inside a call: find identifier before '(' and count comma-separated args
        // Scan left to find the nearest '(' that starts the current argument list
        Token? cursor = token;
        int parenDepth = 0;
        while (cursor is not null)
        {
            if (cursor.Type == TokenType.CloseParen) parenDepth++;
            if (cursor.Type == TokenType.OpenParen)
            {
                if (parenDepth == 0) break;
                parenDepth--;
            }
            if (cursor.Type == TokenType.Identifier && cursor.Next.Type == TokenType.OpenParen && parenDepth == 0)
            {
                cursor = cursor.Next;
                break;
            }
            if (cursor.Type == TokenType.Semicolon || cursor.Type == TokenType.LineBreak)
            {
                // Hit end of statement without finding '('
                cursor = null;
                break;
            }
            cursor = cursor.Previous;
        }
        if (cursor is null)
            return null; // not in a call

        // Find the identifier before this '('
        Token? id = cursor.Previous;
        while (id is not null && (id.IsWhitespacey() || id.IsComment())) id = id.Previous;
        if (id is null || id.Type != TokenType.Identifier)
            return null;

        var (qualifier, name) = ParseNamespaceQualifiedIdentifier(id);

        // Count arguments index by scanning commas from cursor to current position, without nesting into inner parens/brackets/braces
        int activeParam = 0;
        Token? walker = cursor.Next;
        int depthParen = 0, depthBracket = 0, depthBrace = 0;
        while (walker is not null && walker != token.Next)
        {
            if (walker.Type == TokenType.OpenParen) depthParen++;
            else if (walker.Type == TokenType.CloseParen)
            {
                if (depthParen == 0) break; // end of this call
                depthParen--;
            }
            else if (walker.Type == TokenType.OpenBracket) depthBracket++;
            else if (walker.Type == TokenType.CloseBracket && depthBracket > 0) depthBracket--;
            else if (walker.Type == TokenType.OpenBrace) depthBrace++;
            else if (walker.Type == TokenType.CloseBrace && depthBrace > 0) depthBrace--;
            else if (walker.Type == TokenType.Comma && depthParen == 0 && depthBracket == 0 && depthBrace == 0)
            {
                activeParam++;
            }
            walker = walker.Next;
        }

        // Try builtin API first
        List<SignatureInformation> signatures = new();
        var api = TryGetApi();
        if (api is not null)
        {
            try
            {
                var apiFn = api.GetApiFunction(name);
                if (apiFn is not null)
                {
                    var overload = apiFn.Overloads.FirstOrDefault();
                    IEnumerable<GSCode.Parser.SPA.Sense.ScrFunctionParameter> paramSeq = overload != null ? (IEnumerable<GSCode.Parser.SPA.Sense.ScrFunctionParameter>)overload.Parameters : Enumerable.Empty<GSCode.Parser.SPA.Sense.ScrFunctionParameter>();
                    var cleaned = paramSeq.Select(p => StripDefault(p.Name)).ToArray();
                    string label = $"function {name}({string.Join(", ", cleaned)})";
                    var parameters = new Container<ParameterInformation>(paramSeq.Select(p => new ParameterInformation { Label = StripDefault(p.Name), Documentation = string.IsNullOrWhiteSpace(p.Description) ? null : new MarkupContent { Kind = MarkupKind.Markdown, Value = p.Description! } }));
                    var docContent = new MarkupContent { Kind = MarkupKind.Markdown, Value = apiFn.Description ?? string.Empty };
                    signatures.Add(new SignatureInformation { Label = label, Documentation = docContent, Parameters = parameters });
                }
            }
            catch { }
        }

        // Then script-defined (local or imported) using DefinitionsTable
        string ns = qualifier ?? (DefinitionsTable?.CurrentNamespace ?? Path.GetFileNameWithoutExtension(ScriptUri.ToUri().LocalPath));
        string[]? parms = DefinitionsTable?.GetFunctionParameters(ns, name) ?? DefinitionsTable?.GetFunctionParameters(qualifier ?? ns, name);
        string? doc = DefinitionsTable?.GetFunctionDoc(ns, name);
        if (parms is not null)
        {
            var cleaned = parms.Select(StripDefault).ToArray();
            string label = $"function {name}({string.Join(", ", cleaned)})";
            var paramList = new List<ParameterInformation>(cleaned.Length);
            for (int i = 0; i < cleaned.Length; i++)
            {
                string p = cleaned[i];
                string? pDoc = ExtractParameterDocFromDoc(doc, p, i);
                paramList.Add(new ParameterInformation
                {
                    Label = p,
                    Documentation = string.IsNullOrWhiteSpace(pDoc) ? null : new MarkupContent { Kind = MarkupKind.Markdown, Value = pDoc }
                });
            }
            var parameters = new Container<ParameterInformation>(paramList);
            // Do not include full Markdown doc in SignatureHelp for script-defined; keep prototype and parameters only
            signatures.Add(new SignatureInformation { Label = label, Parameters = parameters });
        }

        if (signatures.Count == 0)
            return null;

        int paramCount = 1;
        if (signatures[0].Parameters is { } paramContainer)
        {
            paramCount = paramContainer.Count();
        }

        return new SignatureHelp
        {
            ActiveSignature = 0,
            ActiveParameter = Math.Max(0, Math.Min(activeParam, paramCount - 1)),
            Signatures = new Container<SignatureInformation>(signatures)
        };
    }

    private static string StripDefault(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return string.Empty;
        int idx = name.IndexOf('=');
        return idx >= 0 ? name[..idx].Trim() : name.Trim();
    }

    // ===== SPA helpers =====

    private void EmitUnusedParameterDiagnostics()
    {
        if (RootNode is null) return;
        // Traverse functions
        foreach (var fn in EnumerateFunctions(RootNode))
        {
            // collect used identifiers in function body
            HashSet<string> used = new(StringComparer.OrdinalIgnoreCase);
            CollectIdentifiers(fn.Body, used);
            foreach (var p in fn.Parameters.Parameters)
            {
                if (p.Name is null) continue;
                string paramName = p.Name.Lexeme;
                if (!used.Contains(paramName))
                {
                    Sense.AddSpaDiagnostic(p.Name.Range, GSCErrorCodes.UnusedParameter, paramName);
                }
            }
        }
    }

    private void EmitCallArityDiagnostics()
    {
        if (RootNode is null) return;
        // Build known function param counts for local/dep functions
        var localParamMap = new Dictionary<(string ns, string name), (int required, int total)>();
        if (DefinitionsTable is not null)
        {
            foreach (var kv in DefinitionsTable.GetAllFunctionParameters())
            {
                var key = kv.Key;
                var values = kv.Value ?? Array.Empty<string>();
                int total = values.Length;
                int required = 0;
                foreach (var v in values)
                {
                    if (string.IsNullOrWhiteSpace(v)) continue;
                    if (!v.Contains('=')) required++;
                }
                localParamMap[(key.Namespace, key.Name)] = (required, total);
            }
        }

        // Walk all call nodes
        foreach (var call in EnumerateCalls(RootNode))
        {
            string? ns = null;
            string? name = null;
            Range reportRange = call.Arguments.Range;

            if (call is FunCallNode fcall)
            {
                switch (fcall.Target)
                {
                    case IdentifierExprNode id:
                        name = id.Identifier;
                        reportRange = fcall.Arguments.Range;
                        break;
                    case NamespacedMemberNode nsm:
                        if (nsm.Namespace is IdentifierExprNode nsId && nsm.Member is IdentifierExprNode member)
                        {
                            ns = nsId.Identifier; name = member.Identifier; reportRange = fcall.Arguments.Range;
                        }
                        break;
                }
            }
            // MethodCallNode also counts as a call, but we can't reliably infer name for arity; skip
            if (name is null) continue;

            int argCount = call.Arguments.Arguments.Count;

            // Builtin API arity check
            if (TryGetApi() is ScriptAnalyserData api)
            {
                try
                {
                    var apiFn = api.GetApiFunction(name);
                    if (apiFn is not null && ns is null)
                    {
                        int minAny = int.MaxValue; int maxAny = int.MinValue; bool any = false;
                        foreach (var ov in apiFn.Overloads)
                        {
                            int total = ov.Parameters.Count;
                            int req = ov.Parameters.Count(p => p.Mandatory == true);
                            minAny = Math.Min(minAny, req);
                            maxAny = Math.Max(maxAny, total);
                            any = true;
                        }
                        if (any)
                        {
                            // remove on purpose: too few arguments is very common due to optional params and overloads
                            // TODO: both disabled due to API - re-enable when we're comfortable with the API or have some middle ground solution
                            //if (argCount < minAny)
                            //{
                            //    Sense.AddSpaDiagnostic(reportRange, GSCErrorCodes.TooFewArguments, name, argCount, minAny);
                            //    continue;
                            //}
                            // if (argCount > maxAny)
                            // {
                            //     Sense.AddSpaDiagnostic(reportRange, GSCErrorCodes.TooManyArguments, name, argCount, maxAny);
                            //     continue;
                            // }
                            // If within some overload’s min/max, we assume OK (type checking not implemented)
                            continue;
                        }
                    }
                }
                catch { }
            }

            // Local/dep function arity check via DefinitionsTable
            string lookupNs = ns ?? (DefinitionsTable?.CurrentNamespace ?? Path.GetFileNameWithoutExtension(ScriptUri.ToUri().LocalPath));
            (int required, int total) paramInfo;
            if (!localParamMap.TryGetValue((lookupNs, name), out paramInfo))
            {
                // try any namespace entry for this name if not namespaced
                if (ns is null && localParamMap.Count > 0)
                {
                    bool found = false;
                    foreach (var kv in localParamMap)
                    {
                        if (string.Equals(kv.Key.name, name, StringComparison.OrdinalIgnoreCase))
                        {
                            paramInfo = kv.Value;
                            lookupNs = kv.Key.ns;
                            found = true;
                            break;
                        }
                    }
                    if (!found)
                    {
                        continue; // unknown symbol; skip
                    }
                }
                else
                {
                    continue;
                }
            }

            // removed on purpose : too few arguments is very common due to optional params and overloads
            //if (argCount < paramInfo.required)
            //{
            //    Sense.AddSpaDiagnostic(reportRange, GSCErrorCodes.TooFewArguments, name, argCount, paramInfo.required);
            //}
            //else 
            if (paramInfo.total >= 0 && argCount > paramInfo.total)
            {
                Sense.AddSpaDiagnostic(reportRange, GSCErrorCodes.TooManyArguments, name, argCount, paramInfo.total);
            }
        }
    }

    private void EmitUnknownNamespaceDiagnostics()
    {
        if (RootNode is null || DefinitionsTable is null) return;
        HashSet<string> known = new(StringComparer.OrdinalIgnoreCase);
        foreach (var kv in DefinitionsTable.GetAllFunctionLocations()) known.Add(kv.Key.Namespace);
        foreach (var kv in DefinitionsTable.GetAllClassLocations()) known.Add(kv.Key.Namespace);
        known.Add(DefinitionsTable.CurrentNamespace);

        foreach (var nsm in EnumerateNamespacedMembers(RootNode))
        {
            if (nsm.Namespace is IdentifierExprNode nsId && !known.Contains(nsId.Identifier))
            {
                Sense.AddSpaDiagnostic(nsId.Range, GSCErrorCodes.UnknownNamespace, nsId.Identifier);
            }
        }
    }

    private void EmitUnusedUsingDiagnostics()
    {
        if (RootNode is null || DefinitionsTable is null) return;

        // Map referenced file paths
        HashSet<string> referencedFiles = new(StringComparer.OrdinalIgnoreCase);
        foreach (var kv in _references)
        {
            var key = kv.Key;
            var fLoc = DefinitionsTable.GetFunctionLocation(key.Namespace, key.Name);
            if (fLoc is not null) { referencedFiles.Add(NormalizeFilePathForUri(fLoc.Value.FilePath)); continue; }
            var cLoc = DefinitionsTable.GetClassLocation(key.Namespace, key.Name);
            if (cLoc is not null) { referencedFiles.Add(NormalizeFilePathForUri(cLoc.Value.FilePath)); continue; }
        }

        // For each using directive, if no referenced symbol comes from that dependency file, mark unused
        foreach (var depNode in RootNode.Dependencies)
        {
            // Build expected suffix: ..\scripts\<depNode.Path>.<LanguageId>
            string rel = depNode.Path.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar).TrimStart(Path.DirectorySeparatorChar);
            string expectedSuffix = Path.DirectorySeparatorChar + rel + "." + LanguageId;

            bool anyFromThisUsing = false;
            foreach (var referenced in referencedFiles)
            {
                // referenced already normalized
                if (referenced.Contains(expectedSuffix, StringComparison.OrdinalIgnoreCase))
                {
                    anyFromThisUsing = true;
                    break;
                }
            }

            if (!anyFromThisUsing)
            {
                Sense.AddSpaDiagnostic(depNode.Range, GSCErrorCodes.UnusedUsing, depNode.Path);
            }
        }
    }

    private void EmitUnusedVariableDiagnostics()
    {
        if (RootNode is null) return;
        foreach (var fn in EnumerateFunctions(RootNode))
        {
            // Count all identifier occurrences within the function body
            Dictionary<string, int> usageCounts = new(StringComparer.OrdinalIgnoreCase);
            CollectIdentifierCounts(fn.Body, usageCounts);

            // Single pass through function body to flag unused consts and assignments
            foreach (var node in EnumerateChildren(fn.Body))
            {
                if (node is ConstStmtNode cst)
                {
                    string name = cst.Identifier;
                    usageCounts.TryGetValue(name, out int count);
                    if (count == 0)
                    {
                        Sense.AddSpaDiagnostic(cst.Range, GSCErrorCodes.UnusedVariable, name);
                    }
                    continue;
                }

                if (node is ExprStmtNode es && es.Expr is BinaryExprNode be && be.Operation == TokenType.Assign && be.Left is IdentifierExprNode id)
                {
                    string name = id.Identifier;
                    usageCounts.TryGetValue(name, out int count);
                    // If the only occurrence is this defining assignment (LHS), consider it unused
                    if (count <= 1)
                    {
                        Sense.AddSpaDiagnostic(id.Range, GSCErrorCodes.UnusedVariable, name);
                    }
                }
            }
        }
    }

    private static void CollectIdentifierCounts(AstNode node, Dictionary<string, int> into)
    {
        if (node is IdentifierExprNode id)
        {
            if (!into.TryGetValue(id.Identifier, out int c)) c = 0;
            into[id.Identifier] = c + 1;
        }
        foreach (var child in EnumerateChildren(node))
        {
            CollectIdentifierCounts(child, into);
        }
    }

    private void EmitSwitchCaseDiagnostics()
    {
        if (RootNode is null) return;
        foreach (var sw in EnumerateSwitches(RootNode))
        {
            HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
            bool defaultSeen = false;
            var list = sw.Cases.Cases;
            for (var node = list.First; node is not null; node = node.Next)
            {
                var cs = node.Value;
                bool isLast = node.Next is null;
                // Handle labels
                foreach (var label in cs.Labels)
                {
                    if (label.NodeType == AstNodeType.DefaultLabel)
                    {
                        if (defaultSeen)
                        {
                            // Multiple default labels
                            Range r = GetCaseLabelOrBodyRange(cs, sw);
                            Sense.AddSpaDiagnostic(r, GSCErrorCodes.MultipleDefaultLabels);
                            // Also mark as unreachable
                            Sense.AddSpaDiagnostic(r, GSCErrorCodes.UnreachableCase);
                        }
                        else
                        {
                            defaultSeen = true;
                        }
                    }
                    else if (label.NodeType == AstNodeType.CaseLabel && label.Value is not null)
                    {
                        if (TryEvaluateCaseLabelConstant(label.Value, out string key))
                        {
                            if (!seen.Add(key))
                            {
                                // Duplicate label value in the same switch
                                Sense.AddSpaDiagnostic(label.Value.Range, GSCErrorCodes.DuplicateCaseLabel);
                                Sense.AddSpaDiagnostic(label.Value.Range, GSCErrorCodes.UnreachableCase);
                            }
                        }
                    }
                }

                // Fallthrough detection: if not the last case and body does not terminate with break/return
                if (!isLast)
                {
                    if (!HasTerminatingBreakOrReturn(cs.Body))
                    {
                        Range r = GetCaseLabelOrBodyRange(cs, sw);
                        Sense.AddSpaDiagnostic(r, GSCErrorCodes.FallthroughCase);
                    }
                }
            }
        }
    }

    private static bool TryEvaluateCaseLabelConstant(ExprNode expr, out string key)
    {
        key = string.Empty;
        if (expr is DataExprNode den)
        {
            // Encode type in key to avoid collisions between e.g., string "1" and int 1
            key = den.Type switch
            {
                ScrDataTypes.Int => $"int:{den.Value}",
                ScrDataTypes.Float => $"float:{den.Value}",
                ScrDataTypes.String => $"str:{den.Value}",
                ScrDataTypes.IString => $"istr:{den.Value}",
                ScrDataTypes.Hash => $"hash:{den.Value}",
                ScrDataTypes.Bool => $"bool:{den.Value}",
                _ => string.Empty
            };
            return key.Length > 0;
        }
        return false;
    }

    private static bool HasTerminatingBreakOrReturn(StmtListNode body)
    {
        if (body.Statements.Count == 0) return false;
        // naive: if any top-level statement is a break or a return, consider terminating
        foreach (var st in body.Statements)
        {
            if (st is ControlFlowActionNode cfan && (cfan.NodeType == AstNodeType.BreakStmt))
            {
                return true;
            }
            if (st is ReturnStmtNode)
            {
                return true;
            }
        }
        return false;
    }

    private static Range GetCaseLabelOrBodyRange(CaseStmtNode cs, SwitchStmtNode sw)
    {
        // Prefer the first label's value range if available
        var firstLabel = cs.Labels.FirstOrDefault();
        if (firstLabel is not null && firstLabel.Value is not null)
        {
            return firstLabel.Value.Range;
        }
        // Next, try first statement ranges
        var firstStmt = cs.Body.Statements.FirstOrDefault();
        if (firstStmt is ExprStmtNode es && es.Expr is not null)
        {
            return es.Expr.Range;
        }
        if (firstStmt is ControlFlowActionNode cfan)
        {
            return cfan.Range;
        }
        // Fallback to the switch expression range
        return sw.Expression?.Range ?? RangeHelper.From(0, 0, 0, 1);
    }

    private static IEnumerable<SwitchStmtNode> EnumerateSwitches(AstNode node)
    {
        if (node is SwitchStmtNode s) yield return s;
        foreach (var child in EnumerateChildren(node))
        {
            foreach (var x in EnumerateSwitches(child)) yield return x;
        }
    }

    // Enumerators
    private static IEnumerable<FunDefnNode> EnumerateFunctions(AstNode node)
    {
        if (node is FunDefnNode fn) yield return fn;
        foreach (var child in EnumerateChildren(node))
        {
            foreach (var f in EnumerateFunctions(child)) yield return f;
        }
    }

    private static IEnumerable<FunCallNode> EnumerateCalls(AstNode node)
    {
        if (node is FunCallNode f) yield return f;
        foreach (var child in EnumerateChildren(node))
        {
            foreach (var c in EnumerateCalls(child)) yield return c;
        }
    }

    private static IEnumerable<NamespacedMemberNode> EnumerateNamespacedMembers(AstNode node)
    {
        if (node is NamespacedMemberNode n) yield return n;
        foreach (var child in EnumerateChildren(node))
        {
            foreach (var c in EnumerateNamespacedMembers(child)) yield return c;
        }
    }

    private static IEnumerable<BinaryExprNode> EnumerateBinaryExprs(AstNode node)
    {
        if (node is BinaryExprNode b) yield return b;
        foreach (var child in EnumerateChildren(node))
        {
            foreach (var c in EnumerateBinaryExprs(child)) yield return c;
        }
    }

    private static bool ContainsThreadCall(AstNode node)
    {
        if (node is PrefixExprNode pe && pe.Operation == TokenType.Thread)
        {
            return true;
        }
        if (node is CalledOnNode con)
        {
            // thread appears on the call side for patterns like self thread foo();
            if (ContainsThreadCall(con.Call)) return true;
            // also traverse 'on' to be thorough
            if (ContainsThreadCall(con.On)) return true;
            return false;
        }
        foreach (var child in EnumerateChildren(node))
        {
            if (ContainsThreadCall(child)) return true;
        }
        return false;
    }

    private static bool ContainsThreadCallCached(AstNode node, Dictionary<AstNode, bool> cache)
    {
        if (cache.TryGetValue(node, out var v)) return v;
        bool result;
        if (node is PrefixExprNode pe && pe.Operation == TokenType.Thread)
        {
            result = true;
        }
        else if (node is CalledOnNode con)
        {
            result = ContainsThreadCallCached(con.Call, cache) || ContainsThreadCallCached(con.On, cache);
        }
        else
        {
            result = false;
            foreach (var child in EnumerateChildren(node))
            {
                if (ContainsThreadCallCached(child, cache)) { result = true; break; }
            }
        }
        cache[node] = result;
        return result;
    }

    private void EmitAssignOnThreadDiagnostics()
    {
        if (RootNode is null) return;
        var cache = new Dictionary<AstNode, bool>(ReferenceEqualityComparer.Instance);
        foreach (var fn in EnumerateFunctions(RootNode))
        {
            foreach (var bin in EnumerateBinaryExprs(fn.Body))
            {
                if (bin.Operation == TokenType.Assign && bin.Right is not null)
                {
                    if (ContainsThreadCallCached(bin.Right, cache))
                    {
                        Sense.AddSpaDiagnostic(bin.Range, GSCErrorCodes.AssignOnThreadedFunction);
                    }
                }
            }
        }
    }

    private static IEnumerable<AstNode> EnumerateChildren(AstNode node)
    {
        switch (node)
        {
            case ScriptNode s:
                foreach (var d in s.Dependencies) yield return d;
                foreach (var sd in s.ScriptDefns) yield return sd; break;
            case ClassBodyListNode cbl:
                foreach (var d in cbl.Definitions) yield return d; break;
            case StmtListNode sl:
                foreach (var st in sl.Statements) yield return st; break;
            case IfStmtNode iff:
                if (iff.Condition is not null) yield return iff.Condition;
                if (iff.Then is not null) yield return iff.Then;
                if (iff.Else is not null) yield return iff.Else; break;
            case ReservedFuncStmtNode rfs:
                if (rfs.Expr is not null) yield return rfs.Expr; break;
            case ConstStmtNode cst:
                if (cst.Value is not null) yield return cst.Value; break;
            case ExprStmtNode es:
                if (es.Expr is not null) yield return es.Expr; break;
            case DoWhileStmtNode dw:
                if (dw.Condition is not null) yield return dw.Condition; if (dw.Then is not null) yield return dw.Then; break;
            case WhileStmtNode wl:
                if (wl.Condition is not null) yield return wl.Condition; if (wl.Then is not null) yield return wl.Then; break;
            case ForStmtNode fr:
                if (fr.Init is not null) yield return fr.Init; if (fr.Condition is not null) yield return fr.Condition; if (fr.Increment is not null) yield return fr.Increment; if (fr.Then is not null) yield return fr.Then; break;
            case ForeachStmtNode fe:
                if (fe.Collection is not null) yield return fe.Collection; if (fe.Then is not null) yield return fe.Then; break;
            case ReturnStmtNode rt:
                if (rt.Value is not null) yield return rt.Value; break;
            case DefnDevBlockNode db:
                foreach (var d in db.Definitions) yield return d; break;
            case FunDevBlockNode fdb:
                yield return fdb.Body; break;
            case SwitchStmtNode sw:
                if (sw.Expression is not null) yield return sw.Expression; yield return sw.Cases; break;
            case CaseListNode cl:
                foreach (var c in cl.Cases) yield return c; break;
            case CaseStmtNode cs:
                foreach (var l in cs.Labels) yield return l; yield return cs.Body; break;
            case CaseLabelNode cln:
                if (cln.Value is not null) yield return cln.Value; break;
            case FunDefnNode fd:
                yield return fd.Parameters; yield return fd.Body; break;
            case ParamListNode pl:
                foreach (var p in pl.Parameters) yield return p; break;
            case ParamNode pn:
                if (pn.Default is not null) yield return pn.Default; break;
            case VectorExprNode vx:
                yield return vx.X; if (vx.Y is not null) yield return vx.Y; if (vx.Z is not null) yield return vx.Z; break;
            case TernaryExprNode te:
                yield return te.Condition; if (te.Then is not null) yield return te.Then; if (te.Else is not null) yield return te.Else; break;
            case BinaryExprNode be:
                if (be.Left is not null) yield return be.Left; if (be.Right is not null) yield return be.Right; break;
            case PrefixExprNode pe:
                yield return pe.Operand; break;
            case PostfixExprNode pxe:
                yield return pxe.Operand; break;
            case ConstructorExprNode:
                break;
            case MethodCallNode mc:
                if (mc.Target is not null) yield return mc.Target; yield return mc.Arguments; break;
            case FunCallNode fc:
                if (fc.Target is not null) yield return fc.Target; yield return fc.Arguments; break;
            case NamespacedMemberNode nm:
                yield return nm.Namespace; yield return nm.Member; break;
            case ArgsListNode al:
                foreach (var a in al.Arguments) { if (a is not null) yield return a; }
                break;
            case ArrayIndexNode ai:
                yield return ai.Array; if (ai.Index is not null) yield return ai.Index; break;
            case CalledOnNode con:
                yield return con.On; yield return con.Call; break;
            default:
                break;
        }
    }

    private static void CollectIdentifiers(AstNode node, HashSet<string> into)
    {
        switch (node)
        {
            case IdentifierExprNode id:
                into.Add(id.Identifier);
                break;
        }
        foreach (var child in EnumerateChildren(node))
        {
            CollectIdentifiers(child, into);
        }
    }

    public async Task<IReadOnlyList<Range>> GetLocalVariableReferencesAsync(Position position, bool includeDeclaration, CancellationToken cancellationToken = default)
    {
        await WaitUntilParsedAsync(cancellationToken);

        // Acquire the token under the cursor
        Token? token = Sense.Tokens.Get(position);
        if (token is null || token.Type != TokenType.Identifier)
        {
            return Array.Empty<Range>();
        }
        string target = token.Lexeme;

        // Find the enclosing function that either contains the position in its body
        // or, if the token matches a parameter name, the function that declares it.
        FunDefnNode? enclosing = null;
        foreach (var fn in EnumerateFunctions(RootNode!))
        {
            var bodyRange = GetStmtListRange(fn.Body);
            if (IsPositionInsideRange(position, bodyRange))
            {
                enclosing = fn;
                break;
            }
            // If the position is not inside body, check if it's on a parameter name
            foreach (var p in fn.Parameters.Parameters)
            {
                if (p.Name is null) continue;
                if (ComparePosition(position, p.Name.Range.Start) >= 0 && ComparePosition(p.Name.Range.End, position) >= 0)
                {
                    enclosing = fn;
                    break;
                }
            }
            if (enclosing is not null) break;
        }

        if (enclosing is null)
        {
            return Array.Empty<Range>();
        }

        // Compute function body range and collect identifier tokens within it matching the name
        Range body = GetStmtListRange(enclosing.Body);
        var results = new List<Range>();
        foreach (var t in Sense.Tokens.GetAll())
        {
            if (t.Type != TokenType.Identifier) continue;
            // Restrict to tokens in the same function body
            if (!(ComparePosition(t.Range.Start, body.Start) >= 0 && ComparePosition(body.End, t.Range.End) >= 0))
                continue;
            // Match name (case-insensitive to be consistent with SPA checks)
            if (!string.Equals(t.Lexeme, target, StringComparison.OrdinalIgnoreCase)) continue;
            // Exclude namespace-qualified identifiers or function calls
            Token? prev = t.Previous;
            while (prev is not null && prev.IsWhitespacey()) prev = prev.Previous;
            if (prev is not null && prev.Type == TokenType.ScopeResolution) continue;
            Token? next = t.Next;
            while (next is not null && next.IsWhitespacey()) next = next.Next;
            if (next is not null && next.Type == TokenType.OpenParen) continue; // looks like a call

            results.Add(t.Range);
        }

        // Optionally include declaration site (parameter declaration)
        if (includeDeclaration)
        {
            foreach (var p in enclosing.Parameters.Parameters)
            {
                if (p.Name is null) continue;
                if (string.Equals(p.Name.Lexeme, target, StringComparison.OrdinalIgnoreCase))
                {
                    results.Add(p.Name.Range);
                }
            }
        }

        return results;
    }
}
