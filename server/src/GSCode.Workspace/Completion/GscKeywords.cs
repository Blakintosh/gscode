using System.Collections.Immutable;
using GSCode.Core;

namespace GSCode.Workspace.Completion;

/// <summary>What punctuation a keyword takes when it is completed.</summary>
public enum KeywordShape
{
    /// <summary>A plain word: `else`, `break`, `true`. Completes to itself.</summary>
    Word,

    /// <summary>
    /// Call-shaped and a statement in its own right: `self notify( "x" );`. Takes a semicolon on
    /// the same terms a function call does.
    /// </summary>
    StatementCall,

    /// <summary>A statement taking nothing at all: `waittillframeend;`.</summary>
    BareStatement,
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
    /// Control-flow keywords are deliberately absent: `if` needs a body as well as a header, so
    /// completing it usefully is a different job from punctuating a call.
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
    /// game has. Mirrors the lexer's per-profile keyword gating (<c>Keywords.IsEnabled</c>): the
    /// class family, <c>function</c>, <c>foreach</c> and <c>do</c> follow their capability flags,
    /// the import/header/precache directives follow theirs, and everything else is universal.
    /// </summary>
    public static bool IsAvailable(string keyword, GameProfile profile)
    {
        return keyword switch
        {
            "foreach" => profile.HasForeach,
            "do" => profile.HasDoWhile,
            "function" => profile.HasFunctionKeyword,
            "class" or "var" or "new" or "autoexec" or "private" or "constructor" or "destructor" => profile.HasClasses,
            "#using" => profile.ImportStyle == ImportStyle.Namespace,
            "#include" => profile.ImportStyle == ImportStyle.Include,
            "#namespace" => profile.HasNamespaceDirective,
            "#insert" => profile.HasHeaders,
            "#precache" => profile.HasPrecacheDirective,
            _ => true,
        };
    }
}
