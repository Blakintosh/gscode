using System.Collections.Frozen;
using GSCode.Core;

namespace GSCode.Server.Tests.Corpus;

/// <summary>
/// Stock scripts that ship and that nothing loads, so the compiler never sees them and their calls
/// prove nothing about how it resolves one.
///
/// CoD4's seven are listed as rawfiles in zone_source\common_scripts.csv, and no other CoD4 script
/// names them — not in an <c>#include</c>, a path call or anywhere else. They are also the only stock
/// files whose calls need more than the engine's rule: a bare call sees the file's own functions and
/// its direct includes' own, a path call sees the named file's own. A corpus guard may excuse a
/// finding in one of these, and in nothing else.
/// </summary>
internal static class UnloadedStockScripts
{
    private static readonly FrozenSet<string> s_cod4 = new[]
    {
        @"maps\_createpath.gsc",
        @"maps\_documents.gsc",
        @"maps\_guide.gsc",
        @"maps\_leapfrog.gsc",
        @"maps\_mortarteam.gsc",
        @"maps\_photosource.gsc",
        @"maps\_wood.gsc",
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>Whether <paramref name="relativePath"/> is a script <paramref name="game"/> ships and never loads.</summary>
    public static bool Contains(GameProfile game, string relativePath)
    {
        return game == GameProfile.Cod4 && s_cod4.Contains(relativePath);
    }
}
