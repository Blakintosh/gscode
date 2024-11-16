﻿using GSCode.Data;
using GSCode.Parser.AST;
using GSCode.Parser.Data;
using GSCode.Parser.Lexical;
using GSCode.Parser.Pre;
using Microsoft.VisualStudio.LanguageServer.Protocol;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace GSCode.Parser;

public class Script(Uri ScriptUri)
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
            Console.Error.WriteLine($"Failed to preprocess script: {ex.Message}");

            Sense.AddIdeDiagnostic(RangeHelper.From(0, 0, 0, 1), GSCErrorCodes.UnhandledMacError, ex.GetType().Name);
            return Task.CompletedTask;
        }

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
            Console.Error.WriteLine($"Failed to AST-gen script: {ex.Message}");

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

    public async Task<List<Diagnostic>> GetDiagnosticsAsync()
    {
        // TODO: maybe a mechanism to check if analysed if that's a requirement

        // We still expose diagnostics even if the script failed to parse
        await WaitUntilParsedAsync();
        return Sense.Diagnostics;
    }

    public async Task<List<ISemanticToken>> GetSemanticTokensAsync()
    {
        await WaitUntilParsedAsync();
        return Sense.SemanticTokens;
    }

    public async Task<Hover?> GetHoverAsync(Position position)
    {
        await WaitUntilParsedAsync();

        IHoverable? result = Sense.HoverLibrary.Get(position);
        if (result is not null)
        {
            return result.GetHover();
        }
        return null;
    }

    private async Task WaitUntilParsedAsync()
    {
        if (ParsingTask is null)
        {
            throw new InvalidOperationException("The script has not been parsed yet.");
        }
        await ParsingTask;
    }

    //private void ThrowIfNotAnalysed()
    //{
    //    if (!Analysed)
    //    {
    //        throw new InvalidOperationException("The script has not been analysed yet.");
    //    }
    //}
}
