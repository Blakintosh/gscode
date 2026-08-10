using System.Collections.Frozen;

namespace GSCode.Workspace.Api;

/// <summary>
/// Engine builtins that only exist in a development build, so calling one outside a
/// <c>/# #/</c> dev block breaks once the mod ships.
///
/// Curated here rather than read from the API data, because the bundled
/// <c>t7_api_gsc.json</c> carries no such marker — its <c>flags</c> field records
/// documentation status (processed, verified, unlisted, Broken), not engine availability.
/// This follows the same pattern as <see cref="KeywordDocs"/> and
/// <c>Typing.BuiltinEmulations</c>: game knowledge that has no home in the generated data
/// lives in a small declarative table.
///
/// This drives an Error-severity diagnostic, so a wrong entry flags working code — worse than
/// the check not existing. Add a name only when it is known to be dev-only; the check starts
/// covering it with no other change.
///
/// The data cannot be derived from mechanically, for two reasons. It is inconsistent —
/// <c>Print</c>'s description says "Development only" but <c>PrintLn</c>'s says merely "Writes
/// a line to the console" despite being equally dev-only — and it has no category field at all,
/// so the "Debug" grouping that exists conceptually is absent from the JSON.
///
/// This list was therefore validated against the shipped corpus rather than taken from the
/// descriptions: each name was counted inside versus outside `/# #/` across ~980 stock scripts.
/// Names here are ones stock code only ever calls from inside a dev block (Line 67:0,
/// Record3DText 71:0, PrintLn 269:2). Two candidates whose descriptions call them debug
/// instruments were REJECTED because stock code calls them outside dev blocks and never inside
/// — <c>PixMarker</c> (0:2) and <c>InfoVolumeDebugInit</c> (0:1) — and <c>GetDebugEye</c> was
/// left out as an ambiguous getter with no usages either way.
///
/// THOSE ~980 SCRIPTS ARE BLACK OPS 3'S, and the answers do not carry to another engine. This is a
/// fallback, not a universal truth: <see cref="ApiLoader"/> reads <c>entry.DevOnly ??
/// DevOnlyBuiltins.Contains(name)</c>, so a game whose API data states the answer wins, and only a
/// game that states nothing lands here.
///
/// CoD4 is why that ordering matters. Counted the same way over its 894 scripts, <c>println</c>
/// comes back 438 calls OUTSIDE a dev block against 220 inside — the inverse of BO3's 269:2 — and
/// this table applied to it reported 598 Errors across 107 shipped files. Its four affected names
/// (<c>PrintLn</c>, <c>Print3d</c>, <c>Line</c>, <c>Print</c>: the only four of these twenty that
/// its library even has) now carry <c>"devOnly": false</c> from
/// <c>sources/curated/cod4_api_overrides.json</c>, each with its count as the reason.
///
/// So a game is corrected in ITS OWN DATA rather than by weakening this list. Before adding a name
/// here, remember it will apply to every game that does not say otherwise, and check whether the
/// name belongs to the shared debug vocabulary or only to T7's.
/// </summary>
public static class DevOnlyBuiltins
{
    private static readonly FrozenSet<string> s_names =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            // Console writers.
            "Print",
            "PrintLn",
            "PrintTopRightln",

            // Debug draw primitives.
            "Box",
            "Circle",
            "DebugStar",
            "Line",
            "LineList",
            "Print3d",
            "Sphere",
            "SphericalCone",

            // Debug recorder.
            "Record3DText",
            "RecordCone",
            "RecordEnt",
            "RecordEntText",
            "RecordSphere",
            "RecordStar",

            // Misc development-build helpers.
            "DebugBreak",
            "SetAnimForceNew",
            "SetDebugSideSwitch",
        }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>How many builtins are currently known to be dev-only.</summary>
    public static int Count => s_names.Count;

    /// <summary>True when the builtin exists only in a development build.</summary>
    public static bool Contains(string name)
    {
        return s_names.Contains(name);
    }
}
