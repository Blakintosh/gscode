using System.Collections.Frozen;

namespace GSCode.Workspace.Api;

/// <summary>
/// Documentation for the language's evaluation/function-usage keywords and preprocessor
/// directives (the reference material the GSC language PDF describes). Powers keyword and
/// directive hover plus completion detail. Keys are looked up case-insensitively; directive
/// keys keep their leading '#'. Note: assert / assertmsg are NOT here — they are engine
/// builtins served by the API library, not keywords.
/// </summary>
public static class KeywordDocs
{
    private static readonly FrozenDictionary<string, string> s_docs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        // Evaluation / function-usage keywords.
        ["wait"] = "Pauses the current thread for the given number of seconds of game time.\n\n```gsc\nwait( seconds );\n```",
        ["waitrealtime"] = "Pauses the current thread for the given number of seconds of real time, unaffected by the game's time scale.\n\n```gsc\nwaitrealtime( seconds );\n```",
        ["waittill"] = "Blocks the current thread until the entity sends the named notify, binding any returned parameters.\n\n```gsc\nself waittill( \"event\", arg1, arg2 );\n```",
        ["waittillmatch"] = "Blocks the current thread until the entity sends the named notify whose parameters match the given values.\n\n```gsc\nself waittillmatch( \"event\", value );\n```",
        ["waittillframeend"] = "Blocks the current thread until the end of the current server frame.\n\n```gsc\nwaittillframeend;\n```",
        ["notify"] = "Sends a notification on an entity, waking every thread waiting on that notify.\n\n```gsc\nself notify( \"event\", arg1, arg2 );\n```",
        ["endon"] = "Ends the current thread if the entity sends the named notify.\n\n```gsc\nself endon( \"event\" );\n```",
        ["isdefined"] = "Returns true when the value is defined (not `undefined`).\n\n```gsc\nif ( isdefined( value ) )\n```",
        ["vectorscale"] = "Returns the vector multiplied by the scalar.\n\n```gsc\nvectorscale( vector, scale );\n```",
        ["profilestart"] = "Begins a named profiling scope for performance measurement (paired with `profilestop`).",
        ["profilestop"] = "Ends the most recent profiling scope opened by `profilestart`.",
        ["size"] = "The number of elements in an array or characters in a string (read-only `int`).\n\n```gsc\ncount = array.size;\n```",
        ["vararg"] = "The arguments passed beyond a function's named parameters, as an array. Bound by declaring `...` in the parameter list, where it must be the last entry.\n\n```gsc\nfunction f( first, ... )\n{\n\tforeach ( extra in vararg )\n\t{\n\t\tuse( extra );\n\t}\n}\n```",

        // Preprocessor directives.
        ["#using"] = "Imports another script file so its namespace's functions can be called. Must appear before the first function or class.\n\n```gsc\n#using scripts\\shared\\util_shared;\n```",
        ["#insert"] = "Textually inserts a GSH header file's contents (macros and definitions) at this point.\n\n```gsc\n#insert scripts\\shared\\shared.gsh;\n```",
        ["#define"] = "Defines a preprocessor macro (object-like or function-like). Macro names are case-sensitive.\n\n```gsc\n#define MAX_PLAYERS 18\n```",
        ["#namespace"] = "Sets the namespace for functions declared below it, until the next `#namespace` directive.\n\n```gsc\n#namespace util;\n```",
        ["#precache"] = "Precaches an asset of the given type at load time.\n\n```gsc\n#precache( \"model\", \"my_model\" );\n```",
        ["#using_animtree"] = "Selects the animation tree that `#animtree` references in this file.\n\n```gsc\n#using_animtree( \"generic\" );\n```",
        ["#animtree"] = "References the animation tree selected by `#using_animtree`.",
        ["#if"] = "Conditional compilation: includes the enclosed code only when the condition evaluates true.",
        ["#elif"] = "Conditional compilation: an alternative branch for a preceding `#if` when its condition was false.",
        ["#else"] = "Conditional compilation: the fallback branch when no preceding `#if`/`#elif` condition was true.",
        ["#endif"] = "Closes a `#if` conditional-compilation block.",
    }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    /// <summary>The documentation for a keyword or directive (with its '#'), or null when undocumented.</summary>
    public static string? Find(string word)
    {
        return s_docs.TryGetValue(word, out string? doc) ? doc : null;
    }
}
