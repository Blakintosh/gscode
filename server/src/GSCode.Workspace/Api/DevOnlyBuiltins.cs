using System.Collections.Frozen;
using GSCode.Core;

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
/// Those ~980 scripts are BLACK OPS 3's, and the conclusions are not transferable — which is why
/// the list is now gated on <c>GameProfile.HasCuratedDevOnlyBuiltins</c>. Dev-only-ness is a fact
/// about one engine, not about the language: run the same count over CoD4 and <c>println</c> comes
/// back 220 inside against 438 outside, the exact inverse of BO3's 269:2. Applying this table
/// there reported 598 Errors across 107 shipped files, every one of them wrong.
///
/// To extend it to another game, count that game's scripts the same way and set the flag on its
/// profile. A name that is dev-only everywhere still has to be measured everywhere.
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

    /// <summary>
    /// True when the builtin exists only in a development build ON THIS GAME.
    ///
    /// A game with no validated list answers false for every name: the check then simply does not
    /// fire there, which is the right failure. The alternative — inheriting BO3's answers — is not
    /// a lesser version of the check but a wrong one, and at Error severity.
    /// </summary>
    public static bool Contains(string name, GameProfile? profile = null)
    {
        return (profile ?? GameProfile.Active).HasCuratedDevOnlyBuiltins && s_names.Contains(name);
    }
}
