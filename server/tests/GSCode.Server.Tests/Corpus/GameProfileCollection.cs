using Xunit;

namespace GSCode.Server.Tests.Corpus;

/// <summary>
/// The collection every corpus test belongs to, which is what stops them running in parallel.
///
/// <c>GameProfile.Active</c> is process-global and the sweeps MOVE it: several lints fall back to it,
/// the indexer enumerates through <c>Active.ScriptGlobs</c>, and the parser gates keywords and
/// directives on it. xUnit's unit of parallelism is the COLLECTION, and with every class in its own
/// implicit one the BO3 corpus tests ran beside a sweep that had selected CoD4 — so BO3's own scripts
/// were parsed as CoD4 and 861 of 980 "failed to parse", reporting <c>function</c> and <c>#using</c>
/// as unknown.
///
/// The constraint was written down long before it was enforced ("this class must not run beside
/// anything that reads Active"), and held only by luck: one test switched profiles and the timing
/// happened to work out. Adding more switching tests broke the luck rather than the rule. A comment
/// cannot serialize anything, so the rule lives here now.
///
/// Every class under Corpus/ joins, not merely the ones that switch: reading <c>Active</c> while
/// another class writes it is the same race from the other side.
/// </summary>
[CollectionDefinition(Name)]
public sealed class GameProfileCollection
{
    public const string Name = "GameProfile";
}
