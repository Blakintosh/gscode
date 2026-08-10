using System.Collections.Immutable;
using GSCode.Core;

namespace GSCode.Workspace.Completion;

/// <summary>What punctuation a keyword takes when it is completed.</summary>
public enum KeywordShape
{
    /// <summary>A plain word: `else`, `true`. Completes to itself.</summary>
    Word,

    /// <summary>
    /// Call-shaped and a statement in its own right: `self notify( "x" );`. Takes a semicolon on
    /// the same terms a function call does.
    /// </summary>
    StatementCall,

    /// <summary>A statement taking nothing at all: `waittillframeend;`, `break;`, `continue;`.</summary>
    BareStatement,

    /// <summary>
    /// A statement that MAY carry a value: `return;` and `return 5;` are both whole statements.
    ///
    /// Distinct from <see cref="BareStatement"/> only because of where the cursor lands. Completing
    /// to a bare `return;` would be right half the time and cost a cursor move the other half; this
    /// leaves the caret before the terminator, so typing nothing gives `return;` and typing a value
    /// gives `return 5;` without either case needing a correction.
    /// </summary>
    ValueStatement,
}

/// <summary>The language keywords offered in statement-scope completion.</summary>
public static class GscKeywords
{
    /// <summary>
    /// How each call-shaped keyword completes; anything absent is a plain <see cref="KeywordShape.Word"/>.
    ///
    /// All of these are written as calls, so all of them take parentheses and then follow the
    /// same statement rule a function call does. `isdefined` needs no special case: `x = isdefined( f )`
    /// is an assignment statement and takes a semicolon, while `if ( isdefined( f ) )` is not a
    /// statement position at all and does not.
    ///
    /// `wait` accepts both `wait 0.5;` and `wait( 0.5 );`; the parenthesised form is used, being
    /// the one that reads the same as everything around it.
    ///
    /// The BRANCHING control-flow keywords are deliberately absent: `if` needs a body as well as a
    /// header, so completing it usefully is a different job from punctuating a call. The JUMPS are
    /// not in that category — `break`, `continue` and `return` end a statement and nothing follows
    /// them on the line, so their semicolon is never in doubt the way a call's is.
    /// </summary>
    private static readonly Dictionary<string, KeywordShape> s_shapes = new(StringComparer.Ordinal)
    {
        ["isdefined"] = KeywordShape.StatementCall,
        ["notify"] = KeywordShape.StatementCall,
        ["endon"] = KeywordShape.StatementCall,
        ["waittill"] = KeywordShape.StatementCall,
        ["waittillmatch"] = KeywordShape.StatementCall,
        ["wait"] = KeywordShape.StatementCall,
        ["waitrealtime"] = KeywordShape.StatementCall,
        ["waittillframeend"] = KeywordShape.BareStatement,
        ["break"] = KeywordShape.BareStatement,
        ["continue"] = KeywordShape.BareStatement,
        ["return"] = KeywordShape.ValueStatement,
    };

    /// <summary>The shape of a keyword, defaulting to a plain word.</summary>
    public static KeywordShape ShapeOf(string keyword)
    {
        return s_shapes.GetValueOrDefault(keyword, KeywordShape.Word);
    }

    /// <summary>
    /// Statement and expression keywords a user might type in a function body. Global objects
    /// (<c>self</c>, <c>level</c>, …) are NOT here — they come from the active profile
    /// (<see cref="GameProfile.GlobalObjectNames"/>) so a dialect offers exactly its own.
    /// </summary>
    public static ImmutableArray<string> StatementKeywords { get; } =
    [
        "if", "else", "for", "foreach", "while", "do", "switch", "case", "default",
        "return", "break", "continue", "wait", "waitrealtime", "waittill", "waittillmatch",
        "waittillframeend", "thread", "notify", "endon", "isdefined",
        "true", "false", "undefined", "const", "new",
        // Reads as a value rather than opening a statement, but a user types it in a body like any
        // other name. IsAvailable gates it to the dialects whose keyword set lists it — MW2 alone.
        "thisthread",
    ];

    /// <summary>Top-level keywords/directives offered outside a function body.</summary>
    public static ImmutableArray<string> TopLevelKeywords { get; } =
    [
        "function", "class", "var", "autoexec", "private", "constructor", "destructor",
        "#using", "#include", "#insert", "#namespace", "#precache", "#define", "#using_animtree",
        // Documented in KeywordDocs and hoverable, but previously never offered.
        "#animtree", "#if", "#elif", "#else", "#endif",
    ];

    /// <summary>
    /// Whether a keyword/directive exists in the given dialect, so completion offers only what the
    /// game has. Directives are gated by their own capability flags (import style, headers, precache);
    /// every plain keyword is gated by the profile's keyword SET — the same data the lexer gates on
    /// (<see cref="GameProfile.IsKeyword"/>), so completion and lexing can never disagree about which
    /// words a game treats as keywords.
    /// </summary>
    public static bool IsAvailable(string keyword, GameProfile profile)
    {
        switch ( keyword )
        {
            case "#using":
                return profile.ImportStyle == ImportStyle.Namespace;
            case "#include":
                return profile.ImportStyle == ImportStyle.Include;
            case "#namespace":
                return profile.HasNamespaceDirective;
            case "#insert":
                return profile.HasHeaders;
            case "#precache":
                return profile.HasPrecacheDirective;
        }

        // The remaining directives (#define, #using_animtree, #animtree, the #if family) exist across
        // the whole lineage.
        if ( keyword.StartsWith('#') )
        {
            return true;
        }

        // Everything else is a keyword iff the dialect's keyword set lists it.
        return profile.IsKeyword(keyword);
    }
}
