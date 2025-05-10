using GSCode.Parser.Lexical;
using GSCode.Parser.SPA;
using GSCode.Parser.SPA.Sense;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using Serilog;

namespace GSCode.Parser.Data;

/// <summary>
/// A "dumb" implementation of a completions library, with naive heuristics for completion.
/// </summary>
public sealed class DocumentCompletionsLibrary(DocumentTokensLibrary tokens, string languageId)
{
    /// <summary>
    /// Library of tokens to quickly lookup a token at a given position.
    /// </summary>
    public DocumentTokensLibrary Tokens { get; } = tokens;

    private readonly ScriptAnalyserData _scriptAnalyserData = new(languageId);

    public CompletionList GetCompletionsFromPosition(Position position)
    {
        Token? token = Tokens.Get(position);

        if (token is null)
        {
            return [];
        }

        // For the moment, we'll just support Identifier completions.
        // CompletionContext context = AnalyseCompletionContext(token, position);
        CompletionContext context = new()
        {
            Type = CompletionContextType.GlobalScope,
            Filter = token.Lexeme
        };
        List<CompletionItem> completions = new();

        switch (context.Type)
        {
            case CompletionContextType.GlobalScope:
                completions = GetGlobalScopeCompletions(context);
                break;
        }

        // Get the completions from the definition.


        // return token.SenseDefinition?.GetCompletions();
        return new CompletionList(completions);
    }

    private List<CompletionItem> GetGlobalScopeCompletions(CompletionContext context)
    {
        List<ScrFunctionDefinition> functions = _scriptAnalyserData.GetApiFunctions(context.Filter);

        List<CompletionItem> completions = new();

        Log.Information("Found {Count} functions in global scope", functions.Count);

        foreach (ScrFunctionDefinition function in functions)
        {
            completions.Add(CreateCompletionItem(function));
        }

        return completions;
    }

    private CompletionItem CreateCompletionItem(ScrFunctionDefinition function)
    {
        // Generate snippet-formatted parameters with tabstops
        string insertText = function.Name;
        if (function.Parameters != null && function.Parameters.Count > 0)
        {
            insertText += "(";

            // Create snippet-formatted parameter list with tabstops
            List<string> paramSnippets = new List<string>();
            int tabIndex = 1;

            foreach (var param in function.Parameters)
            {
                // Add mandatory parameters with tabstops
                if (param.Mandatory.GetValueOrDefault(false))
                {
                    paramSnippets.Add($"${{{tabIndex}:{param.Name ?? $"param{tabIndex}"}}}");
                    tabIndex++;
                }
                // Optional parameters are added with brackets
                else
                {
                    paramSnippets.Add($"${{{tabIndex}:[{param.Name ?? $"optionalParam{tabIndex}"}]}}");
                    tabIndex++;
                }
            }

            insertText += string.Join(", ", paramSnippets);
            insertText += ")";

            // Add a final tabstop after the closing parenthesis
            insertText += "$0";
        }
        else
        {
            insertText += "()$0";
        }

        // Prepare parameter documentation
        string parameterDocs = "";
        if (function.Parameters != null && function.Parameters.Count > 0)
        {
            parameterDocs = "\n\n**Parameters:**\n";
            foreach (var param in function.Parameters)
            {
                string required = param.Mandatory.GetValueOrDefault(false) ? "required" : "optional";
                parameterDocs += $"- `{param.Name ?? "param"}` ({required}): {param.Description ?? "No description"}\n";
            }
        }

        // Add called-on context if available
        string calledOn = "";
        if (!string.IsNullOrEmpty(function.CalledOn))
        {
            calledOn = $"\n\n**Called on:** `{function.CalledOn}`";
        }

        // Create markdown documentation
        string docValue = $"```gsc\n{function.Name}()\n```\n\n{function.Description ?? "No description"}{parameterDocs}{calledOn}";

        return new CompletionItem()
        {
            Label = function.Name,
            Detail = function.Description,
            Documentation = new MarkupContent
            {
                Kind = MarkupKind.Markdown,
                Value = docValue
            },
            InsertText = insertText,
            InsertTextFormat = InsertTextFormat.Snippet,
            Kind = CompletionItemKind.Function,
            // Add sorting information to keep API functions organized
            SortText = function.Name.ToLowerInvariant(),
            // Add commit characters to automatically complete when typing these
            CommitCharacters = new Container<string>(new[] { "(", ")", ";" })
        };
    }

    private CompletionContext AnalyseCompletionContext(Token token, Position position)
    {
        var context = new CompletionContext { Position = position };

        TokenType currentType = token.Type;
        TokenType? previousType = token.Previous?.Type;
        TokenType? previousPreviousType = token.Previous?.Previous?.Type;

        // // Check for member access (obj.__|)
        // if (previousType == TokenType.Dot)
        // {
        //     context.Type = CompletionContext.CompletionContextType.MemberAccess;

        //     // Determine object type (what's before the dot)
        //     var objectToken = token.Previous.Previous;
        //     if (objectToken?.Type == TokenType.Identifier)
        //     {
        //         context.ObjectType = DetermineTypeFromToken(objectToken);
        //     }
        // }
        // // Check for namespaced function (namespace::__|)
        // else if (previousType == TokenType.ScopeResolution &&
        //         previousPreviousType == TokenType.Identifier)
        // {
        //     context.Type = CompletionContext.CompletionContextType.FunctionCall;
        //     context.Namespace = token.Previous.Previous.Lexeme;
        // }
        // Check for global scope
        if (currentType == TokenType.Identifier ||
                previousType == TokenType.Whitespace)
        {
            context.Type = CompletionContextType.GlobalScope;
        }

        // If current token is an identifier, use it as filter
        if (currentType == TokenType.Identifier)
        {
            context.Filter = token.Lexeme;
        }

        return context;
    }
}


public record struct CompletionContext
{
    public CompletionContextType Type { get; set; }
    public string? Namespace { get; set; }
    public string? Filter { get; set; }
    public string? ObjectType { get; set; }
    public Position Position { get; set; }
}

public enum CompletionContextType
{
    None,
    FunctionCall,
    MemberAccess,
    GlobalScope,
    TypeName
}