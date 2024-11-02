using GSCode.Parser.AST;
using GSCode.Parser.Data;
using GSCode.Parser.Lexical;
using Microsoft.VisualStudio.LanguageServer.Protocol;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace GSCode.Parser;

public class Script
{
    public bool Parsed { get; private set; } = false;
    public bool Analysed { get; private set; } = false;

    public ParserIntelliSense Sense { get; private set; } = default!;

    public Task ParseAsync(string documentText)
    {
        // Transform the document text into a token sequence
        Lexer lexer = new(documentText.AsSpan());
        (Token startToken, Token endToken) = lexer.Transform();

        ParserIntelliSense sense = Sense = new(endLine: endToken.Range.End.Line);

        // Build the AST.
        AST.Parser parser = new(startToken, sense);
        ScriptNode rootNode = parser.Parse();

        Parsed = true;
        return Task.CompletedTask;
    }

    public void Analyse()
    {
        ThrowIfNotParsed();
    }

    public IEnumerable<Diagnostic> GetDiagnostics()
    {
        // TODO: maybe a mechanism to check if analysed if that's a requirement
        ThrowIfNotParsed();
        return Sense.Diagnostics;
    }

    private void ThrowIfNotParsed()
    {
        if (!Parsed)
        {
            throw new InvalidOperationException("The script has not been parsed yet.");
        }
    }

    private void ThrowIfNotAnalysed()
    {
        if (!Analysed)
        {
            throw new InvalidOperationException("The script has not been analysed yet.");
        }
    }
}
