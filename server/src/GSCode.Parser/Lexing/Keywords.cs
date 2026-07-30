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
        // childthread and call (MW2+, Infinity Ward line) are their own kinds — childthread parses
        // like a threaded call, call like a synchronous one. Both are gated per profile, so they are
        // keywords only where the dialect's keyword set lists them (BO3 uses neither, so there they
        // stay ordinary identifiers).
        ["childthread"] = TokenKind.ChildThread,
        ["call"] = TokenKind.Call,
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
        ["prof_begin"] = TokenKind.ProfileStart,
        ["prof_end"] = TokenKind.ProfileStop,
        // The pack bound by `...`. Gated per profile like childthread/call above: BO3 lists it, so
        // a pre-BO3 script using `vararg` as a variable name keeps working.
        ["vararg"] = TokenKind.Vararg,
    }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    private static readonly FrozenDictionary<string, TokenKind>.AlternateLookup<ReadOnlySpan<char>> s_keywordSpanLookup =
        s_keywords.GetAlternateLookup<ReadOnlySpan<char>>();

    private static readonly FrozenDictionary<string, TokenKind> s_directives = new Dictionary<string, TokenKind>(StringComparer.Ordinal)
    {
        ["using"] = TokenKind.UsingDirective,
        ["include"] = TokenKind.IncludeDirective,
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
    /// Every kind this table can produce. Exists so <c>TokenFacts.IsKeyword</c>'s contiguous-range
    /// assumption can be CHECKED against the table rather than trusted: a kind added here but placed
    /// outside that range lexes as a keyword while every consumer treats it as an identifier, which
    /// is a silent failure rather than a build error.
    /// </summary>
    public static IEnumerable<TokenKind> AllKeywordKinds
    {
        get { return s_keywords.Values.Distinct(); }
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
        return s_keywordSpanLookup.TryGetValue(word, out kind) && profile.IsKeyword(word);
    }

    /// <summary>Matches the word after '#' against the directive table (case-sensitive, whole word).</summary>
    public static bool TryMatchDirective(ReadOnlySpan<char> word, out TokenKind kind)
    {
        return s_directiveSpanLookup.TryGetValue(word, out kind);
    }

    /// <summary>
    /// Matches a directive, but only those the game has: <c>#include</c> is the Infinity Ward import
    /// and <c>#using</c> / <c>#namespace</c> / <c>#insert</c> / <c>#precache</c> are BO3, so a
    /// directive from the wrong family is left unmatched (and reported as unknown). The shared ones
    /// — <c>#define</c>, animtree, and the <c>#if</c> family — are never gated. BO3 has all of its
    /// own, so its lexing is unchanged.
    /// </summary>
    public static bool TryMatchDirective(ReadOnlySpan<char> word, GameProfile profile, out TokenKind kind)
    {
        return s_directiveSpanLookup.TryGetValue(word, out kind) && IsDirectiveEnabled(kind, profile);
    }

    private static bool IsDirectiveEnabled(TokenKind kind, GameProfile profile)
    {
        switch ( kind )
        {
            case TokenKind.IncludeDirective:
                return profile.ImportStyle == ImportStyle.Include;
            case TokenKind.UsingDirective:
                return profile.ImportStyle == ImportStyle.Namespace;
            case TokenKind.NamespaceDirective:
                return profile.HasNamespaceDirective;
            case TokenKind.InsertDirective:
                return profile.HasHeaders;
            case TokenKind.PrecacheDirective:
                return profile.HasPrecacheDirective;
            default:
                // #define, #using_animtree, #animtree, and the #if family exist across the lineage.
                return true;
        }
    }
}
