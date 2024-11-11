using GSCode.Parser.AST.Expressions;
using GSCode.Parser.Data;
using GSCode.Parser.DFA;
using GSCode.Parser.SPA.Logic.Analysers;
using GSCode.Parser.Util;
using Microsoft.VisualStudio.LanguageServer.Protocol;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection.Metadata;
using System.Text;
using System.Threading.Tasks;
using System.Xml.Linq;

#if PREVIEW

namespace GSCode.Parser.SPA;

public class ScrVariableSymbol : ISenseToken
{
    public Range Range { get; }

    public string SemanticTokenType { get; } = "variable";

    public string[] SemanticTokenModifiers { get; private set; } = Array.Empty<string>();

    internal TokenNode Node { get; }
    internal string TypeString { get; }

    public bool Constant { get; private set; } = false;

    internal ScrVariableSymbol(TokenNode node, ScrData data)
    {
        Node = node;
        TypeString = data.TypeToString();
        Range = node.Range;
    }

    internal static ScrVariableSymbol Declaration(TokenNode node, ScrData data, bool isConstant = false)
    {
        return new(node, data)
        {
            SemanticTokenModifiers = isConstant ?
                new string[] { "declaration", "readonly", "local" } :
                new string[] { "declaration", "local" }
        };
    }

    internal static ScrVariableSymbol LanguageSymbol(TokenNode node, ScrData data)
    {
        return new(node, data)
        {
            SemanticTokenModifiers = new string[] { "defaultLibrary" }
        };
    }

    internal static ScrVariableSymbol Usage(TokenNode node, ScrData data)
    {
        bool isConstant = data.ReadOnly;

        return new(node, data)
        {
            SemanticTokenModifiers = isConstant ?
                new string[] { "readonly", "local" } :
                new string[] { "local" }
        };
    }

    public Hover GetHover()
    {
        string typeValue = $"{(Constant ? "const " : string.Empty)}{TypeString}";
        return new()
        {
            Range = Range,
            Contents = new MarkupContent()
            {
                Kind = MarkupKind.Markdown,
                Value = string.Format("```gsc\n/@ {0} @/ {1}\n```",
                   typeValue, Node.SourceToken.Contents!)
            }
        };
    }
}
public class ScrParameterSymbol : ISenseToken
{
    public Range Range { get; }

    public string SemanticTokenType { get; } = "parameter";

    public string[] SemanticTokenModifiers { get; private set; } = Array.Empty<string>();

    internal ScrParameter Source { get; }

    internal ScrParameterSymbol(ScrParameter parameter)
    {
        Source = parameter;
        Range = parameter.Range;

        // Add default library if it's the vararg declaration
        if(parameter.Name == "vararg")
        {
            SemanticTokenModifiers = new string[] { "defaultLibrary" };
        }
    }

    public Hover GetHover()
    {
        string parameterName = $"{Source.Name}";
        return new()
        {
            Range = Range,
            Contents = new MarkupContent()
            {
                Kind = MarkupKind.Markdown,
                Value = string.Format("```gsc\n{0}\n```",
                   parameterName)
                //Value = string.Format("```gsc\n/@ {0} @/ {1}\n```",
                //   typeValue, Node.SourceToken.Contents!)
            }
        };
    }
}


public class ScrPropertySymbol : ISenseToken
{
    public Range Range { get; }

    public string SemanticTokenType { get; } = "property";

    public string[] SemanticTokenModifiers { get; } = Array.Empty<string>();

    internal TokenNode Node { get; }
    internal ScrData Value { get; }

    public bool ReadOnly { get; private set; } = false;

    internal ScrPropertySymbol(TokenNode node, ScrData value, bool isReadOnly = false)
    {
        Node = node;
        Value = value;
        Range = node.Range;
        if (!isReadOnly)
        {
            return;
        }
        SemanticTokenModifiers = new string[] { "readonly" };
        ReadOnly = true;
    }

    public Hover GetHover()
    {
        string typeValue = $"{(ReadOnly ? "readonly " : string.Empty)}{Value.TypeToString()}";
        return new()
        {
            Range = Range,
            Contents = new MarkupContent()
            {
                Kind = MarkupKind.Markdown,
                Value = string.Format("```gsc\n(property) /@ {0} @/ {1}\n```",
                   typeValue, Node.SourceToken.Contents!)
            }
        };
    }
}

#endif