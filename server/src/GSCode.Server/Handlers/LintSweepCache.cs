using System.Buffers.Binary;
using System.Collections.Immutable;
using System.IO.Hashing;
using System.Text;
using GSCode.Core;
using GSCode.Core.Symbols;
using GSCode.Workspace.Cache;
using GSCode.Workspace.Database;
using GSCode.Workspace.Resolution;
using Diagnostic = GSCode.Core.Diagnostics.Diagnostic;

namespace GSCode.Server.Handlers;

/// <summary>What <see cref="LintSweepCache.Capture"/> took: the workspace's fingerprint and every file's set.</summary>
public sealed record SweepCapture(string Fingerprint, List<SweptEntry> Entries);

/// <summary>
/// The full-mode lint sweep's results kept in the workspace cache, so a start whose workspace has not
/// changed restores them instead of sweeping again (PERF.md has the cost).
///
/// All or nothing, because a cross-file result depends on every file it can see: the results are used
/// only when the fingerprint of the whole workspace matches the one they were computed against.
/// Besides the records, the lints read whether a directive's or path call's target EXISTS, and a
/// target can exist without being a record, so the resolver's answers are in the fingerprint too. The
/// server build, bundled data and game are in the cache's identity; the lints read no settings.
/// </summary>
public static class LintSweepCache
{
    /// <summary>
    /// The workspace as the lints see it: every record's path, context, language and content hash,
    /// and what each of its directives and path calls resolves to now.
    /// </summary>
    public static string Fingerprint(ScriptDatabase database, PathResolver resolver)
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
            AppendResolutions(hash, record, resolver, separator);
        }

        return $"{hash.GetCurrentHashAsUInt64():x16}:{records.Count}";
    }

    /// <summary>
    /// What each <c>#using</c>/<c>#include</c> and path call of a script resolves to, asked as the
    /// lints ask it — <c>UsingNotFoundLint</c>, <c>FileImports</c>, <c>FunctionResolutionLint</c>.
    /// </summary>
    private static void AppendResolutions(XxHash64 hash, ScriptRecord record, PathResolver resolver, byte[] separator)
    {
        if ( record.Language is not (ScriptLanguage.Gsc or ScriptLanguage.Csc) )
        {
            return;
        }

        string extension = GameProfile.Active.ExtensionFor(record.Language);
        ResolutionContext context = resolver.GetContext(record.Path);
        foreach ( DependencyEdge edge in record.Dependencies )
        {
            if ( !edge.IsInsert )
            {
                hash.Append(Encoding.UTF8.GetBytes(resolver.Resolve(context, edge.RawPath + extension) ?? ""));
                hash.Append(separator);
            }
        }

        foreach ( GSCode.Parser.Extraction.PathCallReference pathCall in record.PathCallTargets )
        {
            hash.Append(Encoding.UTF8.GetBytes(resolver.Resolve(context, pathCall.Path + extension) ?? ""));
            hash.Append(separator);
        }
    }

    /// <summary>
    /// Every GSC and CSC file's stored diagnostics and the fingerprint they go with, or null when a
    /// record changed while they were being read — there is then no one workspace they describe.
    /// A record with unsaved editor state is left out: the next start reads that file from disk.
    /// </summary>
    public static SweepCapture? Capture(ScriptDatabase database, PathResolver resolver)
    {
        string before = Fingerprint(database, resolver);

        List<SweptEntry> entries = [];
        foreach ( ScriptRecord record in database.AllRecords )
        {
            if ( record.Language is not (ScriptLanguage.Gsc or ScriptLanguage.Csc) || record.IsDirty )
            {
                continue;
            }

            entries.Add(new SweptEntry(record.Path, record.ContentHash, RecordSerializer.SerializeDiagnostics(record.Diagnostics)));
        }

        return Fingerprint(database, resolver) == before ? new SweepCapture(before, entries) : null;
    }

    /// <summary>
    /// Puts the stored diagnostics back on every record they still describe, and returns the paths of
    /// the GSC and CSC files they do not — a file that was open and unsaved when they were stored, or
    /// one that changed underneath — which the caller lints. Null, and nothing touched, when the
    /// workspace is not the one they were computed against.
    /// </summary>
    public static List<string>? TryRestore(
        ScriptDatabase database, PathResolver resolver, StoredSweep stored, NameTable names, out int restored)
    {
        restored = 0;
        if ( stored.Fingerprint != Fingerprint(database, resolver) )
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
