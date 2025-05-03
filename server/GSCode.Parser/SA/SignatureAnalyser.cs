
using System.Text;
using GSCode.Data;
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
                case AstNodeType.Namespace:
                    AnalyseNamespace((NamespaceNode)scriptDefn);
                    break;
                case AstNodeType.Dependency:
                    AnalyseDependency((DependencyNode)scriptDefn);
                    break;
            }
        }
    }

    public void AnalyseDependency(DependencyNode dependencyNode)
    {
        // Add the dependency to the list
        DefinitionsTable.AddDependency(dependencyNode.Path);
    }

    public void AnalyseNamespace(NamespaceNode namespaceNode)
    {
        // Change the namespace at this point and onwards
        DefinitionsTable.CurrentNamespace = namespaceNode.NamespaceIdentifier;
    }

    public void AnalyseFunction(FunDefnNode functionDefn)
    {
        // Get the name of the function - if it's unnamed then it's one that was produced in recovery. No use to us.
        if (functionDefn.Name is not Token nameToken)
        {
            return;
        }

        string name = nameToken.Lexeme;

        // Analyze the parameter list
        IEnumerable<ScrParameter> parameters = AnalyseFunctionParameters(functionDefn.Parameters);


        ScrFunction function = new()
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
            Tags = ["userdefined"],
            IsPrivate = functionDefn.Keywords.Keywords.Any(t => t.Type == TokenType.Private),
            IntelliSense = null // I have no idea why this exists
        };

        // Produce a definition for our function
        DefinitionsTable.AddFunction(function, functionDefn);

        Sense.AddSenseToken(nameToken, new ScrFunctionSymbol(nameToken, function));

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

internal record ScrFunctionSymbol(Token NameToken, ScrFunction Source) : ISenseDefinition
{
    public bool IsFromPreprocessor { get; } = false;
    public Range Range { get; } = NameToken.Range;

    public string SemanticTokenType { get; } = "function";
    public string[] SemanticTokenModifiers { get; } = [];

    public Hover GetHover()
    {
        StringBuilder builder = new();

        builder.AppendLine("```gsc");
        builder.Append($"function {Source.Name}(");

        bool first = true;
        foreach (ScrFunctionArg parameter in Source.Args ?? [])
        {
            AppendParameter(builder, parameter, ref first);
        }
        builder.AppendLine(")");
        builder.AppendLine("```");


        return new()
        {
            Range = Range,
            Contents = new MarkedStringsOrMarkupContent(new MarkupContent()
            {
                Kind = MarkupKind.Markdown,
                Value = builder.ToString()
            })
        };
    }

    private static void AppendParameter(StringBuilder builder, ScrFunctionArg parameter, ref bool first)
    {
        if (!first)
        {
            builder.Append(", ");
        }
        first = false;

        if (string.IsNullOrEmpty(parameter.Type) || parameter.Type == "unknown")
        {
            builder.Append($"{parameter.Name}");
            return;
        }

        builder.Append($"/@ {parameter.Type} @/ {parameter.Name}");
    }
}