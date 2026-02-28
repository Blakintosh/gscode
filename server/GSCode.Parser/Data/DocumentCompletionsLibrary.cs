using GSCode.Parser.Lexical;
using GSCode.Parser.SPA;
using OmniSharp.Extensions.JsonRpc;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using Serilog;
using System;
using System.Collections.Generic;

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

    // Use shared API instance to avoid redundant allocations
    private readonly ScriptAnalyserData? _scriptAnalyserData = ScriptAnalyserData.GetShared(languageId);

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

        // Generate completions from identifiers that occur inside of the file, as well.
        completions.AddRange(GetFileScopeCompletions(context));

        // return token.SenseDefinition?.GetCompletions();
        return new CompletionList(completions);
    }

    private List<CompletionItem> GetGlobalScopeCompletions(CompletionContext context)
    {
        List<ScrFunction> functions = _scriptAnalyserData?.GetApiFunctions(context.Filter) ?? [];

        List<CompletionItem> completions = new();

        Log.Information("Found {Count} functions in global scope", functions.Count);

        foreach (ScrFunction function in functions)
        {
            completions.Add(CreateCompletionItem(function));
        }

        return completions;
    }

    private List<CompletionItem> GetFileScopeCompletions(CompletionContext context)
    {
        List<CompletionItem> completions = new();
        HashSet<string> seenIdentifiers = new(StringComparer.OrdinalIgnoreCase);

        // This will be replaced later, but will suffice as a temporary completions solution.

        // Add GSC/CSC keywords
        string[] keywords = {
            "class", "return", "wait", "thread", "classes", "if", "else", "do", "while",
            "for", "foreach", "in", "new", "waittill", "waittillmatch", "waittillframeend",
            "switch", "case", "default", "break", "continue", "notify", "endon",
            "waitrealtime", "profilestart", "profilestop", "isdefined", "vectorscale",
            // Additional keywords
            "true", "false", "undefined", "self", "level", "game", "world", "vararg", "anim",
            "var", "const", "function", "private", "autoexec", "constructor", "destructor"
        };

        foreach (string keyword in keywords)
        {
            if (!seenIdentifiers.Contains(keyword))
            {
                completions.Add(new CompletionItem()
                {
                    Kind = CompletionItemKind.Keyword,
                    Label = keyword,
                    InsertText = keyword
                });
                seenIdentifiers.Add(keyword);
            }
        }

        // Add GSC directives (only if filter starts with #)
        if ((context.Filter ?? "").StartsWith("#"))
        {
            string[] directives = {
                "#using", "#insert", "#namespace", "#using_animtree", "#precache",
                "#define", "#if", "#elif", "#else", "#endif"
            };

            foreach (string directive in directives)
            {
                if (!seenIdentifiers.Contains(directive))
                {
                    completions.Add(new CompletionItem()
                    {
                        Kind = CompletionItemKind.Keyword,
                        Label = directive,
                        InsertText = directive
                    });
                    seenIdentifiers.Add(directive);
                }
            }
        }

        // Generate completions from identifiers that occur inside of the file
        foreach (Token token in Tokens.GetAll())
        {
            if (token.Type == TokenType.Identifier && !seenIdentifiers.Contains(token.Lexeme))
            {
                // Skip identifiers that are API function names to avoid duplicates
                if (_scriptAnalyserData?.GetApiFunction(token.Lexeme) is not null)
                {
                    continue;
                }

                completions.Add(new CompletionItem()
                {
                    Kind = CompletionItemKind.Variable,
                    Label = token.Lexeme,
                    InsertText = token.Lexeme
                });
                seenIdentifiers.Add(token.Lexeme);
            }
        }

        return completions;
    }

    private CompletionItem CreateCompletionItem(ScrFunction function)
    {
        // TODO: has been hacked to show first only, but we need to handle all overloads eventually.
        // Generate snippet-formatted parameters with tabstops
        string insertText = function.Name;
        if (function.Overloads.First().Parameters.Count > 0)
        {
            insertText += "(";

            // Create snippet-formatted parameter list with tabstops
            List<string> paramSnippets = new List<string>();
            int tabIndex = 1;

            foreach (var param in function.Overloads.First().Parameters)
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

        return new CompletionItem()
        {
            Label = function.Name,
            Detail = function.Description,
            Documentation = new StringOrMarkupContent(new MarkupContent()
            {
                Kind = MarkupKind.Markdown,
                Value = function.Documentation
            }),
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