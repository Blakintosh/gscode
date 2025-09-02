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
        foreach (AstNode scriptDependency in RootNode.Dependencies)
        {
            switch (scriptDependency.NodeType)
            {
                case AstNodeType.Dependency:
                    AnalyseDependency((DependencyNode)scriptDependency);
                    break;
            }
        }

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
                case AstNodeType.ClassDefinition:
                    AnalyseClass((ClassDefnNode)scriptDefn);
                    break;
            }
        }
    }

    public void AnalyseClass(ClassDefnNode classDefn)
    {
        if (classDefn.NameToken is not Token nameToken)
        {
            return;
        }

        string name = nameToken.Lexeme;

        // Create a class definition
        ScrClass scrClass = new()
        {
            Name = name,
            Description = null, // TODO: Check the DOC COMMENT
            InheritsFrom = classDefn.InheritsFromToken?.Lexeme,
        };

        foreach (AstNode child in classDefn.Body.Definitions)
        {
            switch (child.NodeType)
            {
                case AstNodeType.FunctionDefinition:
                    AnalyseClassFunction(scrClass, (FunDefnNode)child);
                    break;
                case AstNodeType.ClassMember:
                    AnalyseClassMember(scrClass, (MemberDeclNode)child);
                    break;
            }
        }

        // Record class location for go-to-definition
        DefinitionsTable.AddClassLocation(DefinitionsTable.CurrentNamespace, name, Sense.ScriptPath, nameToken.Range);

        Sense.AddSenseToken(nameToken, new ScrClassSymbol(nameToken, scrClass));
    }

    private void AnalyseClassFunction(ScrClass scrClass, FunDefnNode functionDefn)
    {
        if (functionDefn.Name is not Token nameToken)
        {
            return;
        }

        string name = nameToken.Lexeme;

        // Analyze the parameter list
        IEnumerable<ScrParameter> parameters = AnalyseFunctionParameters(functionDefn.Parameters);

        // TODO: Probably needs to be a ScrMethod instead.
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
        scrClass.Methods.Add(function);

        // Record method/function location (method recorded as function under containing namespace)
        DefinitionsTable.AddFunctionLocation(DefinitionsTable.CurrentNamespace, name, Sense.ScriptPath, nameToken.Range);
        // Record parameter names for outline/signature
        DefinitionsTable.RecordFunctionParameters(DefinitionsTable.CurrentNamespace, name, (function.Args ?? new List<ScrFunctionArg>()).Select(a => a.Name));
        // Record flags (private, autoexec)
        var flags = new List<string>();
        if (function.IsPrivate) flags.Add("private");
        if (functionDefn.Keywords.Keywords.Any(t => t.Type == TokenType.Autoexec)) flags.Add("autoexec");
        DefinitionsTable.RecordFunctionFlags(DefinitionsTable.CurrentNamespace, name, flags);

        // Record doc comment if present
        DefinitionsTable.RecordFunctionDoc(DefinitionsTable.CurrentNamespace, name, ExtractDocCommentBefore(nameToken));

        Sense.AddSenseToken(nameToken, new ScrMethodSymbol(nameToken, function, scrClass));

        if (parameters is not null)
        {
            foreach (ScrParameter parameter in parameters)
            {
                Sense.AddSenseToken(parameter.Source, new ScrParameterSymbol(parameter));
            }
        }
    }

    private void AnalyseClassMember(ScrClass scrClass, MemberDeclNode memberDecl)
    {
        if (memberDecl.NameToken is not Token nameToken)
        {
            return;
        }

        ScrMember member = new()
        {
            Name = memberDecl.NameToken?.Lexeme ?? "",
            Description = null // TODO: Check the DOC COMMENT
        };

        scrClass.Members.Add(member);
        Sense.AddSenseToken(nameToken, new ScrClassMemberSymbol(nameToken, member, scrClass));
    }

    public void AnalyseDependency(DependencyNode dependencyNode)
    {
        string? dependencyPath = Sense.GetDependencyPath(dependencyNode.Path, dependencyNode.Range);
        if (dependencyPath is null)
        {
            return;
        }

        Sense.AddSenseToken(dependencyNode.FirstPathToken, new ScrDependencySymbol(dependencyNode.Range, dependencyPath, dependencyNode.Path));

        // Add the dependency to the list
        DefinitionsTable.AddDependency(dependencyPath);
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

        // Record function location for go-to-definition
        DefinitionsTable.AddFunctionLocation(DefinitionsTable.CurrentNamespace, name, Sense.ScriptPath, nameToken.Range);
        // Record parameter names for outline/signature
        DefinitionsTable.RecordFunctionParameters(DefinitionsTable.CurrentNamespace, name, (function.Args ?? new List<ScrFunctionArg>()).Select(a => a.Name));
        // Record flags (private, autoexec)
        var flags = new List<string>();
        if (function.IsPrivate) flags.Add("private");
        if (functionDefn.Keywords.Keywords.Any(t => t.Type == TokenType.Autoexec)) flags.Add("autoexec");
        DefinitionsTable.RecordFunctionFlags(DefinitionsTable.CurrentNamespace, name, flags);

        // Record doc comment if present
        DefinitionsTable.RecordFunctionDoc(DefinitionsTable.CurrentNamespace, name, ExtractDocCommentBefore(nameToken));

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
                continue;
            }

            // TODO: do we need to handle defaults now, or leave till later?
            result.Add(new ScrParameter(name, nameToken, nameToken.Range, byRef, parameter.Default));
        }

        return result;
    }

    private static string? ExtractDocCommentBefore(Token nameToken)
    {
        // Look left for a doc comment immediately preceding the function name line.
        // Accept tokens of type DocComment or MultilineComment (wrapped in /# #/ or /@ @/ as per request)
        Token? t = nameToken.Previous;
        int currentLine = nameToken.Range.Start.Line;
        while (t is not null && t.Range.End.Line >= currentLine - 2)
        {
            if (t.Type == TokenType.DocComment || t.Type == TokenType.MultilineComment)
            {
                return t.Lexeme.Trim();
            }
            if (!t.IsWhitespacey() && !t.IsComment()) break;
            t = t.Previous;
        }
        return null;
    }

    private static string BuildPrototype(string? ns, string name, IEnumerable<ScrFunctionArg>? args)
    {
        string paramList = args is null ? string.Empty : string.Join(", ", args.Select(a => a.Name));
        string nsPrefix = string.IsNullOrEmpty(ns) ? string.Empty : ns + "::";
        return $"function {nsPrefix}{name}({paramList})";
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
    public virtual bool IsFromPreprocessor { get; } = false;
    public virtual Range Range { get; } = NameToken.Range;

    public virtual string SemanticTokenType { get; } = "function";
    public virtual string[] SemanticTokenModifiers { get; } = [];

    public virtual Hover GetHover()
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

    protected static void AppendParameter(StringBuilder builder, ScrFunctionArg parameter, ref bool first)
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

internal record ScrClassSymbol(Token NameToken, ScrClass Source) : ISenseDefinition
{
    public bool IsFromPreprocessor { get; } = false;
    public Range Range { get; } = NameToken.Range;

    public string SemanticTokenType { get; } = "class";
    public string[] SemanticTokenModifiers { get; } = [];

    public Hover GetHover()
    {
        return new()
        {
            Range = Range,
            Contents = new MarkedStringsOrMarkupContent(new MarkupContent()
            {
                Kind = MarkupKind.Markdown,
                Value = "```gsc\nclass " + Source.Name + "\n```"
            })
        };
    }
}


internal record ScrClassMemberSymbol(Token NameToken, ScrMember Source, ScrClass ClassSource) : ISenseDefinition
{
    public bool IsFromPreprocessor { get; } = false;
    public Range Range { get; } = NameToken.Range;

    public string SemanticTokenType { get; } = "property";
    public string[] SemanticTokenModifiers { get; } = [];

    public Hover GetHover()
    {
        return new()
        {
            Range = Range,
            Contents = new MarkedStringsOrMarkupContent(new MarkupContent()
            {
                Kind = MarkupKind.Markdown,
                Value = $"```gsc\n{ClassSource.Name}.{Source.Name}\n```"
            })
        };
    }
}

internal record ScrMethodSymbol(Token NameToken, ScrFunction Source, ScrClass ClassSource) : ScrFunctionSymbol(NameToken, Source)
{
    public override bool IsFromPreprocessor { get; } = false;
    public override Range Range { get; } = NameToken.Range;

    public override string SemanticTokenType { get; } = "method";
    public override string[] SemanticTokenModifiers { get; } = [];

    public override Hover GetHover()
    {
        StringBuilder builder = new();

        builder.AppendLine("```gsc");
        builder.Append($"{ClassSource.Name}::{Source.Name}(");

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
}

internal record ScrDependencySymbol(Range Range, string Path, string RawPath) : ISenseDefinition
{
    public bool IsFromPreprocessor { get; } = false;
    public Range Range { get; } = Range;

    public string SemanticTokenType { get; } = "string";
    public string[] SemanticTokenModifiers { get; } = [];

    public Hover GetHover()
    {
        return new()
        {
            Range = Range,
            Contents = new MarkedStringsOrMarkupContent(new MarkupContent()
            {
                Kind = MarkupKind.Markdown,
                Value = $"```gsc\n#using {RawPath}\n/* (script) \"{Path}\" */\n```"
            })
        };
    }
}
