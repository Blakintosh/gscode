using System.Text;
using GSCode.Data;
using GSCode.Parser.AST;
using GSCode.Parser.Data;
using GSCode.Parser.DFA;
using GSCode.Parser.Lexical;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using System.Text.RegularExpressions;

namespace GSCode.Parser.SA;

internal ref struct SignatureAnalyser(ScriptNode rootNode, DefinitionsTable definitionsTable, ParserIntelliSense sense)
{
    private static readonly Regex s_kv = new(@"^(?<k>\w+)\s*:\s*(?<v>.+)$", RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    // Accept <arg> desc, <arg>: desc, [arg] desc, [arg]: desc, or bareword desc
    private static readonly Regex s_argPattern = new(@"^(?<n><[^>]+>|\[[^\]]+\]|[^:\s]+)\s*:??\s*(?<d>.+)$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

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

        // Add class to definitions table for CFG and RDA analysis
        DefinitionsTable.AddClass(scrClass, classDefn);

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
            Overloads = [
                new ScrFunctionOverload()
                {
                    Parameters = GetParametersAsRecord(parameters)!,
                    CalledOn = new ScrFunctionArg()
                    {
                        Name = "unk",
                        Mandatory = false
                    }, // TODO: Check the DOC COMMENT
                    Returns = null!,
                    Vararg = functionDefn.Parameters.Vararg
                }
            ],

            Flags = ["userdefined"],
            Private = functionDefn.Keywords.Keywords.Any(t => t.Type == TokenType.Private)
        };

        // Produce a definition for our function
        scrClass.Methods.Add(function);

        // Record method/function location (method recorded as function under containing namespace)
        DefinitionsTable.AddFunctionLocation(DefinitionsTable.CurrentNamespace, name, Sense.ScriptPath, nameToken.Range);
        // Record parameter names for outline/signature
        DefinitionsTable.RecordFunctionParameters(DefinitionsTable.CurrentNamespace, name, (function.Overloads[0].Parameters ?? new List<ScrFunctionArg>()).Select(a => a.Name));
        // Record flags (private, autoexec)
        var flags = new List<string>();
        if (function.Private) flags.Add("private");
        if (functionDefn.Keywords.Keywords.Any(t => t.Type == TokenType.Autoexec)) flags.Add("autoexec");
        DefinitionsTable.RecordFunctionFlags(DefinitionsTable.CurrentNamespace, name, flags);

        // Record doc comment if present
        string? doc = ExtractDocCommentBefore(nameToken);
        DefinitionsTable.RecordFunctionDoc(DefinitionsTable.CurrentNamespace, name, doc);

        // NEW: Also record under the class name as its own qualifier so ClassName::Method() resolves
        string classNs = scrClass.Name;
        DefinitionsTable.AddFunctionLocation(classNs, name, Sense.ScriptPath, nameToken.Range);
        DefinitionsTable.RecordFunctionParameters(classNs, name, (function.Overloads[0].Parameters ?? new List<ScrFunctionArg>()).Select(a => a.Name));
        DefinitionsTable.RecordFunctionFlags(classNs, name, flags);
        DefinitionsTable.RecordFunctionDoc(classNs, name, doc);

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
            Overloads = [
                new ScrFunctionOverload()
                {
                    CalledOn = null, // TODO: Check the DOC COMMENT
                    Parameters = GetParametersAsRecord(parameters)!,
                    Returns = null!, // TODO: Check the DOC COMMENT
                    Vararg = functionDefn.Parameters.Vararg
                }
            ],
            Flags = ["userdefined"],
            Private = functionDefn.Keywords.Keywords.Any(t => t.Type == TokenType.Private),
            DocComment = ExtractDocCommentBefore(nameToken)
        };

        // Produce a definition for our function
        DefinitionsTable.AddFunction(function, functionDefn);

        // Record function location for go-to-definition
        DefinitionsTable.AddFunctionLocation(DefinitionsTable.CurrentNamespace, name, Sense.ScriptPath, nameToken.Range);
        // Record parameter names for outline/signature
        DefinitionsTable.RecordFunctionParameters(DefinitionsTable.CurrentNamespace, name, (function.Overloads[0].Parameters ?? new List<ScrFunctionArg>()).Select(a => a.Name));
        // Record flags (private, autoexec)
        var flags = new List<string>();
        if (function.Private) flags.Add("private");
        if (functionDefn.Keywords.Keywords.Any(t => t.Type == TokenType.Autoexec)) flags.Add("autoexec");
        DefinitionsTable.RecordFunctionFlags(DefinitionsTable.CurrentNamespace, name, flags);

        // Record doc comment if present
        DefinitionsTable.RecordFunctionDoc(DefinitionsTable.CurrentNamespace, name, function.DocComment);

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
                Type = null, // TODO: Check the DOC COMMENT
                Mandatory = parameter.Default is null,
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
        while (nameToken.Range.Start.Line > 0 && nameToken.Type != TokenType.LineBreak)
        {
            nameToken = nameToken.Previous;
        }

        if (nameToken.Previous == null)
        {
            return null;
        }

        // check around 50 tokens
        int count = 50;
        while (count > 0 && nameToken.Previous.Type != TokenType.DocComment)
        {
            nameToken = nameToken.Previous;
            count--;

            if (nameToken.Previous == null)
            {
                return null;
            }
        }

        if (nameToken.Previous.Type == TokenType.DocComment)
        {
            return SanitizeDocForMarkdown(nameToken.Previous.Lexeme);
        }

        return null;
    }

    private static string SanitizeDocForMarkdown(string lexeme)
    {
        if (string.IsNullOrWhiteSpace(lexeme)) return string.Empty;
        string s = lexeme;

        // Strip common block wrappers
        if (s.StartsWith("/@"))
        {
            if (s.EndsWith("@/")) s = s.Substring(2, s.Length - 4);
            else s = s.Substring(2);
        }
        else if (s.StartsWith("/*"))
        {
            if (s.EndsWith("*/")) s = s.Substring(2, s.Length - 4);
            else s = s.Substring(2);
        }

        // Normalize CRLF to LF and unescape common sequences (\\n, \\r, \\t, \\")
        s = s.Replace("\r\n", "\n").Replace("\r", "\n");
        s = s.Replace("\\n", "\n").Replace("\\r", string.Empty).Replace("\\t", "    ").Replace("\\\"", "\"");

        // Split and clean each line: remove leading *, trim, remove surrounding quotes per line
        string[] rawLines = s.Split('\n');
        List<string> lines = new();
        foreach (var line in rawLines)
        {
            string l = line.Trim();
            if (l.StartsWith("*")) l = l.TrimStart('*').TrimStart();
            if (l.Length >= 2 && l[0] == '"' && l[^1] == '"')
            {
                l = l.Substring(1, l.Length - 2);
            }
            // Avoid stray Markdown code fence terminators
            l = l.Replace("```", "`\u200B``");
            if (l.Length > 0) lines.Add(l);
        }

        // Parse into fields
        string? name = null, summary = null, module = null, callOn = null, spmp = null;
        var mandatory = new List<(string Arg, string Desc)>();
        var optional = new List<(string Arg, string Desc)>();
        var examples = new List<string>();

        foreach (var l in lines)
        {
            var m = s_kv.Match(l);
            if (!m.Success) continue;
            string key = m.Groups["k"].Value.Trim().ToLowerInvariant();
            string val = m.Groups["v"].Value.Trim();

            switch (key)
            {
                case "name":
                    name = val;
                    break;
                case "summary":
                    summary = val;
                    break;
                case "module":
                    module = val;
                    break;
                case "callon":
                    callOn = string.IsNullOrWhiteSpace(val) ? "UNKNOWN" : val;
                    break;
                case "spmp":
                    spmp = val;
                    break;
                case "mandatoryarg":
                    {
                        var am = s_argPattern.Match(val);
                        if (am.Success)
                        {
                            string a = am.Groups["n"].Value.Trim();
                            string d = am.Groups["d"].Value.Trim();

                            a = a.Replace("<", "").Replace(">", "");
                            a = a.Replace("[", "").Replace("]", "");

                            mandatory.Add((a, d));
                        }
                        break;
                    }
                case "optionalarg":
                    {
                        var am = s_argPattern.Match(val);
                        if (am.Success)
                        {
                            string a = am.Groups["n"].Value.Trim();
                            string d = am.Groups["d"].Value.Trim();

                            a = a.Replace("<", "").Replace(">", "");
                            a = a.Replace("[", "").Replace("]", "");

                            optional.Add((a, d));
                        }
                        break;
                    }
                case "example":
                    examples.Add(val);
                    break;
            }
        }

        // If parsing found nothing significant, fall back to cleaned plain text
        if (name is null && summary is null && module is null && callOn is null && spmp is null && mandatory.Count == 0 && optional.Count == 0 && examples.Count == 0)
        {
            return string.Join('\n', lines).Trim();
        }

        // Render Markdown
        StringBuilder sb = new();

        if (!string.IsNullOrWhiteSpace(name))
        {
            sb.AppendLine("```gsc");
            sb.AppendLine(name);
            sb.AppendLine("```");
            sb.AppendLine("---");
        }

        if (!string.IsNullOrWhiteSpace(summary))
        {
            sb.AppendLine($"### _{summary}_");
            sb.AppendLine("---");
        }

        if (!string.IsNullOrWhiteSpace(module) || !string.IsNullOrWhiteSpace(callOn) || !string.IsNullOrWhiteSpace(spmp))
        {
            if (!string.IsNullOrWhiteSpace(module)) sb.AppendLine($"- Module: ```{module}```");
            if (!string.IsNullOrWhiteSpace(callOn)) sb.AppendLine($"- CallOn: ```{callOn}```");
            if (!string.IsNullOrWhiteSpace(spmp)) sb.AppendLine($"- SPMP: ```{spmp}```");
            sb.AppendLine("---");
        }

        if (mandatory.Count > 0 || optional.Count > 0)
        {
            sb.AppendLine("### Parameters");
            if (mandatory.Count > 0)
            {
                sb.AppendLine("- Mandatory");
                foreach (var (a, d) in mandatory)
                {
                    sb.AppendLine($"  - `<{a}>` — {d}");
                }
            }
            if (optional.Count > 0)
            {
                sb.AppendLine("- Optional");
                foreach (var (a, d) in optional)
                {
                    sb.AppendLine($"  - `[{a}]` — {d}");
                }
            }
            sb.AppendLine("---");
        }

        foreach (var ex in examples)
        {
            sb.AppendLine("Example");
            sb.AppendLine("```gsc");
            sb.AppendLine(ex);
            sb.AppendLine("```");
        }

        return sb.ToString().Trim();
    }

    private static string? BuildPrototype(string? ns, string name, IEnumerable<ScrFunctionArg>? args)
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
        // Prefer user doc comment, if present
        if (!string.IsNullOrWhiteSpace(Source.DocComment))
        {
            return new()
            {
                Range = Range,
                Contents = new MarkedStringsOrMarkupContent(new MarkupContent()
                {
                    Kind = MarkupKind.Markdown,
                    Value = Source.DocComment
                })
            };
        }

        StringBuilder builder = new();

        builder.AppendLine("```gsc");
        builder.Append($"function {Source.Name}(");

        bool first = true;
        foreach (ScrFunctionArg parameter in Source.Overloads[0].Parameters ?? [])
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
                Value = Source.Documentation
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

        if (parameter.Type is null)
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
        foreach (ScrFunctionArg parameter in Source.Overloads.First().Parameters)
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
