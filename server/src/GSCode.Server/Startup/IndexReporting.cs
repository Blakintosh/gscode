using GSCode.Core;
using GSCode.Server.Transport;
using GSCode.Workspace.Database;
using GSCode.Workspace.Indexing;
using Serilog;

namespace GSCode.Server.Startup;

/// <summary>
/// Startup-time logging helpers with no state of their own: the index-contents breakdown, the
/// memory report, and the server's own version string. Extracted from <c>Program.cs</c>'s
/// top-level statements so they can be read (and, for <see cref="ServerVersion"/>, called) without
/// pulling in everything else the startup sequence does.
/// </summary>
internal static class IndexReporting
{
    // Logs a formatted breakdown of what the index holds: per-language file counts with a
    // raw/mod/workspace split, plus total declared functions, classes, macros, and distinct
    // namespaces.
    internal static void LogIndexBreakdown(ScriptDatabase database)
    {
        int gscRaw = 0;
        int gscMod = 0;
        int gscWorkspace = 0;
        int cscRaw = 0;
        int cscMod = 0;
        int cscWorkspace = 0;
        int functions = 0;
        int classes = 0;
        int macros = 0;
        HashSet<string> namespaces = new(StringComparer.Ordinal);

        foreach ( ScriptRecord record in database.Gsc.AllRecords )
        {
            CategorizeContext(record.ContextId, ref gscRaw, ref gscMod, ref gscWorkspace);
            functions += record.Functions.Length;
            classes += record.Classes.Length;
            macros += record.Macros.Length;
            foreach ( string declared in record.DeclaredNamespaces )
            {
                namespaces.Add(declared);
            }
        }

        foreach ( ScriptRecord record in database.Csc.AllRecords )
        {
            CategorizeContext(record.ContextId, ref cscRaw, ref cscMod, ref cscWorkspace);
            functions += record.Functions.Length;
            classes += record.Classes.Length;
            macros += record.Macros.Length;
            foreach ( string declared in record.DeclaredNamespaces )
            {
                namespaces.Add(declared);
            }
        }

        int gshFiles = 0;
        foreach ( ScriptRecord record in database.AllGshRecords )
        {
            gshFiles++;
            macros += record.Macros.Length;
        }

        System.Text.StringBuilder report = new();
        report.Append("Index contents:");
        report.Append('\n').Append(FormatLanguageLine("GSC", gscRaw, gscMod, gscWorkspace));
        report.Append('\n').Append(FormatLanguageLine("CSC", cscRaw, cscMod, cscWorkspace));
        report.Append('\n').Append($"    GSH  {gshFiles,6:N0} files");
        report.Append('\n').Append("    ─────────────────────────────────────────────");
        report.Append('\n').Append(
            $"    {functions,6:N0} functions · {classes:N0} classes · {macros:N0} macros · {namespaces.Count:N0} namespaces");

        Log.Verbose("{IndexReport}", report.ToString());
    }

    // Tallies one record's context into the raw / mod / workspace buckets for its language.
    private static void CategorizeContext(string contextId, ref int raw, ref int mod, ref int workspace)
    {
        if ( contextId == "raw" )
        {
            raw++;
        }
        else if ( contextId.StartsWith("mod:", StringComparison.Ordinal) )
        {
            mod++;
        }
        else
        {
            workspace++;
        }
    }

    // Renders one aligned "GSC  1,234 files  (1,000 raw · 200 mod · 34 workspace)" line, omitting
    // any bucket that is empty.
    private static string FormatLanguageLine(string label, int raw, int mod, int workspace)
    {
        int total = raw + mod + workspace;

        List<string> parts = [];
        if ( raw > 0 )
        {
            parts.Add($"{raw:N0} raw");
        }

        if ( mod > 0 )
        {
            parts.Add($"{mod:N0} mod");
        }

        if ( workspace > 0 )
        {
            parts.Add($"{workspace:N0} workspace");
        }

        string split = parts.Count > 0 ? "  (" + string.Join(" · ", parts) + ")" : "";
        return $"    {label}  {total,6:N0} files{split}";
    }

    // The detailed memory breakdown, at Verbose. Called twice a session, after indexing and after
    // compaction.
    //
    // The point is the gap between the managed heap and the working set. Cold indexing allocates
    // heavily per file (source text, token arrays, AST, extraction builders) at ProcessorCount - 1 way
    // parallelism, all of it garbage once the record is built; a warm restore just deserializes
    // records. If the LIVE numbers match across a cold and a warm start while the working set differs,
    // the extra footprint is grown, uncompacted heap rather than retained data — which is what
    // StartupIndexRunner.Compact() exists to give back.
    //
    // Gated by the LOG LEVEL rather than an environment variable. A setting the user can change from
    // the settings UI beats one that needs an env var and a restart — and GSCODE_INSTRUMENTATION was
    // doubly confusing, since PerfTracker already uses that name as a COMPILE-TIME symbol for
    // something else entirely.
    internal static void LogMemoryReport(string phase, IndexOutcome outcome)
    {
        GCMemoryInfo info = GC.GetGCMemoryInfo();

        double workingSet = Environment.WorkingSet / (1024.0 * 1024.0);
        double managedLive = GC.GetTotalMemory(forceFullCollection: false) / (1024.0 * 1024.0);
        double heapSize = info.HeapSizeBytes / (1024.0 * 1024.0);
        double committed = info.TotalCommittedBytes / (1024.0 * 1024.0);
        double fragmented = info.FragmentedBytes / (1024.0 * 1024.0);

        System.Text.StringBuilder report = new();
        report.AppendLine($"Memory after {phase}:");
        report.AppendLine($"    files           {outcome.Total,8:N0}  ({outcome.Restored:N0} restored · {outcome.Analysed:N0} analysed)");
        report.AppendLine($"    working set     {workingSet,8:F1} MB   (what the OS reports)");
        report.AppendLine($"    managed live    {managedLive,8:F1} MB   (retained objects)");
        report.AppendLine($"    heap size       {heapSize,8:F1} MB");
        report.AppendLine($"    committed       {committed,8:F1} MB");
        report.AppendLine($"    fragmented      {fragmented,8:F1} MB");

        // WHERE the fragmentation is, which the total cannot say. The collector treats the two very
        // differently: gen2 holes are compacted by an ordinary blocking collection, LOH holes not at
        // all unless LargeObjectHeapCompactionMode asks for it. Knowing which one holds the bulk
        // decides whether the fix is fewer big arrays or fewer long-lived small ones.
        AppendGenerations(report, info);

        report.Append($"    collections     gen0 {GC.CollectionCount(0):N0} · gen1 {GC.CollectionCount(1):N0} · gen2 {GC.CollectionCount(2):N0}");

        Log.Verbose("{MemoryReport}", report.ToString());
    }

    // Per-generation size and fragmentation, as of each generation's last collection.
    //
    // The runtime reports five entries in a fixed order — gen0, gen1, gen2, the large-object heap and
    // the pinned-object heap — but the count is not contractually five, so the names are indexed
    // defensively rather than assumed.
    private static void AppendGenerations(System.Text.StringBuilder report, GCMemoryInfo info)
    {
        string[] names = ["gen0", "gen1", "gen2", "LOH", "POH"];

        ReadOnlySpan<GCGenerationInfo> generations = info.GenerationInfo;
        for ( int index = 0; index < generations.Length; index++ )
        {
            string name = index < names.Length ? names[index] : $"gen{index}";
            double size = generations[index].SizeAfterBytes / (1024.0 * 1024.0);
            double holes = generations[index].FragmentationAfterBytes / (1024.0 * 1024.0);

            report.AppendLine($"      {name,-4}          {holes,8:F1} MB free of {size,8:F1} MB");
        }
    }

    internal static IEnumerable<string> BundledDataFilePaths()
    {
        string apiDirectory = Path.Combine(AppContext.BaseDirectory, "Api");
        foreach ( string fileName in GameProfile.Active.BundledDataFileNames )
        {
            yield return Path.Combine(apiDirectory, fileName);
        }
    }

    // The server's version, as the build stamped it.
    //
    // Read from the assembly rather than written in the log string, so it cannot drift from what
    // actually shipped. The single source is <Version> in Directory.Build.props, which must match
    // client/package.json since the two ship as one extension.
    internal static string ServerVersion()
    {
        System.Reflection.Assembly assembly = typeof(TransportOptions).Assembly;

        // The informational version carries any suffix; the plain AssemblyVersion drops it, so prefer
        // it and fall back only if it is absent.
        string? informational = System.Reflection.CustomAttributeExtensions
            .GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>(assembly)
            ?.InformationalVersion;

        string version = informational ?? assembly.GetName().Version?.ToString() ?? "unknown";

        // Since .NET 8 the SDK appends "+<full git sha>" to the informational version whenever it builds
        // inside a repository, so this read "2.0.0+95362d3b2dbd71dbb3cf..." - forty hex characters in
        // every startup line. The commit is worth keeping for triage (it identifies the exact build a
        // user is reporting against), so it is shortened to the usual seven rather than switched off.
        int plus = version.IndexOf('+', StringComparison.Ordinal);
        if ( plus >= 0 )
        {
            string revision = version[(plus + 1)..];
            version = revision.Length > 7
                ? string.Concat(version.AsSpan(0, plus + 1), revision.AsSpan(0, 7))
                : version;
        }

        return version;
    }
}
