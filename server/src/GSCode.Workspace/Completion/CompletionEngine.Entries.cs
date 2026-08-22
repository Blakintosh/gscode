using System.Collections.Immutable;
using GSCode.Core;
using GSCode.Core.Symbols;
using GSCode.Core.Text;
using GSCode.Parser;
using GSCode.Parser.Lexing;
using GSCode.Parser.Syntax;
using GSCode.Workspace.Api;
using GSCode.Workspace.Database;

namespace GSCode.Workspace.Completion;
/// <summary>
/// Turning one symbol into one <see cref="CompletionEntry"/>: labels, detail text, and the snippet
/// actually inserted.
///
/// Separated from the producers because the questions are independent — a producer decides WHICH
/// functions belong in the list, and these decide what a function looks like once it is in one. The
/// snippet rules in particular (call punctuation, parameter placeholders, the label truncation) are
/// shared by several producers and belong to none of them.
/// </summary>
public sealed partial class CompletionEngine
{
    /// <summary>A method entry, labelled with the class that declares it rather than a namespace.</summary>
    private static CompletionEntry MethodEntry(ClassMethod method, string callSuffix, bool parameterHints)
    {
        return FunctionEntry(method.Method, callSuffix, parameterHints) with { Detail = method.OwnerClass.Name };
    }

    /// <summary>
    /// What accepting a directive actually inserts: its full form, with the cursor placed where
    /// the argument goes. Inserting the bare word left a directive that does not parse until the
    /// parentheses and semicolon are typed by hand — and a half-written directive reddens the
    /// lines under it, so the editor looks broken while you finish the line.
    ///
    /// The '#' is already in the buffer (that is what put us in this context), so none of these
    /// repeat it. Conditionals and #endif take no punctuation and are inserted plain.
    /// </summary>
    private static string DirectiveSnippet(string keyword, string withoutHash)
    {
        switch ( keyword )
        {
            case "#precache":
                // Both arguments are quoted, and the first is a closed vocabulary that
                // completion offers as soon as the cursor lands inside the quotes.
                return "precache( \"$1\", \"$2\" );$0";
            case "#using_animtree":
                return "using_animtree( \"$1\" );$0";
            case "#using":
            case "#insert":
                // Pre-fill the root: every one of the 9,875 path directives in the stock scripts
                // starts at `scripts\`, so typing it is pure ceremony. The cursor lands after the
                // separator, where completion reopens on that folder's contents.
                //
                return withoutHash + " " + SnippetLiteral(@"scripts\") + "$1;$0";
            case "#namespace":
                return "namespace $1;$0";
            case "#define":
                // No semicolon: a #define runs to the end of the line.
                return "define $1 $0";
            default:
                return withoutHash;
        }
    }

    /// <summary>
    /// Escapes text that must appear VERBATIM inside a snippet.
    ///
    /// Snippet syntax gives '\', '$' and '}' meaning, and '\' escapes whatever follows it — so an
    /// unescaped path separator swallowed the tab stop after it and put `#using scripts$1;` in the
    /// buffer as literal text. GSC paths are full of separators, so this has to be a function
    /// rather than something remembered at each call site.
    /// </summary>
    private static string SnippetLiteral(string text)
    {
        return text
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("$", "\\$", StringComparison.Ordinal)
            .Replace("}", "\\}", StringComparison.Ordinal);
    }

    /// <summary>
    /// Whether the cursor lands somewhere this engine can actually suggest for, so accepting the
    /// directive should reopen the suggestion list.
    ///
    /// Only where a vocabulary exists. Reopening on <c>#define</c> or <c>#namespace</c>, whose
    /// argument is a name the user is inventing, would pop a list over what they are typing.
    /// </summary>
    private static bool DirectiveArgumentHasVocabulary(string keyword)
    {
        switch ( keyword )
        {
            // The asset type: a closed list from PrecacheAssetTypes.
            case "#precache":
            // A script path: offered from the indexed files.
            case "#using":
            case "#insert":
                return true;
            default:
                return false;
        }
    }

    /// <summary>
    /// A whole function declaration, not just the word that starts one.
    ///
    /// Writing one by hand is four pieces of punctuation that are the same every time — the
    /// parentheses, the braces, and getting the brace onto its own line — so the snippet does them
    /// and leaves the caret on the NAME, which is the only part that varies.
    ///
    /// Laid out the way the formatter would: Allman braces and a tab, measured at 51,048 Allman
    /// against 37 same-line and 247,613 tab-led lines against 886 space-led across the stock
    /// scripts. A snippet that had to be reformatted the moment it landed would be a strange thing
    /// to ship.
    ///
    /// The dialect decides the opening: BO3 declares with the `function` keyword, while the merge
    /// dialects open with the bare name. The label stays "function" either way — it is what the
    /// user is looking for, not what gets inserted.
    /// </summary>
    private static CompletionEntry FunctionDeclarationSnippet(GameProfile game)
    {
        string opening = game.HasFunctionKeyword ? "function " : "";

        return new CompletionEntry(
            "function",
            CompletionKind.Snippet,
            "declaration",
            opening + "${1:name}()\n{\n\t$0\n}",
            "Declares a function, with the caret on the name.");
    }

    /// <summary>How much parameter text a label may carry before it is cut short.</summary>
    /// <remarks>
    /// A row that runs past the popup's width is truncated by the editor anyway, at whatever
    /// character it happens to reach — and a name cut mid-word reads as though it were the name.
    /// Cutting it here, at a separator, keeps the row honest about being incomplete.
    /// </remarks>
    private const int MaximumParameterHintLength = 42;

    /// <summary>
    /// The parameter list shown INLINE in a completion label — <c>( entity, team )</c>.
    ///
    /// Names only. The types, defaults and descriptions belong to signature help and the doc
    /// panel, which have room for them; what the list is for is telling apart the entries whose
    /// names alone do not, which in this codebase is most of them — <c>on_agent_generic_damaged</c>
    /// and <c>on_agent_player_damaged</c> differ by their arguments and nothing else.
    ///
    /// Free to produce: the parameters are already in hand when the list is built, unlike
    /// documentation, which is why this does not need the resolve round trip that a doc block does.
    /// </summary>
    private static string ParameterHint(ImmutableArray<ParameterSymbol> parameters, bool hasVarargs)
    {
        if ( parameters.Length == 0 )
        {
            return hasVarargs ? "( ... )" : "()";
        }

        System.Text.StringBuilder rendered = new();
        foreach ( ParameterSymbol parameter in parameters )
        {
            if ( rendered.Length > 0 )
            {
                rendered.Append(", ");
            }

            if ( rendered.Length > MaximumParameterHintLength )
            {
                rendered.Append('…');
                return "( " + rendered + " )";
            }

            rendered.Append(parameter.Name);
        }

        if ( hasVarargs )
        {
            rendered.Append(", ...");
        }

        return "( " + rendered + " )";
    }

    private static CompletionEntry FunctionEntry(FunctionSymbol function, string callSuffix, bool parameterHints)
    {
        string detail = function.Namespace.Length > 0 ? function.Namespace + "::" + function.Name : function.Name;

        // No Documentation: rendering a doc block for every function in the workspace on every
        // keystroke is exactly what completionItem/resolve exists to avoid. The namespace rides
        // along so resolve can find this function again.
        //
        // The parameters go in LabelDetail rather than the label, so the label stays exactly the
        // name — which is what the editor filters, sorts and resolves on.
        return new CompletionEntry(
            function.Name,
            CompletionKind.Function,
            detail,
            function.Name + callSuffix,
            Namespace: function.Namespace,
            LabelDetail: parameterHints ? ParameterHint(function.Parameters, function.HasVarargs) : "");
    }

    /// <summary>
    /// An engine builtin. Its parameters come from the FIRST overload — the data models several,
    /// but a completion row has space for one shape, and the doc panel behind it lists them all.
    /// A trailing <c>?</c> marks an optional parameter, matching how signature help renders them.
    /// </summary>
    private static CompletionEntry BuiltinEntry(BuiltinFunction builtin, string callSuffix, bool parameterHints)
    {
        if ( !parameterHints )
        {
            return new CompletionEntry(
                builtin.Name, CompletionKind.Function, "builtin", builtin.Name + callSuffix, IsBuiltin: true);
        }

        BuiltinOverload? overload = builtin.Overloads.FirstOrDefault();
        ImmutableArray<ParameterSymbol> parameters = overload is null
            ? []
            : [.. overload.Parameters.Select(static p =>
                new ParameterSymbol(p.Mandatory ? p.Name : p.Name + "?", false, ""))];

        return new CompletionEntry(
            builtin.Name,
            CompletionKind.Function,
            "builtin",
            builtin.Name + callSuffix,
            LabelDetail: ParameterHint(parameters, hasVarargs: false),
            IsBuiltin: true);
    }

    /// <summary>
    /// A function reached through a <c>#using</c> import, labelled and inserted FULLY QUALIFIED.
    ///
    /// The label carries the qualifier because that is what makes the namespace findable: the
    /// editor filters on the label, so typing "uti" surfaces every <c>util::</c> function at once
    /// rather than only the ones whose own name happens to begin that way. Typing the function's
    /// name still matches it — the qualifier is a prefix, not a replacement — so both routes work.
    ///
    /// Inserting the qualifier is not a convenience but a correctness matter: an unqualified call
    /// into another namespace does not resolve.
    /// </summary>
    private static CompletionEntry ImportedFunctionEntry(
        FunctionSymbol function, string ns, string callSuffix, bool parameterHints)
    {
        string qualified = ns + "::" + function.Name;

        // The label stays the QUALIFIED name and nothing else: filtering on the qualifier is the
        // whole reason it carries one, and the editor filters on the label. ResolveName keeps the
        // function's OWN name, which is what documentation is looked up by — that one is needed
        // whatever the parameter hints do, since `util::get_players` matches no symbol.
        return new CompletionEntry(
            qualified,
            CompletionKind.Function,
            "function",
            qualified + callSuffix,
            Namespace: ns,
            ResolveName: function.Name,
            LabelDetail: parameterHints ? ParameterHint(function.Parameters, function.HasVarargs) : "");
    }

    /// <summary>
    /// A macro, which is a call or a constant depending on how it was defined.
    ///
    /// A function-like macro takes the same call punctuation a function does, because at the use
    /// site it IS a call — `#define ABS(x)` written without its parentheses expands to nothing
    /// usable. An object-like one inserts its bare name.
    ///
    /// The detail names the header a macro came from, since that is the question asked about one
    /// that is not in the file being read: `#insert`ed constants outnumber locally-defined ones in
    /// the stock scripts, and "macro" alone said nothing about where to go look.
    ///
    /// The documentation is the trailing comment on the `#define` line, carried at parse time —
    /// free here, so unlike a function's doc block it needs no resolve round trip.
    /// </summary>
    private static CompletionEntry MacroEntry(
        GSCode.Parser.Preprocessing.MacroDefinition macro, string callSuffix, bool parameterHints)
    {
        string detail = macro.SourceFile is null
            ? "macro"
            : "macro (" + System.IO.Path.GetFileName(macro.SourceFile) + ")";

        if ( macro.Parameters is not ImmutableArray<string> parameters )
        {
            return new CompletionEntry(macro.Name, CompletionKind.Macro, detail, "", macro.Documentation ?? "");
        }

        return new CompletionEntry(
            macro.Name,
            CompletionKind.Macro,
            detail,
            macro.Name + callSuffix,
            macro.Documentation ?? "",
            LabelDetail: parameterHints
                ? ParameterHint([.. parameters.Select(static p => new ParameterSymbol(p, false, ""))], hasVarargs: false)
                : "");
    }

    /// <summary>
    /// An imported namespace, offered by NAME so it is findable without already knowing one of its
    /// functions. Inserts the qualifier and reopens the list — which the explicit `ns::` handler
    /// above then fills with that namespace's members — the same walk-it-down shape path segments
    /// use for a folder.
    /// </summary>
    private static CompletionEntry NamespaceEntry(string ns)
    {
        return new CompletionEntry(ns, CompletionKind.Namespace, "namespace", ns + "::", Namespace: ns, RetriggerCompletion: true);
    }

    /// <summary>
    /// What a completed call brings with it after the name, honouring the callPunctuation setting.
    ///
    /// The semicolon is added only in STATEMENT position. `x = foobar()` and `foobar()[0]` are
    /// expressions, and closing them would put a semicolon in the middle of one.
    /// </summary>
    private static string CallSnippet(
        ImmutableArray<Token> tokens, int currentIndex, int offset, CallPunctuation punctuation)
    {
        switch ( punctuation )
        {
            case CallPunctuation.Off:
                return "";
            case CallPunctuation.ParensAndSemicolon when IsStatementPosition(tokens, currentIndex, offset):
                return "($0);";
            default:
                return "($0)";
        }
    }

    /// <summary>
    /// What a completed KEYWORD inserts, derived from the same call suffix functions use so the
    /// callPunctuation setting governs both.
    ///
    /// <paramref name="callSuffix"/> already encodes the setting and the position: "" when
    /// punctuation is off, "($0)" for a call, "($0);" for a call that is a whole statement. Each
    /// keyword shape reads what it needs from that rather than re-deriving it.
    /// </summary>
    private static string KeywordInsertText(string keyword, string callSuffix)
    {
        bool punctuate = callSuffix.Length > 0;
        bool statement = callSuffix.EndsWith(';');

        switch ( GscKeywords.ShapeOf(keyword) )
        {
            case KeywordShape.StatementCall:
                return punctuate ? keyword + callSuffix : "";

            // `waittillframeend;`, `break;`, `continue;` — nothing but the terminator.
            case KeywordShape.BareStatement:
                return statement ? keyword + ";" : "";

            // `return$0;` — the caret sits before the terminator, so typing nothing leaves
            // `return;` while typing a value leaves `return 5;`. Neither needs a correction
            // afterwards, which is what a bare `return;` would cost in the value case.
            case KeywordShape.ValueStatement:
                return statement ? keyword + "$0;" : "";

            default:
                // Empty means "insert the label", which is what a plain word wants.
                return "";
        }
    }
}
