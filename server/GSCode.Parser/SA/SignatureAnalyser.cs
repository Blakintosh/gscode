
using GSCode.Parser.AST;
using GSCode.Parser.Data;
using GSCode.Parser.DFA;
using GSCode.Parser.Lexical;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;

namespace GSCode.Parser.SA;

internal ref struct SignatureAnalyser(ScriptNode rootNode, DefinitionsTable definitionsTable, ParserIntelliSense sense)
{
    private ScriptNode RootNode { get; } = rootNode;
    private DefinitionsTable DefinitionsTable { get; } = definitionsTable;
    private ParserIntelliSense Sense { get; } = sense;

    public void Analyse()
    {
        foreach (AstNode scriptDefn in RootNode.ScriptDefns)
        {
            switch (scriptDefn.NodeType)
            {
                case AstNodeType.FunctionDefinition:
                    AnalyseFunction((FunDefnNode)scriptDefn);
                    break;
            }
        }
    }

    public void AnalyseFunction(FunDefnNode functionDefn)
    {
        // Get the name of the function - if it's unnamed then it's one that was produced in recovery. No use to us.
        string? name = functionDefn.Name?.Lexeme;

        if (name is null)
        {
            return;
        }

        // Analyze the parameter list
        IEnumerable<ScrParameter> parameters = AnalyseFunctionParameters(functionDefn.Parameters);

        // Produce a definition for our function
        DefinitionsTable.AddFunction(new ScrFunction()
        {
            Name = name,
            Description = null, // TODO: Check the DOC COMMENT
            Args = GetParametersAsRecord(parameters),
            CalledOn = new ScrFunctionArg()
            {
                Name = "unk",
                Required = false
            }, // TODO: Check the DOC COMMENT
            Returns = new ScrFunctionArg()
            {
                Name = "unk",
                Required = false
            }, // TODO: Check the DOC COMMENT
            Tags = new() { "userdefined" },
            IntelliSense = null // I have no idea why this exists
        }, functionDefn);

        if (parameters is not null)
        {
            foreach (ScrParameter parameter in parameters)
            {
                Sense.AddSenseToken(parameter.Source, new ScrParameterSymbol(parameter));
            }
        }
    }

    private static List<ScrFunctionArg>? GetParametersAsRecord(IEnumerable<ScrParameter> parameters)
    {
        List<ScrFunctionArg> result = new();
        foreach (ScrParameter parameter in parameters)
        {
            result.Add(new ScrFunctionArg()
            {
                Name = parameter.Name,
                Description = null, // TODO: Check the DOC COMMENT
                Type = "unknown", // TODO: Check the DOC COMMENT
                Required = parameter.Default is null,
                Default = null // Not sure we can populate this
            });
        }

        return result;
    }

    private List<ScrParameter> AnalyseFunctionParameters(ParamListNode parameters)
    {
        List<ScrParameter> result = new();
        foreach (ParamNode parameter in parameters.Parameters)
        {
            if (parameter.Name is not Token nameToken)
            {
                continue;
            }

            string name = parameter.Name.Lexeme;
            bool byRef = parameter.ByRef;

            if (parameter.Default is null)
            {
                result.Add(new ScrParameter(name, nameToken, nameToken.Range, byRef));
            }

            // TODO: do we need to handle defaults now, or leave till later?
            result.Add(new ScrParameter(name, nameToken, nameToken.Range, byRef, parameter.Default));
        }

        return result;
    }
}


/// <summary>
/// Records the definition of a function parameter for semantics & hovers
/// </summary>
/// <param name="Source">The parameter source</param>
internal record ScrParameterSymbol(ScrParameter Source) : ISenseDefinition
{
    // I'm pretty sure this is redundant
    public bool IsFromPreprocessor { get; } = false;
    public Range Range { get; } = Source.Range;

    public string SemanticTokenType { get; } = "parameter";
    public string[] SemanticTokenModifiers { get; } = [];

    public Hover GetHover()
    {
        string parameterName = $"{Source.Name}";
        return new()
        {
            Range = Range,
            Contents = new MarkedStringsOrMarkupContent(new MarkupContent()
            {
                Kind = MarkupKind.Markdown,
                Value = string.Format("```gsc\n{0}\n```",
                   parameterName)
            })
        };
    }
}