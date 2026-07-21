using System.Collections.Immutable;

namespace GSCode.Workspace.Completion;

/// <summary>The language keywords offered in statement-scope completion.</summary>
public static class GscKeywords
{
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
