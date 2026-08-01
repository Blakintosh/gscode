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
/// Those ~980 scripts are BLACK OPS 3's, and the conclusions are NOT transferable — which is why
/// the table is keyed by game. Dev-only-ness is a fact about one engine, not about the language,
/// and CoD4 is the proof: the same count there returns <c>println</c> 220 inside against 438
/// outside, the exact inverse of BO3's 269:2. Lending BO3's list to CoD4 reported 598 Errors
/// across 107 shipped files, every one of them wrong.
///
/// Keyed by game rather than gated by a flag so that a name added to one game's list can never
/// leak into another's. That is the failure mode worth designing out: the bug was not a missing
/// switch but an assumption that a debug function is debug everywhere.
///
/// To add a game, count its own scripts the same way and give it an entry. A game with no entry
/// reports nothing, which is the right answer for one nobody has measured.
/// </summary>
public static class DevOnlyBuiltins
{
    /// <summary>
    /// Black Ops 3, counted across its ~980 stock scripts. Every name here is one those scripts
    /// call only from inside a <c>/# #/</c>.
    /// </summary>
    private static readonly FrozenSet<string> s_blackOps3 =
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

    /// <summary>
    /// Call of Duty 4, counted the same way across its 894 stock scripts: EMPTY, and measured
    /// rather than assumed.
    ///
    /// Of BO3's twenty names, sixteen are never called in CoD4 at all — no evidence either way,
    /// so no claim — and the four that are called are all called OUTSIDE a dev block, which
    /// settles them the other way:
    ///
    /// <code>
    ///   name       inside  outside
    ///   PrintLn       220      438
    ///   Print3d        48       83
    ///   Line           40      110
    ///   Print           7        3
    /// </code>
    ///
    /// Recorded as an explicit empty entry rather than an absent one, because "measured, and the
    /// answer is none" is a different fact from "nobody has looked" — and this is the game that
    /// proves the table has to be per-game at all.
    /// </summary>
    private static readonly FrozenSet<string> s_cod4 = FrozenSet<string>.Empty;

    private static readonly FrozenDictionary<string, FrozenSet<string>> s_byGame =
        new Dictionary<string, FrozenSet<string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["bo3"] = s_blackOps3,
            ["cod4"] = s_cod4,
        }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    /// <summary>The names known dev-only on one game; empty for a game nobody has counted.</summary>
    public static FrozenSet<string> For(GameProfile? profile = null)
    {
        return s_byGame.TryGetValue((profile ?? GameProfile.Active).ShortName, out FrozenSet<string>? names)
            ? names
            : FrozenSet<string>.Empty;
    }

    /// <summary>How many builtins are known dev-only on one game.</summary>
    public static int CountFor(GameProfile? profile = null)
    {
        return For(profile).Count;
    }

    /// <summary>
    /// True when the builtin exists only in a development build ON THIS GAME.
    ///
    /// A game with no measured list answers false for every name, so the check simply does not fire
    /// there. That is the right failure: the alternative — inheriting another game's answers — is
    /// not a weaker version of the check but a wrong one, at Error severity, on working code.
    /// </summary>
    public static bool Contains(string name, GameProfile? profile = null)
    {
        return For(profile).Contains(name);
    }
}
