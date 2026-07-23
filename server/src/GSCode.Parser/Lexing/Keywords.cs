using System.Collections.Frozen;
using GSCode.Core;

namespace GSCode.Parser.Lexing;

/// <summary>
/// Keyword and directive lookup tables. Keywords match case-insensitively (the language
/// reference's own examples use Function/Do/Break); directives match case-sensitively
/// and lowercase, mirroring engine behavior.
/// </summary>
public static class Keywords
{
    private static readonly FrozenDictionary<string, TokenKind> s_keywords = new Dictionary<string, TokenKind>(StringComparer.OrdinalIgnoreCase)
    {
        ["class"] = TokenKind.Class,
        ["function"] = TokenKind.Function,
        ["var"] = TokenKind.Var,
        ["return"] = TokenKind.Return,
        ["wait"] = TokenKind.Wait,
        ["waittill"] = TokenKind.WaitTill,
        ["waittillmatch"] = TokenKind.WaitTillMatch,
        ["waittillframeend"] = TokenKind.WaitTillFrameEnd,
        ["waitrealtime"] = TokenKind.WaitRealTime,
        ["thread"] = TokenKind.Thread,
        ["if"] = TokenKind.If,
        ["else"] = TokenKind.Else,
        ["do"] = TokenKind.Do,
        ["while"] = TokenKind.While,
        ["for"] = TokenKind.For,
        ["foreach"] = TokenKind.Foreach,
        ["in"] = TokenKind.In,
        ["new"] = TokenKind.New,
        ["switch"] = TokenKind.Switch,
        ["case"] = TokenKind.Case,
        ["default"] = TokenKind.Default,
        ["break"] = TokenKind.Break,
        ["continue"] = TokenKind.Continue,
        ["notify"] = TokenKind.Notify,
        ["endon"] = TokenKind.Endon,
        ["assert"] = TokenKind.Assert,
        ["assertmsg"] = TokenKind.AssertMsg,
        ["constructor"] = TokenKind.Constructor,
        ["destructor"] = TokenKind.Destructor,
        ["autoexec"] = TokenKind.Autoexec,
        ["private"] = TokenKind.Private,
        ["const"] = TokenKind.Const,
        ["isdefined"] = TokenKind.IsDefined,
        ["undefined"] = TokenKind.Undefined,
        ["true"] = TokenKind.True,
        ["false"] = TokenKind.False,
        ["vectorscale"] = TokenKind.VectorScale,
        ["profilestart"] = TokenKind.ProfileStart,
        ["profilestop"] = TokenKind.ProfileStop,
    }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    private static readonly FrozenDictionary<string, TokenKind>.AlternateLookup<ReadOnlySpan<char>> s_keywordSpanLookup =
        s_keywords.GetAlternateLookup<ReadOnlySpan<char>>();

    private static readonly FrozenDictionary<string, TokenKind> s_directives = new Dictionary<string, TokenKind>(StringComparer.Ordinal)
    {
        ["using"] = TokenKind.UsingDirective,
        ["insert"] = TokenKind.InsertDirective,
        ["define"] = TokenKind.DefineDirective,
        ["namespace"] = TokenKind.NamespaceDirective,
        ["precache"] = TokenKind.PrecacheDirective,
        ["using_animtree"] = TokenKind.UsingAnimTreeDirective,
        ["animtree"] = TokenKind.AnimTreeDirective,
        ["if"] = TokenKind.IfDirective,
        ["elif"] = TokenKind.ElifDirective,
        ["else"] = TokenKind.ElseDirective,
        ["endif"] = TokenKind.EndifDirective,
    }.ToFrozenDictionary(StringComparer.Ordinal);

    private static readonly FrozenDictionary<string, TokenKind>.AlternateLookup<ReadOnlySpan<char>> s_directiveSpanLookup =
        s_directives.GetAlternateLookup<ReadOnlySpan<char>>();

    /// <summary>Matches an identifier-shaped word against the keyword table (case-insensitive).</summary>
    public static bool TryMatchKeyword(ReadOnlySpan<char> word, out TokenKind kind)
    {
        return s_keywordSpanLookup.TryGetValue(word, out kind);
    }

    /// <summary>
    /// Matches a word against the keyword table, but only for keywords the game actually has: a
    /// keyword the dialect lacks (e.g. <c>foreach</c> before MW2, <c>function</c>/<c>class</c> in
    /// the Infinity Ward games) stays an ordinary identifier, so a script may use it as a name.
    ///
    /// BO3 has every keyword, so its lexing is unchanged.
    /// </summary>
    public static bool TryMatchKeyword(ReadOnlySpan<char> word, GameProfile profile, out TokenKind kind)
    {
        return s_keywordSpanLookup.TryGetValue(word, out kind) && IsEnabled(kind, profile);
    }

    /// <summary>Whether a keyword exists in the given game's dialect.</summary>
    private static bool IsEnabled(TokenKind kind, GameProfile profile)
    {
        switch ( kind )
        {
            case TokenKind.Function:
                return profile.HasFunctionKeyword;
            case TokenKind.Foreach:
                return profile.HasForeach;
            case TokenKind.Do:
                return profile.HasDoWhile;

            // The class system and its function modifiers are all BO3-only.
            case TokenKind.Class:
            case TokenKind.New:
            case TokenKind.Var:
            case TokenKind.Constructor:
            case TokenKind.Destructor:
            case TokenKind.Autoexec:
            case TokenKind.Private:
                return profile.HasClasses;

            default:
                return true;
        }
    }

    /// <summary>Matches the word after '#' against the directive table (case-sensitive, whole word).</summary>
    public static bool TryMatchDirective(ReadOnlySpan<char> word, out TokenKind kind)
    {
        return s_directiveSpanLookup.TryGetValue(word, out kind);
    }
}
