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

namespace GSCode.Parser;

public class Script(DocumentUri ScriptUri)
{
    public bool Failed { get; private set; } = false;
    public bool Parsed { get; private set; } = false;
    public bool Analysed { get; private set; } = false;

    internal ParserIntelliSense Sense { get; private set; } = default!;

    private Task? ParsingTask { get; set; } = null;

    public async Task ParseAsync(string documentText)
    {
        ParsingTask = DoParseAsync(documentText);
        await ParsingTask;
    }

    public async Task DoParseAsync(string documentText)
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
            await Console.Error.WriteLineAsync($"Failed to tokenise script: {ex.Message}");

            // Create a dummy IntelliSense container so we can provide an error to the IDE.
            Sense = new(0, ScriptUri);
            Sense.AddIdeDiagnostic(RangeHelper.From(0, 0, 0, 1), GSCErrorCodes.UnhandledLexError, ex.GetType().Name);

            return Task.CompletedTask;
        }

        ParserIntelliSense sense = Sense = new(endLine: endToken.Range.End.Line, ScriptUri);

        // Preprocess the tokens.
        Preprocessor preprocessor = new(startToken, sense);
        try
        {
            preprocessor.Process();
        }
        catch (Exception ex)
        {
            Failed = true;
            await Console.Error.WriteLineAsync($"Failed to preprocess script: {ex.Message}");

            Sense.AddIdeDiagnostic(RangeHelper.From(0, 0, 0, 1), GSCErrorCodes.UnhandledMacError, ex.GetType().Name);
            return Task.CompletedTask;
        }

        // Build a library of tokens so IntelliSense can quickly lookup a token at a given position.
        Sense.CommitTokens(startToken);

        // Build the AST.
        AST.Parser parser = new(startToken, sense);
        ScriptNode rootNode;

        try
        {
            rootNode = parser.Parse();
        }
        catch (Exception ex)
        {
            Failed = true;
            await Console.Error.WriteLineAsync($"Failed to AST-gen script: {ex.Message}");

            Sense.AddIdeDiagnostic(RangeHelper.From(0, 0, 0, 1), GSCErrorCodes.UnhandledAstError, ex.GetType().Name);
            return Task.CompletedTask;
        }

        Parsed = true;
        return Task.CompletedTask;
    }

    public async Task AnalyseAsync()
    {
        await WaitUntilParsedAsync();
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
        
        foreach(ISemanticToken token in Sense.SemanticTokens)
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
        return Sense.GetCompletionsFromPosition(position);
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

    //private void ThrowIfNotAnalysed()
    //{
    //    if (!Analysed)
    //    {
    //        throw new InvalidOperationException("The script has not been analysed yet.");
    //    }
    //}
}
