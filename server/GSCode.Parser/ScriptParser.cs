/**
    GSCode.NET Language Server
    Copyright (C) 2022 Blakintosh

    This program is free software: you can redistribute it and/or modify
    it under the terms of the GNU General Public License as published by
    the Free Software Foundation, either version 3 of the License, or
    (at your option) any later version.

    This program is distributed in the hope that it will be useful,
    but WITHOUT ANY WARRANTY; without even the implied warranty of
    MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
    GNU General Public License for more details.

    You should have received a copy of the GNU General Public License
    along with this program.  If not, see <https://www.gnu.org/licenses/>.
*/
using GSCode.Lexer;
using GSCode.Parser.Data;
using GSCode.Parser.SPA.Logic.Components;
using GSCode.Parser.Steps;
using GSCode.Parser.Steps.Interfaces;
using Microsoft.VisualStudio.LanguageServer.Protocol;
using Serilog;
using System.Diagnostics;

namespace GSCode.Parser;

public class DependencyParser : IScriptParser
{
    public ScriptLexer Lexer { get; }
    public ParserIntelliSense? IntelliSense { get; private set; }
    public DefinitionsTable? DefinitionsTable { get; set; }

    public Uri RootFileUri { get; }

    internal ASTGenerationStep? _astStep = null;
    internal SignatureAnalyserStep? _saStep = null;

    public DependencyParser(ScriptLexer lexer)
    {
        Lexer = lexer;
        RootFileUri = lexer.RootFileUri;
    }

    /// <summary>
    /// Parses the script file.
    /// </summary>
    public Task Parse()
    {
        Stopwatch sw = Stopwatch.StartNew();

        IntelliSense = new(Lexer.EndLine);
        ScriptTokenLinkedList tokens = Lexer.Tokens;

        PreprocessorStep preprocessorStep = new(IntelliSense, Lexer.RootFileUri.LocalPath, tokens);
        preprocessorStep.Run();

        WhitespaceRemovalStep whitespaceRemovalStep = new WhitespaceRemovalStep(tokens);
        whitespaceRemovalStep.Run();

        ASTGenerationStep astStep = new(IntelliSense, Lexer.RootFileUri.LocalPath, tokens);
        astStep.Run();
        _astStep = astStep;

        DefinitionsTable = new(Path.GetFileNameWithoutExtension(Lexer.RootFileUri.LocalPath));
        SignatureAnalyserStep saStep = new(IntelliSense, DefinitionsTable, _astStep.RootNode);
        saStep.Run();
        _saStep = saStep;

        sw.Stop();

        Log.Information("Primary parsing completed in {0}ms.", sw.Elapsed.TotalMilliseconds);
        return Task.CompletedTask;
    }
}

public class ScriptParser : DependencyParser
{
    public ScriptParser(ScriptLexer lexer) : base(lexer) { }

    /// <summary>
    /// Performs the final static analysis & evaluation on the script file, after dependencies have been gathered.
    /// </summary>
    public Task FinishParsing(IEnumerable<IExportedSymbol> exportedSymbols)
    {
        if(IntelliSense is null || _astStep is null || _saStep is null)
        {
            throw new InvalidOperationException("Cannot finish parsing before primary parsing is complete.");
        }

        Stopwatch sw = Stopwatch.StartNew();

        ControlFlowAnalyserStep cfaStep = new ControlFlowAnalyserStep(IntelliSense, _saStep.DefinitionsTable);
        cfaStep.Run();

        DataFlowAnalyser dfaStep = new DataFlowAnalyser(IntelliSense, exportedSymbols, cfaStep.FunctionGraphs);
        dfaStep.Run();

        sw.Stop();

        Log.Information("Secondary parsing completed in {0}ms.", sw.Elapsed.TotalMilliseconds);
        return Task.CompletedTask;
    }
}