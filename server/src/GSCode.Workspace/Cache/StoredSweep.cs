namespace GSCode.Workspace.Cache;

/// <summary>One file's diagnostics as the lint sweep left them, for the content it was linted at.</summary>
public sealed record SweptEntry(string Path, ulong ContentHash, byte[] Diagnostics);

/// <summary>
/// The lint sweep's stored results, and the fingerprint of the workspace they were computed against.
/// </summary>
public sealed record StoredSweep(string Fingerprint, IReadOnlyDictionary<string, SweptEntry> Entries);
