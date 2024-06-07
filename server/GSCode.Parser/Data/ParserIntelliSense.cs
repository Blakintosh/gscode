using GSCode.Data;
using GSCode.Parser.AST.Expressions;
using Microsoft.VisualStudio.LanguageServer.Protocol;

namespace GSCode.Parser.Data;

public sealed record class SemanticToken(Range Range, string SemanticTokenType, string[] SemanticTokenModifiers) : ISemanticToken;

public enum DeferredSymbolType
{
    Function,
    Class
}
public sealed record class DeferredSymbol(Range Range, string? Namespace, string Value);

public sealed class ParserIntelliSense
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
    public List<Uri> Dependencies { get; } = new();

    public ParserIntelliSense(int endLine)
    {
        HoverLibrary = new(endLine + 1);
    }

    public void AddSenseToken(ISenseToken token)
    {
        SemanticTokens.Add(token);
        HoverLibrary.Add(token);
    }

    public void AddDiagnostic(Range range, string source, GSCErrorCodes code, params object?[] args)
    {
        Diagnostics.Add(DiagnosticCodes.GetDiagnostic(range, source, code, args));
    }

    public void AddSpaDiagnostic(Range range, GSCErrorCodes code, params object?[] args)
    {
        Diagnostics.Add(DiagnosticCodes.GetDiagnostic(range, DiagnosticSources.SPA, code, args));
    }

    public void AddAstDiagnostic(Range range, GSCErrorCodes code, params object?[] args)
    {
        Diagnostics.Add(DiagnosticCodes.GetDiagnostic(range, DiagnosticSources.AST, code, args));
    }

    public void AddDependency(string scriptPath)
    {
        Dependencies.Add(new Uri(scriptPath));
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
