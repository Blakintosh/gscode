using GSCode.Parser.AST.Nodes;
using GSCode.Parser.Data;
using GSCode.Parser.SPA.Logic.Analysers;
using GSCode.Parser.SPA.Logic.Components;
using GSCode.Parser.SPA;
using GSCode.Parser.Steps.Interfaces;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Globalization;

namespace GSCode.Parser.Steps;

internal class SignatureAnalyserStep : IParserStep, ISenseProvider
{
    public ASTNode ASTRoot { get; }
    public ScriptAnalyserData Data { get; } = new("gsc");

    public ParserIntelliSense Sense { get; }

    public DefinitionsTable DefinitionsTable { get; }

    public SignatureAnalyserStep(ParserIntelliSense sense, DefinitionsTable definitionsTable, ASTNode astRoot)
    {
        Sense = sense;
        ASTRoot = astRoot;
        DefinitionsTable = definitionsTable;
    }

    public async Task RunAsync()
    {
        // Analyse all class, method & function signatures
        await Task.Run(() =>
        {
            AnalyzeSignatures(ASTRoot.Branch!);
        });
    }

    public void AnalyzeSignatures(ASTBranch branch)
    {
        for (int i = 0; i < branch.ChildrenCount; i++)
        {
            ASTNode child = branch.GetChild(i);

            ASTNode? last = i - 1 >= 0 ? branch.GetChild(i - 1) : null;
            ASTNode? next = i + 1 < branch.ChildrenCount ? branch.GetChild(i + 1) : null;

            // Analyse the child for a signature
            if (child.SignatureAnalyzer is SignatureNodeAnalyser analyser)
            {
                analyser.Analyze(child, last, next, DefinitionsTable, Sense);
            }
        }
    }
}
