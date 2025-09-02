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

        Parsed = true;
        return Task.CompletedTask;
    }

    public async Task AnalyseAsync(IEnumerable<IExportedSymbol> exportedSymbols, CancellationToken cancellationToken = default)
    {
        await WaitUntilParsedAsync(cancellationToken);

        // TODO: Implement this.
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
        return null;
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

        // Fallback: if we're on a #using line but the specific token doesn't have a SenseDefinition,
        // reconstruct the dependency path from tokens on the same line and navigate to it.
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

        // Ensure the token is a function-like identifier before attempting Go-to-Definition.
        // Acceptable cases:
        //  - An identifier followed by '(' (call site)
        //  - A namespace::identifier qualified reference (previous token is '::')
        //  - The identifier itself is a declared function/class/method (has corresponding SenseDefinition)
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

        if (!looksLikeCall && !isQualified && !hasDefinitionSymbol)
        {
            // Not a function token (could be variable, property, etc.) — skip
            return null;
        }

        // Parse namespace qualifier
        var (qualifier, name) = ParseNamespaceQualifiedIdentifier(token);

        // Built-in API functions/methods: if the name exists in the language API, treat as builtin (no source location)
        try
        {
            ScriptAnalyserData api = new(LanguageId);
            var apiFn = api.GetApiFunction(name);
            if (apiFn is not null)
            {
                // Built-in function — no source location to return
                return null;
            }
        }
        catch
        {
            // Be permissive on failures when loading API data — fall back to normal lookup
        }

        // 1. Try qualified lookup if present
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

        // 2. Try current namespace
        string ns = DefinitionsTable?.CurrentNamespace ?? Path.GetFileNameWithoutExtension(ScriptUri.ToUri().LocalPath);
        var localLoc = DefinitionsTable?.GetFunctionLocation(ns, name)
                    ?? DefinitionsTable?.GetClassLocation(ns, name);
        if (localLoc is not null)
        {
            string normalized = NormalizeFilePathForUri(localLoc.Value.FilePath);
            var targetUri = new Uri(normalized);
            return new Location() { Uri = targetUri, Range = localLoc.Value.Range };
        }

        // 3. Try any namespace in this file
        var anyLoc = DefinitionsTable?.GetFunctionLocationAnyNamespace(name)
                  ?? DefinitionsTable?.GetClassLocationAnyNamespace(name);
        if (anyLoc is not null)
        {
            string normalized = NormalizeFilePathForUri(anyLoc.Value.FilePath);
            var targetUri = new Uri(normalized);
            return new Location() { Uri = targetUri, Range = anyLoc.Value.Range };
        }

        // 4. Not found locally; let ScriptManager search all loaded scripts (pass qualifier if present)
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

    public async Task<(string? qualifier, string name)?> GetQualifiedIdentifierAt(Position position, CancellationToken cancellationToken = default)
    {
        await WaitUntilParsedAsync(cancellationToken);

        Token? token = Sense.Tokens.Get(position);
        if (token is null)
        {
            return null;
        }

        return ParseNamespaceQualifiedIdentifier(token);
    }

    //private void ThrowIfNotAnalysed()
    //{
    //    if (!Analysed)
    //    {
    //        throw new InvalidOperationException("The script has not been analysed yet.");
    //    }
    //}
}
