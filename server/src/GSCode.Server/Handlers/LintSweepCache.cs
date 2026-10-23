using System.Buffers.Binary;
using System.Collections.Immutable;
using System.IO.Hashing;
using System.Text;
using GSCode.Core;
using GSCode.Core.Symbols;
using GSCode.Workspace.Cache;
using GSCode.Workspace.Database;
using Diagnostic = GSCode.Core.Diagnostics.Diagnostic;

namespace GSCode.Server.Handlers;

/// <summary>What <see cref="LintSweepCache.Capture"/> took: the workspace's fingerprint and every file's set.</summary>
public sealed record SweepCapture(string Fingerprint, List<SweptEntry> Entries);

/// <summary>
/// The full-mode lint sweep's results kept in the workspace cache, for a start whose workspace has
/// not changed since they were computed.
///
/// Every <c>workspaceIndexingMode: full</c> start ran the whole sweep, warm or cold: a minute on every
/// core at 50,000 heavy BO3 files and two at 50,000 heavy cod4 files, to arrive at the diagnostics the
/// previous session already had. A cross-file lint result depends on every file it can see, so the
/// rule is all or nothing: the stored results are used only when the fingerprint of the whole
/// workspace — every record's path, language and content hash, headers included — is the one they
/// were computed against. Anything else, one changed signature in one file included, sweeps as before.
///
/// Everything else a lint reads is already part of the cache's identity, which empties the cache when
/// it changes: the server build, the bundled data and the game. The lints read no settings.
/// </summary>
public static class LintSweepCache
{
    /// <summary>The workspace as the lints see it: every record's path, language and content hash.</summary>
    public static string Fingerprint(ScriptDatabase database)
    {
        List<ScriptRecord> records = [.. database.AllRecords];
        records.Sort(static (left, right) => string.CompareOrdinal(left.Path, right.Path));

        // A path and a context id never contain NUL, so one separates them without ambiguity.
        XxHash64 hash = new();
        Span<byte> number = stackalloc byte[8];
        byte[] separator = [0];
        foreach ( ScriptRecord record in records )
        {
            hash.Append(Encoding.UTF8.GetBytes(record.Path));
            hash.Append(separator);
            hash.Append(Encoding.UTF8.GetBytes(record.ContextId));
            hash.Append(separator);
            BinaryPrimitives.WriteUInt64LittleEndian(number, (ulong)record.Language);
            hash.Append(number);
            BinaryPrimitives.WriteUInt64LittleEndian(number, record.ContentHash);
            hash.Append(number);
        }

        return $"{hash.GetCurrentHashAsUInt64():x16}:{records.Count}";
    }

    /// <summary>
    /// Every GSC and CSC file's stored diagnostics and the fingerprint they go with, or null when a
    /// record changed while they were being read — there is then no one workspace they describe.
    /// A record with unsaved editor state is left out: the next start reads that file from disk.
    /// </summary>
    public static SweepCapture? Capture(ScriptDatabase database)
    {
        string before = Fingerprint(database);

        List<SweptEntry> entries = [];
        foreach ( ScriptRecord record in database.AllRecords )
        {
            if ( record.Language is not (ScriptLanguage.Gsc or ScriptLanguage.Csc) || record.IsDirty )
            {
                continue;
            }

            entries.Add(new SweptEntry(record.Path, record.ContentHash, RecordSerializer.SerializeDiagnostics(record.Diagnostics)));
        }

        return Fingerprint(database) == before ? new SweepCapture(before, entries) : null;
    }

    /// <summary>
    /// Puts the stored diagnostics back on every record they still describe, and returns the paths of
    /// the GSC and CSC files they do not — a file that was open and unsaved when they were stored, or
    /// one that changed underneath — which the caller lints. Null, and nothing touched, when the
    /// workspace is not the one they were computed against.
    /// </summary>
    public static List<string>? TryRestore(ScriptDatabase database, StoredSweep stored, NameTable names, out int restored)
    {
        restored = 0;
        if ( stored.Fingerprint != Fingerprint(database) )
        {
            return null;
        }

        List<string> unrestored = [];
        foreach ( ScriptRecord record in database.AllRecords )
        {
            if ( record.Language is not (ScriptLanguage.Gsc or ScriptLanguage.Csc) )
            {
                continue;
            }

            ImmutableArray<Diagnostic>? diagnostics = null;
            if ( stored.Entries.TryGetValue(record.Path, out SweptEntry? entry) && entry.ContentHash == record.ContentHash )
            {
                diagnostics = RecordSerializer.DeserializeDiagnostics(entry.Diagnostics, names);
            }

            if ( diagnostics is ImmutableArray<Diagnostic> set
                && database.SetDiagnostics(record.Path, record.Language, record.ContentHash, set) )
            {
                restored++;
            }
            else
            {
                unrestored.Add(record.Path);
            }
        }

        return unrestored;
    }
}
