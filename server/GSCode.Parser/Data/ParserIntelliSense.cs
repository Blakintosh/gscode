using GSCode.Data;
using GSCode.Parser.AST.Expressions;
using GSCode.Parser.Lexical;
using GSCode.Parser.Util;
using OmniSharp.Extensions.LanguageServer.Protocol;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;

namespace GSCode.Parser.Data;

public sealed record class SemanticToken(Range Range, string SemanticTokenType, string[] SemanticTokenModifiers) : ISemanticToken;

public enum DeferredSymbolType
{
    Function,
    Class
}
public sealed record class DeferredSymbol(Range Range, string? Namespace, string Value);

internal sealed class ParserIntelliSense
{

    /// <summary>
    /// List of semantic tokens to push to the editor.
    /// </summary>
    public List<ISemanticToken> SemanticTokens { get; } = new();

    /// <summary>
    /// Hover storage for IntelliSense
    /// </summary>
    public DocumentHoversLibrary HoverLibrary { get; }

    /// <summary>
    /// List of diagnostics to push to the editor.
    /// </summary>
    public List<Diagnostic> Diagnostics { get; } = new();

    /// <summary>
    /// List of dependencies to request from the Language Server.
    /// </summary>
    public List<DocumentUri> Dependencies { get; } = new();

    private readonly string _scriptPath;

    public ParserIntelliSense(int endLine, DocumentUri scriptUri)
    {
        HoverLibrary = new(endLine + 1);
        _scriptPath = scriptUri.Path;
    }

    public void AddSenseToken(Token token, ISenseDefinition definition)
    {
        // The token is from an insert (which we don't show) or it's already had a definition pushed.
        // In these cases, skip (for existing, the first gets precedence).
        if (token.IsFromPreprocessor || token.SenseDefinition is not null)
        {
            return;
        }
        
        // Link the definition to the token so that we don't have duplicates later on.
        token.SenseDefinition = definition;
        
        SemanticTokens.Add(definition);
        HoverLibrary.Add(definition);
    }

    public void AddDiagnostic(Range range, string source, GSCErrorCodes code, params object?[] args)
    {
        Diagnostics.Add(DiagnosticCodes.GetDiagnostic(range, source, code, args));
    }

    public void AddSpaDiagnostic(Range range, GSCErrorCodes code, params object?[] args) => AddDiagnostic(range, DiagnosticSources.Spa, code, args);
    public void AddAstDiagnostic(Range range, GSCErrorCodes code, params object?[] args) => AddDiagnostic(range, DiagnosticSources.Ast, code, args);
    public void AddPreDiagnostic(Range range, GSCErrorCodes code, params object?[] args) => AddDiagnostic(range, DiagnosticSources.Preprocessor, code, args);
    public void AddIdeDiagnostic(Range range, GSCErrorCodes code, params object?[] args) => AddDiagnostic(range, DiagnosticSources.Ide, code, args);

    public void AddDependency(string scriptPath)
    {
        Dependencies.Add(new Uri(scriptPath));
    }

    public TokenList? GetFileTokens(string dependencyPath, Range? belongToRange = null)
    {
        string? resolvedPath = ParserUtil.GetScriptFilePath(_scriptPath, dependencyPath);

        // Sanity check the result
        if(resolvedPath is null || !File.Exists(resolvedPath))
        {
            return null;
        }

        string contents = File.ReadAllText(resolvedPath);

        // Use the lexer to transform the contents into a token list, with their range being the one specified.
        Lexer lexer = new(contents.AsSpan(), belongToRange);
        return lexer.Transform();
    }

    /* Others to support:
     * Inlay hint
     * Go to declaration
     * Go to definition
     * Go to implementation
     * Find references
     * Code lens
     * Signature help
     * Completion items
     * Rename
     * ...
     */
}
