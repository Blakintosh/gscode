using GSCode.Parser.AST;
using GSCode.Parser.AST.Expressions;
using GSCode.Parser.Data;
using GSCode.Parser.DFA;
using GSCode.Parser.Lexical;
using GSCode.Parser.SPA.Logic.Analysers;
using GSCode.Parser.Util;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection.Metadata;
using System.Text;
using System.Threading.Tasks;
using System.Xml.Linq;

namespace GSCode.Parser.SPA;

public class ScrVariableSymbol : ISenseDefinition
{
    public Range Range { get; }

    public string SemanticTokenType { get; } = "variable";

    public string[] SemanticTokenModifiers { get; private set; } = [];
    public bool IsFromPreprocessor { get; } = false;

    internal Token IdentifierToken { get; }
    internal string TypeString { get; }

    public bool IsConstant { get; private set; } = false;

    internal ScrVariableSymbol(Token identifierToken, ScrData data)
    {
        IdentifierToken = identifierToken;
        TypeString = data.TypeToString();
        Range = identifierToken.Range;
    }

    internal static ScrVariableSymbol Declaration(IdentifierExprNode node, ScrData data)
    {
        return new(node.Token, data)
        {
            SemanticTokenModifiers =
                new string[] { "declaration", "local" },
        };
    }

    internal static ScrVariableSymbol ConstantDeclaration(Token identifierToken, ScrData data)
    {
        return new(identifierToken, data)
        {
            SemanticTokenModifiers = new string[] { "declaration", "readonly", "local" },
            IsConstant = true
        };
    }

    internal static ScrVariableSymbol LanguageSymbol(IdentifierExprNode node, ScrData data)
    {
        return new(node.Token, data)
        {
            SemanticTokenModifiers = new string[] { "defaultLibrary" }
        };
    }

    internal static ScrVariableSymbol Usage(IdentifierExprNode node, ScrData data)
    {
        bool isConstant = data.ReadOnly;

        return new(node.Token, data)
        {
            SemanticTokenModifiers = isConstant ?
                new string[] { "readonly", "local" } :
                new string[] { "local" }
        };
    }

    public Hover GetHover()
    {
        string typeValue = $"{(IsConstant ? "const " : string.Empty)}{TypeString}";
        return new()
        {
            Range = Range,
            Contents = new MarkedStringsOrMarkupContent(new MarkupContent()
            {
                Kind = MarkupKind.Markdown,
                Value = string.Format("```gsc\n/@ {0} @/ {1}\n```",
                   typeValue, IdentifierToken.Lexeme)
            })
        };
    }
}
// public class ScrParameterSymbol : ISenseToken
// {
//     public Range Range { get; }

//     public string SemanticTokenType { get; } = "parameter";

//     public string[] SemanticTokenModifiers { get; private set; } = Array.Empty<string>();

//     internal ScrParameter Source { get; }

//     internal ScrParameterSymbol(ScrParameter parameter)
//     {
//         Source = parameter;
//         Range = parameter.Range;

//         // Add default library if it's the vararg declaration
//         if(parameter.Name == "vararg")
//         {
//             SemanticTokenModifiers = new string[] { "defaultLibrary" };
//         }
//     }

//     public Hover GetHover()
//     {
//         string parameterName = $"{Source.Name}";
//         return new()
//         {
//             Range = Range,
//             Contents = new MarkupContent()
//             {
//                 Kind = MarkupKind.Markdown,
//                 Value = string.Format("```gsc\n{0}\n```",
//                    parameterName)
//                 //Value = string.Format("```gsc\n/@ {0} @/ {1}\n```",
//                 //   typeValue, Node.SourceToken.Contents!)
//             }
//         };
//     }
// }


public class ScrFieldSymbol : ISenseDefinition
{
    public Range Range { get; }

    public string SemanticTokenType { get; } = "field";

    public string[] SemanticTokenModifiers { get; private set; } = [];
    public bool IsFromPreprocessor { get; } = false;

    internal IdentifierExprNode Node { get; }
    internal string TypeString { get; }

    public bool ReadOnly { get; private set; } = false;

    internal ScrFieldSymbol(IdentifierExprNode node, ScrData data, bool isReadOnly = false)
    {
        Node = node;
        Range = node.Range;
        TypeString = data.TypeToString();
        if (!isReadOnly)
        {
            return;
        }
        SemanticTokenModifiers = new string[] { "readonly" };
        ReadOnly = true;
    }

    public Hover GetHover()
    {
        string typeValue = $"{(ReadOnly ? "readonly " : string.Empty)}{TypeString}";
        return new()
        {
            Range = Range,
            Contents = new MarkedStringsOrMarkupContent(new MarkupContent()
            {
                Kind = MarkupKind.Markdown,
                Value = string.Format("```gsc\n(field) /@ {0} @/ {1}\n```",
                   typeValue, Node.Identifier!)
            })
        };
    }
}

public class ScrNamespaceScopeSymbol : ISenseDefinition
{
    public Range Range { get; }

    public string SemanticTokenType { get; } = "namespace";

    public string[] SemanticTokenModifiers { get; private set; } = [];
    public bool IsFromPreprocessor { get; } = false;

    internal IdentifierExprNode Node { get; }
    internal string NamespaceName { get; }

    internal ScrNamespaceScopeSymbol(IdentifierExprNode node)
    {
        Node = node;
        Range = node.Range;
        NamespaceName = node.Identifier;
    }

    public Hover GetHover()
    {
        return new()
        {
            Range = Range,
            Contents = new MarkedStringsOrMarkupContent(new MarkupContent()
            {
                Kind = MarkupKind.Markdown,
                Value = string.Format("```gsc\n(namespace) {0}\n```",
                   NamespaceName)
            })
        };
    }
}

public class ScrFunctionReferenceSymbol : ISenseDefinition
{
    public Range Range { get; }

    public string SemanticTokenType { get; } = "function";

    public string[] SemanticTokenModifiers { get; private set; } = [];
    public bool IsFromPreprocessor { get; } = false;

    internal ScrFunction Source { get; }

    internal ScrFunctionReferenceSymbol(Token token, ScrFunction source)
    {
        Source = source;
        Range = token.Range;
    }

    public Hover GetHover()
    {
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
}

public class ScrReservedFunctionSymbol : ISenseDefinition
{
    public Range Range { get; }

    public string SemanticTokenType { get; } = "keyword";

    public string[] SemanticTokenModifiers { get; private set; } = [];
    public bool IsFromPreprocessor { get; } = false;

    internal ScrFunction? Source { get; }

    internal ScrReservedFunctionSymbol(Token token, ScrFunction? source)
    {
        Source = source;
        Range = token.Range;
    }

    public Hover GetHover()
    {
        return new()
        {
            Range = Range,
            Contents = new MarkedStringsOrMarkupContent(new MarkupContent()
            {
                Kind = MarkupKind.Markdown,
                // TODO: This is a hack
                Value = Source?.Documentation ?? "Reserved function"
            })
        };
    }
}