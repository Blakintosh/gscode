using System.Collections.Frozen;
using GSCode.Core.Symbols;

namespace GSCode.Workspace.Typing;

/// <summary>
/// Return types for the callable keywords that carry no entry in the bundled builtin API,
/// so the flow typer can type them instead of giving up. Successor to v1's EmulatedFunctions.
///
/// Deliberately short. Of the callable keywords absent from the API — isdefined, vectorscale,
/// profilestart, profilestop, waittill, waittillmatch, notify, endon — only the first two
/// yield a value worth typing. The rest are statement-shaped and produce nothing, and in this
/// lattice a void result and Unknown are indistinguishable, so listing them would add entries
/// that change no outcome. Every other builtin gets its return type from the API JSON.
/// </summary>
public static class BuiltinEmulations
{
    private static readonly FrozenDictionary<string, ScrType> s_returnTypes =
        new Dictionary<string, ScrType>(StringComparer.OrdinalIgnoreCase)
        {
            ["isdefined"] = ScrType.Bool,
            ["vectorscale"] = ScrType.Vector,
        }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    /// <summary>The emulated return type for a keyword call, when one is known.</summary>
    public static bool TryGetReturnType(string name, out ScrType type)
    {
        return s_returnTypes.TryGetValue(name, out type);
    }
}
