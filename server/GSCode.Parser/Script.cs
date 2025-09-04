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
using System.IO;
using GSCode.Parser.SPA;
using System.Text.RegularExpressions;

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

        // Build references index from token stream
        BuildReferenceIndex();

        Parsed = true;
        return Task.CompletedTask;
    }

    private static Token? PreviousNonTrivia(Token? token)
    {
        Token? t = token?.Previous;
        while (t is not null && (t.IsWhitespacey() || t.IsComment()))
        {
            t = t.Previous;
        }
        return t;
    }

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

    public async Task AnalyseAsync(IEnumerable<IExportedSymbol> exportedSymbols, CancellationToken cancellationToken = default)
    {
        await WaitUntilParsedAsync(cancellationToken);

        // TODO: Implement deeper analysis; for now references are built after parse.
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
        StringBuilder sb = new();
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
            var def = scriptMacro.DefineSource;
            if (def.IsFromPreprocessor)
            {
                string normalized = NormalizeFilePathForUri(ScriptUri.ToUri().LocalPath);
                return new Location() { Uri = new Uri(normalized), Range = def.Range };
            }
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
            string? resolved = Sense.GetDependencyPath(ih.RawPath, ih.Range);
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
        while (iter is not null && iter.Range.Start.Line == line)
        {
            if (iter.Lexeme == "#using")
            {
                usingToken = iter;
                break;
            }
            iter = iter.Next;
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
            var line = lines[1].Trim(); // always the second line from ```gsc\r\nfoo(bar)...```
            int lp = line.IndexOf('(');
            int rp = line.LastIndexOf(')');
            if (lp < 0 || rp < lp) return Array.Empty<string>();
            string inside = line.Substring(lp + 1, rp - lp - 1);
            if (string.IsNullOrWhiteSpace(inside)) return Array.Empty<string>();
            var parts = inside.Split(',');
            return parts.Select(p => Normalize(p)).Where(p => p.Length > 0).ToArray();
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
            if (!candidates.Any(c => string.Equals(c, token, StringComparison.OrdinalIgnoreCase))) continue;
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
            if(cursor.Type == TokenType.Semicolon || cursor.Type == TokenType.LineBreak)
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
}
