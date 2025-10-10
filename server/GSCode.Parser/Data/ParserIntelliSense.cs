using GSCode.Data;
using GSCode.Parser.AST.Expressions;
using GSCode.Parser.Lexical;
using GSCode.Parser.Util;
using OmniSharp.Extensions.LanguageServer.Protocol;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using System.Collections.Generic;
using System.IO;

namespace GSCode.Parser.Data;

public sealed record class SemanticToken(Range Range, string SemanticTokenType, string[] SemanticTokenModifiers) : ISemanticToken;

public enum DeferredSymbolType
{
    Function,
    Class
}
public sealed record class DeferredSymbol(Range Range, string? Namespace, string Value);

// Macro outline item now includes a source display (e.g., shared/shared.gsh) if known
// and the parameter names when the macro has a parameter list, plus a define snippet for UI.
public sealed record class MacroOutlineItem(
    string Name,
    Range Range,
    string? SourceDisplay = null,
    string[]? Parameters = null,
    string? DefineSnippet = null);

// Track #insert regions to map generated tokens back to their origin file
public sealed record class InsertRegion(Range Range, string RawPath, string? ResolvedPath);

// Captured macro call-site (pre-expansion), for signature help and related UX.
public sealed record class MacroCallSite(
    string Name,
    Range NameRange,
    Range CallRange,
    IReadOnlyList<Range> ArgumentRanges,
    string[]? ParameterNames,
    string? Documentation);

internal sealed class ParserIntelliSense
{
    private class SemanticTokenComparer : IComparer<ISemanticToken>
    {
        public int Compare(ISemanticToken? x, ISemanticToken? y)
        {
            if (x?.Range is null && y?.Range is null)
            {
                return 0;
            }
            if (x?.Range is null)
            {
                return -1;
            }
            if (y?.Range is null)
            {
                return 1;
            }

            int lineComparison = x.Range.Start.Line.CompareTo(y.Range.Start.Line);
            if (lineComparison != 0)
            {
                return lineComparison;
            }
            return x.Range.Start.Character.CompareTo(y.Range.Start.Character);
        }
    }

    /// <summary>
    /// List of semantic tokens to push to the editor.
    /// </summary>
    public SortedSet<ISemanticToken> SemanticTokens { get; } = new(new SemanticTokenComparer());

    /// <summary>
    /// Hover storage for IntelliSense
    /// </summary>
    public DocumentHoversLibrary HoverLibrary { get; }

    /// <summary>
    /// List of folding ranges to push to the editor.
    /// </summary>
    public List<FoldingRange> FoldingRanges { get; } = [];

    /// <summary>
    /// List of diagnostics to push to the editor.
    /// </summary>
    public List<Diagnostic> Diagnostics { get; } = new();

    /// <summary>
    /// List of dependencies to request from the Language Server.
    /// </summary>
    public List<DocumentUri> Dependencies { get; } = new();

    /// <summary>
    /// Library of tokens to quickly lookup a token at a given position.
    /// </summary>
    public DocumentTokensLibrary Tokens { get; } = new();

    /// <summary>
    /// Library of completions to quickly lookup completions at a given position.
    /// </summary>
    public DocumentCompletionsLibrary Completions { get; }

    private readonly string _scriptPath;
    public readonly string _languageId;
    public string ScriptPath => _scriptPath;
    public string ScriptUri { get; }

    /// <summary>
    /// Macros discovered during preprocessing for use in the outliner.
    /// </summary>
    public List<MacroOutlineItem> MacroOutlines { get; } = new();

    /// <summary>
    /// Insert regions (range on the source line) to resolved file path mapping.
    /// </summary>
    public List<InsertRegion> InsertRegions { get; } = new();

    /// <summary>
    /// Captured macro call-sites (pre-expansion). Used for signature help.
    /// </summary>
    public List<MacroCallSite> MacroCallSites { get; } = new();

    public ParserIntelliSense(int endLine, DocumentUri scriptUri, string languageId)
    {
        HoverLibrary = new(endLine + 1);
        _scriptPath = scriptUri.Path;
        ScriptUri = scriptUri.Path;
        _languageId = languageId;
        Completions = new(Tokens, languageId);
    }

    public void AddInsertRegion(Range range, string rawPath, string? resolvedPath)
    {
        InsertRegions.Add(new InsertRegion(range, rawPath, resolvedPath));
    }

    public void AddMacroOutline(string name, Range range, string? sourceDisplay = null, string[]? parameters = null, string? defineSnippet = null)
    {
        MacroOutlines.Add(new MacroOutlineItem(name, range, sourceDisplay, parameters, defineSnippet));
    }

    public void AddMacroCallSite(string name, Range nameRange, Range callRange, IReadOnlyList<Range> argumentRanges, string[]? parameterNames, string? documentation = null)
    {
        MacroCallSites.Add(new MacroCallSite(name, nameRange, callRange, argumentRanges, parameterNames, documentation));
    }

    public MacroCallSite? GetMacroCallSiteAt(Position position)
    {
        foreach (var site in MacroCallSites)
        {
            if (IsPositionInsideRange(position, site.CallRange))
            {
                return site;
            }
        }
        return null;
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

        AddSenseDefinition(definition);
    }

    public void AddSenseDefinition(ISenseDefinition definition)
    {
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

    public string? GetDependencyPath(string dependencyPath, Range sourceRange)
    {
        string qualifiedDependencyPath = dependencyPath + "." + _languageId;
        string? resolvedPath = ParserUtil.GetScriptFilePath(_scriptPath, qualifiedDependencyPath);
        if (resolvedPath is null)
        {
            AddSpaDiagnostic(sourceRange, GSCErrorCodes.MissingUsingFile, qualifiedDependencyPath);
            return null;
        }

        return resolvedPath;
    }

    public TokenList? GetFileTokens(string dependencyPath, Range? belongToRange = null)
    {
        string? resolvedPath = ParserUtil.GetScriptFilePath(_scriptPath, dependencyPath);

        // Sanity check the result
        if (resolvedPath is null || !File.Exists(resolvedPath))
        {
            return null;
        }

        string contents = File.ReadAllText(resolvedPath);

        // Use the lexer to transform the contents into a token list, with their range being the one specified.
        Lexer lexer = new(contents.AsSpan(), belongToRange);
        return lexer.Transform();
    }

    public void CommitTokens(Token startToken)
    {
        Tokens.AddRange(startToken);
    }

    public string? ResolveInsertPath(string dependencyPath, Range sourceRange)
    {
        string? resolvedPath = ParserUtil.GetScriptFilePath(_scriptPath, dependencyPath);
        if (resolvedPath is null)
        {
            AddPreDiagnostic(sourceRange, GSCErrorCodes.MissingInsertFile, dependencyPath);
            return null;
        }
        return resolvedPath;
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
