using System.Globalization;
using System.Text;

namespace GSCode.Server.Tests.Corpus;

/// <summary>
/// The per-file timing sweep as a standalone HTML page, written beside the diagnostic sweep but
/// never by it: a perf run costs a second pass over every script, so it is opted into rather than
/// carried along.
///
/// Two tables, because they answer different questions. Slowest ABSOLUTE says where the wall-clock
/// went. Slowest PER KILOBYTE says where an algorithm is superlinear — a long file taking long is
/// arithmetic, a short file taking long is a bug.
/// </summary>
internal static class PerfReport
{
    internal sealed record Item(string Path, double Milliseconds, long Bytes)
    {
        public double MillisecondsPerKilobyte
        {
            get { return Bytes == 0 ? 0 : Milliseconds / (Bytes / 1024.0); }
        }
    }

    /// <summary>A snapshot of the pools that actually decide the server's footprint.</summary>
    internal sealed record Memory(long ManagedLive, long HeapSize, long Committed, long Fragmented, long WorkingSet, int Gen0, int Gen1, int Gen2);

    /// <summary>
    /// Taken AFTER a forced collection, so it reports what is retained rather than what happens to
    /// be uncollected. Fragmented is the gap between the heap and what is live in it, which on an
    /// indexing run is mostly large-object heap and is the number that has misled before: a large
    /// working set with a small live graph is holes, not a leak.
    /// </summary>
    public static Memory Sample()
    {
        GC.Collect(2, GCCollectionMode.Forced, blocking: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(2, GCCollectionMode.Forced, blocking: true);

        GCMemoryInfo info = GC.GetGCMemoryInfo();
        long live = GC.GetTotalMemory(forceFullCollection: false);

        return new Memory(
            ManagedLive: live,
            HeapSize: info.HeapSizeBytes,
            Committed: info.TotalCommittedBytes,
            Fragmented: info.FragmentedBytes,
            WorkingSet: Environment.WorkingSet,
            Gen0: GC.CollectionCount(0),
            Gen1: GC.CollectionCount(1),
            Gen2: GC.CollectionCount(2));
    }

    public static void Write(
        string outputPath, string game, IReadOnlyList<Item> items, string corpusRoot,
        IReadOnlyDictionary<string, int> worldCounts, Memory memory, int cachedHeaders)
    {
        List<double> sorted = [.. items.Select(static i => i.Milliseconds).Order()];
        double total = sorted.Sum();
        int tailCount = (items.Count / 100) + 1;
        double tail = items.OrderByDescending(static i => i.Milliseconds).Take(tailCount).Sum(static i => i.Milliseconds);

        StringBuilder html = new();
        html.AppendLine("<!doctype html><meta charset=\"utf-8\">");
        html.AppendLine($"<title>GSCode perf sweep - {Escape(game)}</title>");
        html.AppendLine("<style>");
        html.AppendLine("body{font:14px/1.5 system-ui,sans-serif;margin:2rem;max-width:70rem;color:#1a1a1a}");
        html.AppendLine("h1{font-size:1.4rem;margin-bottom:.2rem}h2{font-size:1.05rem;margin-top:2rem}");
        html.AppendLine(".sub{color:#666;margin-bottom:1.5rem}");
        html.AppendLine("table{border-collapse:collapse;width:100%;margin-top:.5rem}");
        html.AppendLine("th,td{text-align:left;padding:.35rem .6rem;border-bottom:1px solid #e5e5e5}");
        html.AppendLine("th{background:#fafafa;font-weight:600}td.n{text-align:right;font-variant-numeric:tabular-nums}");
        html.AppendLine("code{font:13px ui-monospace,monospace}");
        html.AppendLine(".stats{display:flex;gap:2rem;flex-wrap:wrap;margin:1rem 0;padding:1rem;background:#fafafa;border:1px solid #eee}");
        html.AppendLine(".stat b{display:block;font-size:1.2rem;font-variant-numeric:tabular-nums}");
        html.AppendLine(".stat span{color:#666;font-size:.85rem}");
        html.AppendLine("@media(prefers-color-scheme:dark){body{background:#111;color:#eee}");
        html.AppendLine("th{background:#1c1c1c}th,td{border-color:#2a2a2a}.stats{background:#1a1a1a;border-color:#2a2a2a}");
        html.AppendLine(".sub,.stat span{color:#999}}");
        html.AppendLine("</style>");

        html.AppendLine($"<h1>Analysis timing - {Escape(game)}</h1>");
        html.AppendLine($"<div class=\"sub\">{items.Count} files from <code>{Escape(corpusRoot)}</code>. "
            + "Each file lexed, preprocessed, parsed and extracted once to warm, then timed.</div>");

        html.AppendLine("<div class=\"stats\">");
        Stat(html, "total", $"{total:F0} ms");
        Stat(html, "median", $"{Percentile(sorted, 0.50):F2} ms");
        Stat(html, "p90", $"{Percentile(sorted, 0.90):F2} ms");
        Stat(html, "p99", $"{Percentile(sorted, 0.99):F2} ms");
        Stat(html, "max", $"{(sorted.Count > 0 ? sorted[^1] : 0):F2} ms");
        Stat(html, $"slowest {tailCount}", total > 0 ? $"{tail / total * 100:F1}% of total" : "-");
        html.AppendLine("</div>");

        // Per world, because a .gsc and a .csc are separate universes to the database, and "980
        // files" hides which of the two the time went to.
        html.AppendLine("<h2>Files by world</h2>");
        html.AppendLine("<table><tr><th>world</th><th>files</th></tr>");
        foreach ( KeyValuePair<string, int> world in worldCounts.OrderByDescending(static w => w.Value) )
        {
            html.AppendLine($"<tr><td>{Escape(world.Key)}</td><td class=\"n\">{world.Value}</td></tr>");
        }

        html.AppendLine($"<tr><td>headers held in the insert cache</td><td class=\"n\">{cachedHeaders}</td></tr>");
        html.AppendLine("</table>");

        html.AppendLine("<h2>Memory after the sweep</h2>");
        html.AppendLine("<div class=\"sub\">Sampled after a forced gen2 collection, so this is what is RETAINED.</div>");
        html.AppendLine("<table><tr><th>pool</th><th>MB</th><th>what it means</th></tr>");
        Row(html, "managed live", memory.ManagedLive, "the object graph still reachable");
        Row(html, "heap size", memory.HeapSize, "what the GC has carved out");
        Row(html, "committed", memory.Committed, "backed by real memory");
        Row(html, "fragmented", memory.Fragmented, "holes in the heap, mostly large-object");
        Row(html, "working set", memory.WorkingSet, "what the OS reports for the process");
        html.AppendLine($"<tr><td>collections</td><td class=\"n\">{memory.Gen0}/{memory.Gen1}/{memory.Gen2}</td>"
            + "<td>gen0 / gen1 / gen2</td></tr>");
        html.AppendLine("</table>");

        Table(html, "Slowest by absolute time",
            "Where the wall-clock went.",
            [.. items.OrderByDescending(static i => i.Milliseconds).Take(25)], corpusRoot);

        Table(html, "Slowest per kilobyte",
            "Files over 4 KB, ranked by time per KB. A short file high on this list is where to look "
            + "for superlinear behaviour - a long one is just long.",
            [.. items.Where(static i => i.Bytes > 4096)
                     .OrderByDescending(static i => i.MillisecondsPerKilobyte).Take(25)], corpusRoot);

        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        File.WriteAllText(outputPath, html.ToString());
    }

    private static void Row(StringBuilder html, string label, long bytes, string meaning)
    {
        html.AppendLine($"<tr><td>{Escape(label)}</td><td class=\"n\">{bytes / 1024.0 / 1024.0:F1}</td>"
            + $"<td>{Escape(meaning)}</td></tr>");
    }

    private static void Stat(StringBuilder html, string label, string value)
    {
        html.AppendLine($"<div class=\"stat\"><b>{Escape(value)}</b><span>{Escape(label)}</span></div>");
    }

    private static void Table(StringBuilder html, string title, string blurb, IReadOnlyList<Item> rows, string root)
    {
        html.AppendLine($"<h2>{Escape(title)}</h2>");
        html.AppendLine($"<div class=\"sub\">{Escape(blurb)}</div>");
        html.AppendLine("<table><tr><th>ms</th><th>KB</th><th>ms/KB</th><th>file</th></tr>");

        foreach ( Item row in rows )
        {
            string relative = row.Path.StartsWith(root, StringComparison.OrdinalIgnoreCase)
                ? row.Path[root.Length..].TrimStart('\\', '/')
                : row.Path;

            html.AppendLine(
                $"<tr><td class=\"n\">{row.Milliseconds:F1}</td>"
                + $"<td class=\"n\">{row.Bytes / 1024.0:F0}</td>"
                + $"<td class=\"n\">{row.MillisecondsPerKilobyte:F2}</td>"
                + $"<td><code>{Escape(relative)}</code></td></tr>");
        }

        html.AppendLine("</table>");
    }

    private static double Percentile(List<double> sorted, double fraction)
    {
        if ( sorted.Count == 0 )
        {
            return 0;
        }

        int index = (int)Math.Clamp(Math.Round(fraction * (sorted.Count - 1)), 0, sorted.Count - 1);
        return sorted[index];
    }

    private static string Escape(string text)
    {
        return text.Replace("&", "&amp;", StringComparison.Ordinal)
            .Replace("<", "&lt;", StringComparison.Ordinal)
            .Replace(">", "&gt;", StringComparison.Ordinal);
    }
}
