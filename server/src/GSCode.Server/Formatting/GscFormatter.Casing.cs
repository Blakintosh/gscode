using GSCode.Parser;
using GSCode.Parser.Lexing;
using GSCode.Parser.Preprocessing;

namespace GSCode.Server.Formatting;

/// <summary>
/// <see cref="FormatOptions.FixCasing"/>: keywords are written in lowercase, and a call is written
/// the way its function is declared — <c>isDefined( x )</c> as <c>isdefined( x )</c>, <c>FOo()</c> as
/// <c>foo()</c> when the script declares <c>function foo()</c>, <c>getplayers()</c> as
/// <c>GetPlayers()</c>.
/// </summary>
/// <remarks>
/// GSC resolves keywords and function names case-insensitively, so a fix never changes what runs —
/// with two exceptions this pass is built around, both of which it leaves exactly as written:
/// <list type="bullet">
/// <item>Macro names are case-sensitive. <c>scale( 2 )</c> is not a use of <c>#define SCALE</c>, so
/// recasing it would turn a call into a macro expansion. A token spelled exactly like a macro is
/// never touched, no fix may produce a macro's name, and nothing inside a <c>#define</c> is touched.</item>
/// <item>Spelling picks between a script function and a builtin of the same name — stock declares
/// <c>function earthquake()</c> and, in the same file, calls the engine's <c>Earthquake( … )</c>. The
/// caller's lookup answers null for such a name, and the call keeps its spelling.</item>
/// </list>
/// Every fix differs from the source only in case; the token gate checks the output against them.
/// </remarks>
public static partial class GscFormatter
{
    private static string?[] CasingFixes(
        List<SignificantToken> significant,
        ParseResult result,
        TokenRoles roles,
        Func<string?, string, string?>? canonicalFunction)
    {
        string?[] fixes = new string?[significant.Count];
        HashSet<string> macroNames = MacroNames(result.Preprocessed);

        for ( int index = 0; index < significant.Count; index++ )
        {
            if ( roles.InDirective[index] )
            {
                continue;
            }

            Token token = significant[index].Token;
            string text = token.GetText(result.Text).ToString();
            if ( macroNames.Contains(text) )
            {
                continue;
            }

            string? spelling = null;
            if ( TokenFacts.IsKeyword(token.Kind) )
            {
                spelling = text.ToLowerInvariant();
            }
            else if ( token.Kind == TokenKind.Identifier && canonicalFunction is not null
                && IsCallee(significant, index, result, out string? qualifier) )
            {
                spelling = canonicalFunction(qualifier, text);
            }

            bool onlyCaseDiffers = spelling is not null
                && !string.Equals(spelling, text, StringComparison.Ordinal)
                && string.Equals(spelling, text, StringComparison.OrdinalIgnoreCase);
            if ( onlyCaseDiffers && !macroNames.Contains(spelling!) )
            {
                fixes[index] = spelling;
            }
        }

        return fixes;
    }

    /// <summary>Every macro name this file can see, its own and its inserts', exactly as defined.</summary>
    private static HashSet<string> MacroNames(PreprocessResult preprocessed)
    {
        HashSet<string> names = new(StringComparer.Ordinal);
        foreach ( MacroDefinition definition in preprocessed.Macros.All )
        {
            names.Add(definition.Name);
        }

        foreach ( MacroDefinition definition in preprocessed.AllMacroDefinitions )
        {
            names.Add(definition.Name);
        }

        return names;
    }

    /// <summary>
    /// Whether the identifier at <paramref name="index"/> names a function being called or referred
    /// to: <c>foo(</c>, <c>ns::foo</c>, <c>::foo</c> or <c>&amp;foo</c>. Not a declaration's own name,
    /// a member after <c>.</c> or <c>-&gt;</c>, or a class after <c>new</c>, whose spellings are not
    /// this pass's to change.
    /// </summary>
    private static bool IsCallee(List<SignificantToken> significant, int index, ParseResult result, out string? qualifier)
    {
        qualifier = null;
        TokenKind previous = index > 0 ? significant[index - 1].Token.Kind : TokenKind.OpenBrace;
        TokenKind beforePrevious = index > 1 ? significant[index - 2].Token.Kind : TokenKind.OpenBrace;
        TokenKind next = index + 1 < significant.Count ? significant[index + 1].Token.Kind : TokenKind.EndOfFile;

        if ( previous is TokenKind.Dot or TokenKind.Arrow or TokenKind.New )
        {
            return false;
        }

        if ( IsDeclarationName(significant, index) )
        {
            return false;
        }

        if ( previous == TokenKind.ScopeResolution )
        {
            if ( beforePrevious == TokenKind.Identifier )
            {
                qualifier = significant[index - 2].Token.GetText(result.Text).ToString();
            }

            return true;
        }

        if ( previous == TokenKind.Ampersand && IsUnaryHere(beforePrevious, previous) )
        {
            return true;
        }

        return next == TokenKind.OpenParen;
    }

    /// <summary>Whether the identifier is the name in <c>function [private] [autoexec] name(</c>.</summary>
    private static bool IsDeclarationName(List<SignificantToken> significant, int index)
    {
        int walk = index - 1;
        while ( walk >= 0 && significant[walk].Token.Kind is TokenKind.Private or TokenKind.Autoexec )
        {
            walk--;
        }

        return walk >= 0 && significant[walk].Token.Kind == TokenKind.Function;
    }
}
