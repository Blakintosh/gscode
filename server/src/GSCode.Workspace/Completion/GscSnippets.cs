using System.Collections.Immutable;
using GSCode.Core;

namespace GSCode.Workspace.Completion;

/// <summary>
/// The snippets whose construct only exists in SOME dialects, and which therefore cannot live in
/// the extension's contributed snippet files.
///
/// A contributed snippet is registered against a language id, and one language id (`gsc`) covers
/// five games. VS Code merges those snippets into the completion list unconditionally and there is
/// no way to withdraw one — so `foreach`, `class`, `new`, `#using` and the rest were offered while
/// editing CoD4, which has none of them. Typing the foreach snippet there produced a construct the
/// game cannot run, and the server then reported the result as an unresolved call.
///
/// Only the server knows which game is active, so these belong here. The UNIVERSAL snippets — if,
/// for, while, switch, waittill, notify, the dev block — stay in `client/snippets/common.json`,
/// where they cost nothing and work offline before the server has started.
///
/// The function declaration is not here either: <c>CompletionEngine.FunctionDeclarationSnippet</c>
/// already writes it the way the dialect declares one, which is what the client's `func`/`funciw`
/// pair was doing by hand.
/// </summary>
public static class GscSnippets
{
    /// <summary>
    /// One snippet and the word that decides whether the dialect has it.
    /// </summary>
    /// <param name="Label">What is typed and matched — also what the list shows.</param>
    /// <param name="Body">Snippet text, with tab stops. The handler infers snippet format from those.</param>
    /// <param name="GatedOn">
    /// A keyword or directive passed to <see cref="GscKeywords.IsAvailable"/>, so a snippet and the
    /// word it writes can never disagree about which games have it. Empty when the gate is not a
    /// word at all — see <see cref="ScriptDoc"/>.
    /// </param>
    /// <param name="Retrigger">
    /// Whether accepting this snippet should reopen the suggestion list, because the tab stop it
    /// leaves the cursor on has a CLOSED VOCABULARY the engine can answer for. Only `precache` sets
    /// it: its first argument is an asset type, and the list of those is per-world, so carrying the
    /// names in the body here would be a second copy of <c>PrecacheAssetTypes</c> that the .gsc/.csc
    /// split would then have to be applied to twice.
    /// </param>
    public sealed record Entry(
        string Label,
        string Body,
        string Documentation,
        string GatedOn,
        bool InsideFunction,
        bool Retrigger = false);

    /// <summary>
    /// Bodies are the ones the client contributed, unchanged: they were copied from the shape the
    /// stock scripts use, and re-deriving them here would be a second source for the same text.
    /// Backslashes are doubled because a snippet body escapes them.
    /// </summary>
    private static readonly ImmutableArray<Entry> s_entries =
    [
        new Entry(
            "foreach",
            "foreach ( ${1:value} in ${2:array} )\n{\n\t$0\n}",
            "foreach loop.",
            "foreach",
            InsideFunction: true),
        new Entry(
            "foreachkv",
            "foreach ( ${1:key}, ${2:value} in ${3:array} )\n{\n\t$0\n}",
            "foreach binding both key and value.",
            "foreach",
            InsideFunction: true),
        new Entry(
            "new",
            "${1:instance} = new ${2:Class}();",
            "Construct a class instance.",
            "new",
            InsideFunction: true),
        new Entry(
            "class",
            "class ${1:Name}\n{\n\tvar ${2:member};\n\n\tconstructor()\n\t{\n\t\t$0\n\t}\n\n\tdestructor()\n\t{\n\t}\n}",
            "Class with a constructor and destructor.",
            "class",
            InsideFunction: false),
        new Entry(
            "funcauto",
            "function autoexec ${1:name}()\n{\n\t$0\n}",
            "Function that runs once on load.",
            "autoexec",
            InsideFunction: false),
        new Entry(
            "funcpriv",
            "function private ${1:name}( ${2} )\n{\n\t$0\n}",
            "Function visible only to files declaring the same namespace.",
            "private",
            InsideFunction: false),
        new Entry(
            "using",
            @"#using scripts\\${1:shared}\\${2:util_shared};",
            "Import a namespace.",
            "#using",
            InsideFunction: false),
        new Entry(
            "insert",
            @"#insert scripts\\${1:shared}\\${2:shared}.gsh;",
            "Paste a header's macros into this file. The path must name a .gsh.",
            "#insert",
            InsideFunction: false),
        new Entry(
            "namespace",
            "#namespace ${1:name};",
            "Declare this file's namespace.",
            "#namespace",
            InsideFunction: false),
        new Entry(
            "include",
            @"#include ${1:maps}\\${2:_utility};",
            "Merge a file's functions into this scope.",
            "#include",
            InsideFunction: false),

        // The client contributed this one until it became the last snippet file that broke the rule
        // the other move established: #precache is BO3's alone (HasPrecacheDirective), and a
        // contributed snippet cannot be withdrawn per game, so a CoD4 file was offered a directive
        // its game does not have. The body is the same one DirectiveSnippet writes for a typed '#',
        // and Retrigger hands the asset type to the same completion arm — so the two routes to a
        // #precache produce identical text and identical vocabulary.
        new Entry(
            "precache",
            "#precache( \"$1\", \"${2:asset}\" );",
            "Precache an asset at load.",
            "#precache",
            InsideFunction: false,
            Retrigger: true),
    ];

    /// <summary>
    /// The ScriptDoc pair, gated on comment STYLE rather than on a word — the two forms are not
    /// keywords and no directive names them, so <see cref="GscKeywords.IsAvailable"/> has nothing
    /// to answer with. See <see cref="GameProfile.ScriptDocStyle"/>.
    /// </summary>
    private static readonly Entry AtSignScriptDoc = new(
        "doc",
        "/@\n\"Name: ${1:name}( <${2:arg}> )\"\n\"Summary: ${3:What it does.}\"\n\"Module: ${4:Utility}\"\n"
            + "\"CallOn: ${5}\"\n\"MandatoryArg: <${2:arg}> : ${6:description}\"\n\"Example: ${7}\"\n"
            + "\"SPMP: ${8|both,singleplayer,multiplayer|}\"\n@/",
        "ScriptDoc block, with the tags the stock scripts use.",
        "",
        InsideFunction: false);

    private static readonly Entry TripleSlashScriptDoc = new(
        "doc",
        "/*\n\tName: ${1:name}( <${2:arg}> )\n\tSummary: ${3:What it does.}\n\tModule: ${4:Utility}\n"
            + "\tMandatoryArg: <${2:arg}> : ${5:description}\n\tExample: ${6}\n*/",
        "ScriptDoc block.",
        "",
        InsideFunction: false);

    private static Entry ScriptDoc(GameProfile game)
    {
        return game.ScriptDocStyle == ScriptDocStyle.AtSign ? AtSignScriptDoc : TripleSlashScriptDoc;
    }

    /// <summary>The snippets this dialect has, for one completion scope.</summary>
    public static ImmutableArray<Entry> For(GameProfile game, bool insideFunction)
    {
        ImmutableArray<Entry>.Builder available = ImmutableArray.CreateBuilder<Entry>();
        foreach ( Entry entry in s_entries )
        {
            if ( entry.InsideFunction == insideFunction && GscKeywords.IsAvailable(entry.GatedOn, game) )
            {
                available.Add(entry);
            }
        }

        // Every dialect has SOME ScriptDoc form, so this one is chosen rather than filtered.
        if ( !insideFunction )
        {
            available.Add(ScriptDoc(game));
        }

        return available.ToImmutable();
    }
}
