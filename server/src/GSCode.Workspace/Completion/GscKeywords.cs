using System.Collections.Immutable;

namespace GSCode.Workspace.Completion;

/// <summary>What punctuation a keyword takes when it is completed.</summary>
public enum KeywordShape
{
    /// <summary>A plain word: `else`, `break`, `true`. Completes to itself.</summary>
    Word,

    /// <summary>
    /// Call-shaped, but only ever part of a larger expression — `isdefined( x )` is a condition,
    /// never a statement, so it must not gain a semicolon however statement-like the position
    /// looks.
    /// </summary>
    ExpressionCall,

    /// <summary>
    /// Call-shaped and a statement in its own right: `self notify( "x" );`. Takes a semicolon on
    /// the same terms a function call does.
    /// </summary>
    StatementCall,

    /// <summary>A statement taking a value with no parentheses: `wait 0.5;`.</summary>
    ValueStatement,

    /// <summary>A statement taking nothing at all: `waittillframeend;`.</summary>
    BareStatement,
}

/// <summary>The language keywords offered in statement-scope completion.</summary>
public static class GscKeywords
{
    /// <summary>
    /// How each call-shaped keyword completes; anything absent is a plain <see cref="KeywordShape.Word"/>.
    ///
    /// The distinction that matters is not keyword-versus-function but EXPRESSION-versus-STATEMENT.
    /// `isdefined` is only ever a condition, so it takes parentheses and never a semicolon, while
    /// `notify` and `endon` are statements and take both. `wait` takes a value and no parentheses
    /// at all — which is why one rule for everything call-shaped would be wrong.
    ///
    /// Control-flow keywords are deliberately absent: `if` needs a body as well as a header, so
    /// completing it usefully is a different job from punctuating a call.
    /// </summary>
    private static readonly Dictionary<string, KeywordShape> s_shapes = new(StringComparer.Ordinal)
    {
        ["isdefined"] = KeywordShape.ExpressionCall,
        ["notify"] = KeywordShape.StatementCall,
        ["endon"] = KeywordShape.StatementCall,
        ["waittill"] = KeywordShape.StatementCall,
        ["waittillmatch"] = KeywordShape.StatementCall,
        ["wait"] = KeywordShape.ValueStatement,
        ["waitrealtime"] = KeywordShape.ValueStatement,
        ["waittillframeend"] = KeywordShape.BareStatement,
    };

    /// <summary>The shape of a keyword, defaulting to a plain word.</summary>
    public static KeywordShape ShapeOf(string keyword)
    {
        return s_shapes.GetValueOrDefault(keyword, KeywordShape.Word);
    }

    /// <summary>Statement and expression keywords a user might type in a function body.</summary>
    public static ImmutableArray<string> StatementKeywords { get; } =
    [
        "if", "else", "for", "foreach", "while", "do", "switch", "case", "default",
        "return", "break", "continue", "wait", "waitrealtime", "waittill", "waittillmatch",
        "waittillframeend", "thread", "notify", "endon", "isdefined",
        "true", "false", "undefined", "self", "level", "game", "world", "anim", "const", "new",
    ];

    /// <summary>Top-level keywords/directives offered outside a function body.</summary>
    public static ImmutableArray<string> TopLevelKeywords { get; } =
    [
        "function", "class", "var", "autoexec", "private", "constructor", "destructor",
        "#using", "#insert", "#namespace", "#precache", "#define", "#using_animtree",
        // Documented in KeywordDocs and hoverable, but previously never offered.
        "#animtree", "#if", "#elif", "#else", "#endif",
    ];
}
