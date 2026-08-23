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

        ArrowReceiver arrow = ClassifyArrow(tokens, result.Text, site.Value.CalleeIndex);

        // A macro is asked BEFORE a function, because the preprocessor gets there first: where a
        // #define and a function share a name, the invocation being typed is replaced before the
        // parser ever sees it, so the function's parameters would describe code that never runs.
        // They can only collide on exact case — a macro name is the language's one case-SENSITIVE
        // kind. Neither an arrow call nor a qualified name can reach one: `[[o]]->NAME(` dispatches
        // on an object and `util::NAME(` names a namespace member, and the preprocessor expands
        // neither.
        //
        // Deliberately NOT gated on the dialect's HasMacros, unlike macro COMPLETION. Completion
        // decides what to propose, and proposing an expansion a pre-BO3 engine will not perform is
        // a wrong answer. This describes a name the user has already written, and the preprocessor
        // expands a #define on every dialect by design — see Preprocessor.ReportIfNoPreprocessor,
        // which reports the directive and then processes it anyway. Withholding help here would
        // leave the expansion happening with nothing on screen to describe it.
        if ( arrow == ArrowReceiver.None && namespaceName is null )
        {
            SignatureResult? macroSignature = TryMacro(result, calleeName, site.Value.ActiveParameter);
            if ( macroSignature is not null )
            {
                return macroSignature;
            }
        }

        SignatureResult? scriptSignature = TryScriptFunction(
            result, contextId, namespaceName, calleeName, site.Value.ActiveParameter, position, arrow);
        if ( scriptSignature is not null )
        {
            return scriptSignature;
        }

        // An arrow call cannot reach a builtin — the syntax dispatches on an object.
        if ( arrow != ArrowReceiver.None )
        {
            return null;
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

    /// <summary>
    /// Signature help for a function-like macro, read from the PARSE IN HAND rather than the store.
    /// The macro table is rebuilt on every parse from this file plus the headers it #inserts, so it
    /// is current for a header inserted a keystroke ago — which the indexed record is not, and help
    /// fires while the invocation is still being typed.
    /// </summary>
    private static SignatureResult? TryMacro(ParseResult result, string calleeName, int activeParameter)
    {
        // Ordinal, which is the lookup MacroTable is keyed by: `IS_TRUE(` and `is_true(` are two
        // different questions, unlike every other name in the language.
        if ( !result.Preprocessed.Macros.TryGet(calleeName, out GSCode.Parser.Preprocessing.MacroDefinition macro) )
        {
            return null;
        }

        // An OBJECT-like macro is not a call. `MAX_PLAYERS( x )` expands to its body followed by a
        // parenthesised expression, so it has no parameters to describe, and answering null hands
        // the position back to the by-name lookups rather than showing an empty signature.
        if ( macro.Parameters is not ImmutableArray<string> macroParameters )
        {
            return null;
        }

        ImmutableArray<SignatureParameter>.Builder parameters = ImmutableArray.CreateBuilder<SignatureParameter>();
        foreach ( string parameterName in macroParameters )
        {
            // Nothing to document per parameter: a #define carries at most one trailing comment,
            // and it describes the macro rather than any one of its arguments.
            parameters.Add(new SignatureParameter(parameterName, ""));
        }

        // The EXPANSION alone below the label, without hover's `#define` line: the label above is
        // the define form already, and the client draws it with the active argument highlighted.
        //
        // The body keeps its own parameter names, where hover substitutes the call site's arguments.
        // Here the parameter names are the subject — they are what the label highlights as the caret
        // moves between arguments — so showing where the highlighted one lands in the expansion is
        // what this panel is for.
        return new SignatureResult(
            BuildLabel(macro.Name, parameters),
            parameters.ToImmutable(),
            ClampActive(activeParameter, parameters.Count),
            MarkdownDocRenderer.RenderMacroExpansion(
                MacroExpansionPreview.Render(macro.Body), macro.Documentation ?? ""));
    }

    private SignatureResult? TryScriptFunction(
        ParseResult result, string contextId, string? namespaceName, string calleeName, int activeParameter,
        Position position, ArrowReceiver arrow)
    {
        LanguageStore store = _database.StoreFor(result.Language);
        string keyName = calleeName.ToLowerInvariant();

        // A method first, where the call could be one. Signature help is token-driven rather than
        // reference-driven, so the enclosing class comes from the caret's POSITION — which is also
        // what makes it work on half-typed code, where the call has no reference entry yet.
        //
        // An ARROW call is the exception: `[[o_obj]]->play(` is not a call on the class the caret
        // happens to sit in, so resolving it through the enclosing class would answer with whichever
        // class the cursor is inside and show that one's parameter names. Only `[[self]]->` names
        // the enclosing class; every other receiver is untyped and takes the by-name candidates.
        string? enclosingClass = EnclosingClassAt(result, position);

        if ( arrow != ArrowReceiver.None )
        {
            ImmutableArray<ClassMethod> arrowMethods = MethodsForArrow(
                store, contextId, arrow == ArrowReceiver.Self ? enclosingClass : null, keyName,
                result.Extraction.Classes);

            if ( arrowMethods.Length > 0 )
            {
                return BuildSignature(arrowMethods[0].Method, arrowMethods[0].OwnerClass, activeParameter);
            }
        }
        else
        {
            ImmutableArray<ResolvedFunction> methods = MethodsFor(store, contextId, enclosingClass, namespaceName, keyName);
            if ( methods.Length > 0 )
            {
                return BuildSignature(methods[0].Function, methods[0].OwnerClass, activeParameter);
            }
        }

        // An arrow call reaches a method or a field holding a function pointer, never a namespace
        // function by name, so the by-namespace lookups below would only guess.
        if ( arrow != ArrowReceiver.None )
        {
            return null;
        }

        ImmutableArray<ResolvedFunction> functions = namespaceName is not null
            ? DatabaseQueries.LookupFunctions(store, contextId, result.FilePath, namespaceName, keyName, askingNamespaces: DatabaseQueries.DeclaredNamespaces(result))
            : LookupUnqualified(result, store, contextId, keyName);

        if ( functions.Length == 0 )
        {
            return null;
        }

        return BuildSignature(functions[0].Function, functions[0].OwnerClass, activeParameter);
    }

    /// <summary>Whether the call being helped is an arrow call, and whether its receiver is <c>self</c>.</summary>
    private enum ArrowReceiver
    {
        /// <summary>Not an arrow call.</summary>
        None,

        /// <summary><c>[[self]]-&gt;m(</c> — the enclosing class.</summary>
        Self,

        /// <summary><c>[[anything else]]-&gt;m(</c> — a receiver whose class is not knowable here.</summary>
        Unknown,
    }

    /// <summary>
    /// Classifies the token immediately before the callee. <c>]]</c> lexes as TWO CloseBracket
    /// tokens, so the walk back to the receiver skips however many are there.
    /// </summary>
    private static ArrowReceiver ClassifyArrow(ImmutableArray<Token> tokens, SourceText text, int calleeIndex)
    {
        int arrowIndex = PreviousSignificant(tokens, calleeIndex);
        if ( arrowIndex < 0 || tokens[arrowIndex].Kind != TokenKind.Arrow )
        {
            return ArrowReceiver.None;
        }

        int receiver = PreviousSignificant(tokens, arrowIndex);
        while ( receiver >= 0 && tokens[receiver].Kind == TokenKind.CloseBracket )
        {
            receiver = PreviousSignificant(tokens, receiver);
        }

        bool isSelf = receiver >= 0
            && tokens[receiver].Kind == TokenKind.Identifier
            && TokenFacts.IsSelfName(tokens[receiver].GetText(text));

        return isSelf ? ArrowReceiver.Self : ArrowReceiver.Unknown;
    }

    /// <summary>
    /// The declarations an arrow call could reach: the enclosing class's chain for
    /// <c>[[self]]-&gt;</c>, otherwise every class declaring the name.
    /// </summary>
    /// <param name="localClasses">
    /// The classes of the parse in hand, which win over the store's copy. Signature help fires while
    /// a class is being written, and the store holds only the last INDEXED version — so without
    /// these, asking for help on a method of the class you are editing walks a chain starting at a
    /// class the store has never seen and answers nothing.
    /// </param>
    private static ImmutableArray<ClassMethod> MethodsForArrow(
        LanguageStore store,
        string contextId,
        string? receiverClass,
        string keyName,
        ImmutableArray<ClassSymbol> localClasses)
    {
        if ( receiverClass is not null )
        {
            return [.. MethodResolution.MethodsOf(store, contextId, receiverClass, localClasses)
                .Where(method => string.Equals(method.Method.KeyName, keyName, StringComparison.Ordinal))];
        }

        // The receiver's class is unknown, so every class declaring the name is a candidate — the
        // store's and this file's alike.
        ImmutableArray<ClassMethod>.Builder candidates = ImmutableArray.CreateBuilder<ClassMethod>();
        HashSet<string> seen = new(StringComparer.Ordinal);

        foreach ( ClassSymbol local in localClasses )
        {
            foreach ( FunctionSymbol method in local.Methods )
            {
                if ( string.Equals(method.KeyName, keyName, StringComparison.Ordinal) && seen.Add(local.KeyName) )
                {
                    candidates.Add(new ClassMethod(method, local, null));
                }
            }
        }

        foreach ( string className in store.Classes.ClassesDeclaringMethod(keyName) )
        {
            if ( !seen.Add(className) )
            {
                continue;
            }

            foreach ( ClassMethod method in MethodResolution.MethodsOf(store, contextId, className, localClasses) )
            {
                if ( string.Equals(method.Method.KeyName, keyName, StringComparison.Ordinal) )
                {
                    candidates.Add(method);
                    break;
                }
            }
        }

        return candidates.ToImmutable();
    }

    /// <summary>
    /// The declarations a method-shaped call at this caret could reach: through the enclosing class's
    /// chain when unqualified, or through the qualifier when written <c>Class::name(</c>.
    /// </summary>
    private static ImmutableArray<ResolvedFunction> MethodsFor(
        LanguageStore store, string contextId, string? enclosingClass, string? namespaceName, string keyName)
    {
        if ( namespaceName is null && enclosingClass is null )
        {
            return [];
        }

        SymbolKey written = namespaceName is null
            ? new SymbolKey(null, keyName, SymbolKind.Function, enclosingClass)
            : new SymbolKey(namespaceName, keyName, SymbolKind.Function);

        SymbolKey canonical = MethodResolution.Canonicalize(store, contextId, written, ReferenceKind.Call);
        if ( canonical.OwnerClass is null )
        {
            return [];
        }

        return MethodResolution.LookupMethods(store, contextId, canonical.OwnerClass, canonical.Name);
    }

    /// <summary>
    /// The class whose body contains this offset, by range containment over the file's own classes.
    /// There are at most a handful per file, so this stays cheaper than any index would be.
    /// </summary>
    private static string? EnclosingClassAt(ParseResult result, Position position)
    {
        foreach ( ClassSymbol classSymbol in result.Extraction.Classes )
        {
            if ( classSymbol.FullRange.Contains(position) )
            {
                return classSymbol.KeyName;
            }
        }

        return null;
    }

    private SignatureResult BuildSignature(FunctionSymbol function, ClassSymbol? ownerClass, int activeParameter)
    {
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

        string signatureLabel = MarkdownDocRenderer.RenderFunction(function, ownerClass);
        return new SignatureResult(
            BuildLabel(function.Name, parameters),
            parameters.ToImmutable(),
            ClampActive(activeParameter, parameters.Count),
            signatureLabel);
    }

    private ImmutableArray<ResolvedFunction> LookupUnqualified(ParseResult result, LanguageStore store, string contextId, string keyName)
    {
        // Try each namespace the file participates in. Hoisted out of the loop: it was rebuilt on
        // every iteration, and the spans it was read from included a phantom whose lookup scanned
        // the whole store to return nothing.
        ImmutableArray<string> askingNamespaces = DatabaseQueries.DeclaredNamespaces(result);

        foreach ( string declared in askingNamespaces )
        {
            ImmutableArray<ResolvedFunction> found = DatabaseQueries.LookupFunctions(store, contextId, result.FilePath, declared, keyName, askingNamespaces: askingNamespaces);
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
