using System.Collections.Immutable;
using GSCode.Core;
using GSCode.Core.Diagnostics;
using GSCode.Core.Symbols;
using GSCode.Core.Text;
using GSCode.Parser.Extraction;
using GSCode.Parser.Lexing;
using GSCode.Parser.Preprocessing;
using GSCode.Parser.Syntax;

namespace GSCode.Parser;

/// <summary>
/// The complete output of analysing one file: every stage's product plus the merged
/// diagnostic list. Open documents keep this whole result; indexed files keep only
/// what the Workspace layer copies into their ScriptRecord.
/// </summary>
public sealed record ParseResult(
    string FilePath,
    ScriptLanguage Language,
    SourceText Text,
    LexResult Lexed,
    PreprocessResult Preprocessed,
    ParseTree Tree,
    ExtractionResult Extraction,
    ImmutableArray<Diagnostic> AllDiagnostics);

/// <summary>
/// The one entry point for per-file analysis: lex → preprocess → parse → extract.
/// Pure and synchronous; the only I/O is the injected insert provider. GSH files run
/// in lenient mode: they are injectable fragments, so parse-stage (3xxx) diagnostics
/// are suppressed while macros/directives still extract fully.
/// </summary>
public static class ScriptAnalysis
{
    public static ParseResult Analyze(
        string filePath,
        ScriptLanguage language,
        SourceText text,
        IInsertProvider insertProvider,
        NameTable names)
    {
        LexResult lexed = Lexer.Lex(text);
        PreprocessResult preprocessed = Preprocessor.Process(filePath, lexed.Tokens, text, insertProvider, names);
        ParseTree tree = Syntax.Parser.Parse(preprocessed.Tokens);
        ExtractionResult extraction = SymbolExtractor.Extract(filePath, tree, preprocessed, lexed.Tokens, text, names);

        bool lenient = language == ScriptLanguage.Gsh;

        ImmutableArray<Diagnostic>.Builder all = ImmutableArray.CreateBuilder<Diagnostic>();
        all.AddRange(lexed.Diagnostics);
        all.AddRange(preprocessed.Diagnostics);

        foreach ( Diagnostic diagnostic in tree.Diagnostics )
        {
            // GSH fragments legitimately fail whole-script parsing; keep them quiet.
            if ( lenient && (int)diagnostic.Code >= 3000 && (int)diagnostic.Code < 4000 )
            {
                continue;
            }

            all.Add(diagnostic);
        }

        if ( !lenient )
        {
            all.AddRange(extraction.Diagnostics);
        }

        // Excluded #if branches grey out. The preprocessor already trimmed these ranges to
        // significant tokens and dropped any coming from inserts, so they map 1:1 onto hints.
        foreach ( TextRange region in preprocessed.DisabledRegions )
        {
            Diagnostic hint = Diagnostic.Create(
                region, DiagnosticSeverity.Hint, GscDiagnosticCode.InactiveConditionalBranch);

            all.Add(hint with { Tags = [DiagnosticTag.Unnecessary] });
        }

        return new ParseResult(filePath, language, text, lexed, preprocessed, tree, extraction, all.ToImmutable());
    }

    /// <summary>Infers the language from a file extension (defaults to GSC).</summary>
    public static ScriptLanguage LanguageFromPath(string filePath)
    {
        return GameProfile.Active.LanguageFromPath(filePath);
    }
}
