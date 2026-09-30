using GSCode.Parser;
using GSCode.Parser.Lexing;
using GSCode.Parser.Preprocessing;

namespace GSCode.Server.Formatting;

/// <summary>
/// <see cref="FormatOptions.FixCasing"/>: keywords are written in lowercase, and a function, namespace
/// or class name is written the way it is declared — <c>isDefined( x )</c> as <c>isdefined( x )</c>,
/// <c>FOo()</c> as <c>foo()</c> when the script declares <c>function foo()</c>, <c>getplayers()</c> as
/// <c>GetPlayers()</c>, <c>Util::</c> as <c>util::</c>, <c>new derived()</c> as <c>new Derived()</c>.
/// </summary>
/// <remarks>
/// GSC resolves all of these case-insensitively, so a fix changes how code reads, not what runs.
/// Macros are the exception: the preprocessor matches a macro name exactly, 1:1. So a macro use is
/// never recased, no fix may produce a macro's name, and nothing inside a <c>#define</c> is touched.
/// A function-like macro is only a use where a '(' follows it, which is how <c>DEFAULT( a, 1 )</c> —
/// stock's macro — and a <c>default:</c> label can sit in one file: the first stays the macro, the
/// second is the keyword.
///
/// Every fix differs from the source only in case; the token gate checks the output against them.
/// </remarks>
public static partial class GscFormatter
{
    private static string?[] CasingFixes(
        List<SignificantToken> significant,
        ParseResult result,
        TokenRoles roles,
        ICasingLookup? lookup)
    {
        string?[] fixes = new string?[significant.Count];
        Dictionary<string, bool> macros = Macros(result.Preprocessed);

        for ( int index = 0; index < significant.Count; index++ )
        {
            if ( roles.InDirective[index] )
            {
                continue;
            }

            Token token = significant[index].Token;
            string text = token.GetText(result.Text).ToString();
            if ( IsMacroUse(macros, text, significant, index) )
            {
                continue;
            }

            string? spelling = null;
            if ( TokenFacts.IsKeyword(token.Kind) )
            {
                spelling = text.ToLowerInvariant();
            }
            else if ( token.Kind == TokenKind.Identifier && lookup is not null )
            {
                spelling = IdentifierSpelling(significant, index, result, lookup, text);
            }

            bool onlyCaseDiffers = spelling is not null
                && !string.Equals(spelling, text, StringComparison.Ordinal)
                && string.Equals(spelling, text, StringComparison.OrdinalIgnoreCase);
            if ( onlyCaseDiffers && !macros.ContainsKey(spelling!) )
            {
                fixes[index] = spelling;
            }
        }

        return fixes;
    }

    /// <summary>
    /// Every macro this file can see, its own and its inserts', by exact name, with whether it is
    /// function-like.
    /// </summary>
    private static Dictionary<string, bool> Macros(PreprocessResult preprocessed)
    {
        Dictionary<string, bool> macros = new(StringComparer.Ordinal);
        foreach ( MacroDefinition definition in preprocessed.Macros.All )
        {
            macros[definition.Name] = definition.IsFunctionLike;
        }

        foreach ( MacroDefinition definition in preprocessed.AllMacroDefinitions )
        {
            macros.TryAdd(definition.Name, definition.IsFunctionLike);
        }

        return macros;
    }

    /// <summary>
    /// Whether this token is a use of a macro: spelled exactly like one, and — for a function-like
    /// macro — followed by '(', without which the preprocessor leaves it alone.
    /// </summary>
    private static bool IsMacroUse(Dictionary<string, bool> macros, string text, List<SignificantToken> significant, int index)
    {
        if ( !macros.TryGetValue(text, out bool functionLike) )
        {
            return false;
        }

        if ( !functionLike )
        {
            return true;
        }

        return index + 1 < significant.Count && significant[index + 1].Token.Kind == TokenKind.OpenParen;
    }

    /// <summary>
    /// The spelling for an identifier by what it names: the namespace or class before a <c>::</c>, a
    /// class after <c>new</c> or as a base class, or a function being called or referred to. Anything
    /// else — a variable, a field, a declaration's own name — is not this pass's to change.
    /// </summary>
    private static string? IdentifierSpelling(
        List<SignificantToken> significant, int index, ParseResult result, ICasingLookup lookup, string text)
    {
        TokenKind previous = KindAt(significant, index - 1);
        TokenKind beforePrevious = KindAt(significant, index - 2);
        TokenKind next = KindAt(significant, index + 1);

        if ( previous is TokenKind.Dot or TokenKind.Arrow )
        {
            return null;
        }

        if ( next == TokenKind.ScopeResolution )
        {
            return lookup.Qualifier(text);
        }

        if ( previous == TokenKind.New || IsBaseClassName(significant, index) )
        {
            return lookup.Class(text);
        }

        if ( IsDeclarationName(significant, index) )
        {
            return null;
        }

        string? qualifier = null;
        if ( previous == TokenKind.ScopeResolution && beforePrevious == TokenKind.Identifier )
        {
            qualifier = significant[index - 2].Token.GetText(result.Text).ToString();
        }

        // Where the callee sits decides what it can mean. A bare call resolves to a builtin first;
        // a threaded call or a reference cannot mean one.
        int callee = qualifier is null ? index : index - 2;
        TokenKind beforeCallee = KindAt(significant, callee - 1);
        bool reference = beforeCallee == TokenKind.Ampersand && IsUnaryHere(KindAt(significant, callee - 2), beforeCallee);
        bool threaded = beforeCallee is TokenKind.Thread or TokenKind.ChildThread;

        if ( previous != TokenKind.ScopeResolution && !reference && next != TokenKind.OpenParen )
        {
            return null;
        }

        return lookup.Function(qualifier, text, preferScript: reference || threaded);
    }

    private static TokenKind KindAt(List<SignificantToken> significant, int index)
    {
        if ( index < 0 )
        {
            return TokenKind.OpenBrace;
        }

        return index < significant.Count ? significant[index].Token.Kind : TokenKind.EndOfFile;
    }

    /// <summary>Whether the identifier is the name in <c>function [private] [autoexec] name(</c>.</summary>
    private static bool IsDeclarationName(List<SignificantToken> significant, int index)
    {
        int walk = index - 1;
        while ( walk >= 0 && significant[walk].Token.Kind is TokenKind.Private or TokenKind.Autoexec )
        {
            walk--;
        }

        return walk >= 0 && significant[walk].Token.Kind is TokenKind.Function or TokenKind.Class;
    }

    /// <summary>Whether the identifier is the base in <c>class Name : Base</c>.</summary>
    private static bool IsBaseClassName(List<SignificantToken> significant, int index)
    {
        return KindAt(significant, index - 1) == TokenKind.Colon
            && KindAt(significant, index - 2) == TokenKind.Identifier
            && KindAt(significant, index - 3) == TokenKind.Class;
    }
}
