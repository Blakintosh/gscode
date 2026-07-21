using System.Collections.Immutable;
using GSCode.Core.Symbols;
using GSCode.Core.Text;
using GSCode.Parser;
using GSCode.Parser.Lexing;
using GSCode.Workspace.Api;
using GSCode.Workspace.Database;

namespace GSCode.Workspace.Completion;

/// <summary>One parameter shown in signature help.</summary>
public sealed record SignatureParameter(string Label, string Documentation);

/// <summary>A resolved signature: its rendered label, its parameters, and the active argument index.</summary>
public sealed record SignatureResult(
    string Label,
    ImmutableArray<SignatureParameter> Parameters,
    int ActiveParameter,
    string Documentation);

/// <summary>
/// Computes signature help by scanning back from the cursor to the enclosing unclosed '(',
/// identifying its callee (script function, builtin, or a call-shaped keyword), and counting
/// top-level commas to the cursor for the active parameter.
/// </summary>
public sealed class SignatureEngine
{
    private readonly ScriptDatabase _database;
    private readonly BuiltinApiSet _builtins;

    public SignatureEngine(ScriptDatabase database, BuiltinApiSet builtins)
    {
        _database = database;
        _builtins = builtins;
    }

    /// <summary>Resolves signature help at a position, or null when not inside a call.</summary>
    public SignatureResult? Resolve(ParseResult result, string contextId, Position position)
    {
        ImmutableArray<Token> tokens = result.Lexed.Tokens;
        int offset = result.Text.GetOffset(position);

        CallSite? site = FindEnclosingCall(tokens, offset);
        if ( site is null )
        {
            return null;
        }

        string calleeName = tokens[site.Value.CalleeIndex].GetText(result.Text).ToString();
        string? namespaceName = site.Value.NamespaceIndex >= 0
            ? tokens[site.Value.NamespaceIndex].GetText(result.Text).ToString().ToLowerInvariant()
            : null;

        SignatureResult? scriptSignature = TryScriptFunction(result, contextId, namespaceName, calleeName, site.Value.ActiveParameter);
        if ( scriptSignature is not null )
        {
            return scriptSignature;
        }

        // Namespace-less builtins (sys:: aliases them; a plain name reaches them too).
        if ( namespaceName is null || namespaceName == "sys" )
        {
            BuiltinFunction? builtin = _builtins.For(result.Language).Find(calleeName);
            if ( builtin is not null )
            {
                return BuildBuiltinSignature(builtin, site.Value.ActiveParameter);
            }
        }

        return null;
    }

    private SignatureResult? TryScriptFunction(ParseResult result, string contextId, string? namespaceName, string calleeName, int activeParameter)
    {
        LanguageStore store = _database.StoreFor(result.Language);
        string keyName = calleeName.ToLowerInvariant();

        ImmutableArray<ResolvedFunction> functions = namespaceName is not null
            ? DatabaseQueries.LookupFunctions(store, contextId, result.FilePath, namespaceName, keyName, askingNamespaces: DatabaseQueries.DeclaredNamespaces(result))
            : LookupUnqualified(result, store, contextId, keyName);

        if ( functions.Length == 0 )
        {
            return null;
        }

        FunctionSymbol function = functions[0].Function;
        ImmutableArray<SignatureParameter>.Builder parameters = ImmutableArray.CreateBuilder<SignatureParameter>();
        foreach ( ParameterSymbol parameter in function.Parameters )
        {
            string label = parameter.ByRef ? "&" + parameter.Name : parameter.Name;
            if ( parameter.DefaultValueText.Length > 0 )
            {
                label += " = " + parameter.DefaultValueText;
            }

            parameters.Add(new SignatureParameter(label, ParameterDoc(function, parameter.Name)));
        }

        string signatureLabel = MarkdownDocRenderer.RenderFunction(function);
        return new SignatureResult(
            BuildLabel(function.Name, parameters),
            parameters.ToImmutable(),
            ClampActive(activeParameter, parameters.Count),
            signatureLabel);
    }

    private ImmutableArray<ResolvedFunction> LookupUnqualified(ParseResult result, LanguageStore store, string contextId, string keyName)
    {
        // Try each namespace the file participates in.
        foreach ( NamespaceSpan span in result.Extraction.Namespaces )
        {
            ImmutableArray<ResolvedFunction> found = DatabaseQueries.LookupFunctions(store, contextId, result.FilePath, span.KeyName, keyName, askingNamespaces: DatabaseQueries.DeclaredNamespaces(result));
            if ( found.Length > 0 )
            {
                return found;
            }
        }

        return [];
    }

    private static SignatureResult BuildBuiltinSignature(BuiltinFunction builtin, int activeParameter)
    {
        BuiltinOverload overload = builtin.Overloads.FirstOrDefault() ?? new BuiltinOverload(null, [], "", false);
        ImmutableArray<SignatureParameter>.Builder parameters = ImmutableArray.CreateBuilder<SignatureParameter>();
        foreach ( BuiltinParameter parameter in overload.Parameters )
        {
            string label = parameter.Mandatory ? parameter.Name : parameter.Name + "?";
            parameters.Add(new SignatureParameter(label, parameter.Description));
        }

        return new SignatureResult(
            BuildLabel(builtin.Name, parameters),
            parameters.ToImmutable(),
            ClampActive(activeParameter, parameters.Count),
            builtin.Description);
    }

    private static string BuildLabel(string name, ImmutableArray<SignatureParameter>.Builder parameters)
    {
        return name + "(" + string.Join(", ", parameters.Select(static p => p.Label)) + ")";
    }

    private static string ParameterDoc(FunctionSymbol function, string parameterName)
    {
        foreach ( GSCode.Core.Docs.ScriptDocArgument argument in function.Doc.Arguments )
        {
            if ( string.Equals(argument.Name, parameterName, StringComparison.OrdinalIgnoreCase) )
            {
                return argument.Description;
            }
        }

        return "";
    }

    private static int ClampActive(int active, int count)
    {
        if ( count == 0 )
        {
            return 0;
        }

        return Math.Clamp(active, 0, count - 1);
    }

    private readonly record struct CallSite(int CalleeIndex, int NamespaceIndex, int ActiveParameter);

    /// <summary>
    /// Walks back from the cursor tracking bracket depth to find the '(' that encloses it,
    /// then the callee before that paren and the comma count (active parameter).
    /// </summary>
    private static CallSite? FindEnclosingCall(ImmutableArray<Token> tokens, int offset)
    {
        // Index of the first token at/after the cursor; scan leftwards from there.
        int start = tokens.Length - 1;
        for ( int index = 0; index < tokens.Length; index++ )
        {
            if ( tokens[index].Start >= offset )
            {
                start = index - 1;
                break;
            }
        }

        int depth = 0;
        int commas = 0;

        for ( int index = start; index >= 0; index-- )
        {
            TokenKind kind = tokens[index].Kind;

            if ( kind == TokenKind.CloseParen )
            {
                depth++;
            }
            else if ( kind == TokenKind.OpenParen )
            {
                if ( depth == 0 )
                {
                    // Found the enclosing open paren; the callee is just before it.
                    int calleeIndex = PreviousSignificant(tokens, index);
                    if ( calleeIndex < 0 || tokens[calleeIndex].Kind != TokenKind.Identifier )
                    {
                        return null;
                    }

                    int namespaceIndex = -1;
                    int scope = PreviousSignificant(tokens, calleeIndex);
                    if ( scope >= 0 && tokens[scope].Kind == TokenKind.ScopeResolution )
                    {
                        namespaceIndex = PreviousSignificant(tokens, scope);
                    }

                    return new CallSite(calleeIndex, namespaceIndex, commas);
                }

                depth--;
            }
            else if ( kind == TokenKind.Comma && depth == 0 )
            {
                commas++;
            }
            else if ( (kind == TokenKind.Semicolon || kind == TokenKind.OpenBrace || kind == TokenKind.CloseBrace) && depth == 0 )
            {
                // A statement boundary at our level means we are not inside a call.
                return null;
            }
        }

        return null;
    }

    private static int PreviousSignificant(ImmutableArray<Token> tokens, int fromIndex)
    {
        int index = fromIndex - 1;
        while ( index >= 0 && tokens[index].IsTrivia )
        {
            index--;
        }

        return index;
    }
}
