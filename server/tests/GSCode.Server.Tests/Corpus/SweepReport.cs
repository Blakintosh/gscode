using System.Net;
using System.Text;
using GSCode.Core.Diagnostics;

namespace GSCode.Server.Tests.Corpus;

/// <summary>
/// Writes the corpus sweep as a single self-contained HTML file.
///
/// The sweep's value is in reading it — deciding, finding by finding, whether a diagnostic is a
/// real defect in the shipped scripts or a false positive in ours. Test output is a poor place to
/// do that with a couple of thousand findings: no grouping you can collapse, no source context,
/// and no way to filter. This is the same data with the file's own line beside each one.
///
/// Everything is inlined so the file opens from disk with no server and no network. It is written
/// outside the repository by default, since it is a snapshot of someone's local mod-tools install
/// rather than a build artifact.
/// </summary>
internal static class SweepReport
{
    internal readonly record struct Item(
        GscDiagnosticCode Code, DiagnosticSeverity Severity, string Message, string Path, int Line, int Character);

    public static void Write(string outputPath, IReadOnlyList<Item> items, string corpusRoot)
    {
        StringBuilder html = new();

        html.Append("<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\">");
        html.Append("<meta name=\"viewport\" content=\"width=device-width,initial-scale=1\">");
        html.Append("<title>GSCode corpus diagnostic sweep</title>");
        AppendStyle(html);
        html.Append("</head><body>");

        AppendHeader(html, items, corpusRoot);
        AppendSummary(html, items);
        AppendGroups(html, items, corpusRoot);
        AppendScript(html);

        html.Append("</body></html>");

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath))!);

        // UTF8Encoding(false), not Encoding.UTF8: the latter writes a BOM, which would sit in
        // front of the doctype. The <meta charset> already declares the encoding.
        File.WriteAllText(outputPath, html.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    private static void AppendHeader(StringBuilder html, IReadOnlyList<Item> items, string corpusRoot)
    {
        int files = items.Select(i => i.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count();
        int errors = items.Count(i => i.Severity == DiagnosticSeverity.Error);
        int warnings = items.Count(i => i.Severity == DiagnosticSeverity.Warning);

        html.Append("<h1>Corpus diagnostic sweep</h1>");

        // The game is whichever GSCODE_CORPUS_* root this run was pointed at, so the lede says
        // "these scripts" rather than naming one. It used to say BO3 on every report, including
        // the CoD4 and WaW ones - and a report that names the wrong game is exactly how a
        // BO3-measured conclusion got applied to four other games once already.
        html.Append("<p class=\"lede\">Every diagnostic the editor would raise over these shipped scripts. ")
            .Append("They shipped in a released game, so each finding here is either a real defect in ")
            .Append("them or — far more often — a false positive in GSCode.</p>");

        html.Append("<div class=\"stats\">");
        AppendStat(html, items.Count.ToString("N0"), "diagnostics");
        AppendStat(html, files.ToString("N0"), "files");
        AppendStat(html, errors.ToString("N0"), "errors", errors > 0 ? "error" : "");
        AppendStat(html, warnings.ToString("N0"), "warnings", warnings > 0 ? "warning" : "");
        html.Append("</div>");

        html.Append("<p class=\"meta\">Corpus: <code>").Append(Escape(corpusRoot)).Append("</code><br>")
            .Append("Generated ").Append(Escape(DateTime.Now.ToString("yyyy-MM-dd HH:mm"))).Append("</p>");
    }

    private static void AppendStat(StringBuilder html, string value, string label, string severityClass = "")
    {
        html.Append("<div class=\"stat ").Append(severityClass).Append("\"><b>")
            .Append(Escape(value)).Append("</b><span>").Append(Escape(label)).Append("</span></div>");
    }

    private static void AppendSummary(StringBuilder html, IReadOnlyList<Item> items)
    {
        html.Append("<h2>By diagnostic</h2>");
        html.Append("<table class=\"summary\"><thead><tr>")
            .Append("<th>Code</th><th>Name</th><th>Severity</th><th class=\"num\">Count</th><th class=\"num\">Files</th>")
            .Append("</tr></thead><tbody>");

        foreach ( IGrouping<GscDiagnosticCode, Item> group in Ordered(items) )
        {
            int files = group.Select(i => i.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count();
            DiagnosticSeverity severity = group.First().Severity;

            html.Append("<tr><td><a href=\"#code-").Append((int)group.Key).Append("\">")
                .Append((int)group.Key).Append("</a></td>")
                .Append("<td>").Append(Escape(group.Key.ToString())).Append("</td>")
                .Append("<td><span class=\"sev ").Append(SeverityClass(severity)).Append("\">")
                .Append(Escape(severity.ToString())).Append("</span></td>")
                .Append("<td class=\"num\">").Append(group.Count().ToString("N0")).Append("</td>")
                .Append("<td class=\"num\">").Append(files.ToString("N0")).Append("</td></tr>");
        }

        html.Append("</tbody></table>");
    }

    private static void AppendGroups(StringBuilder html, IReadOnlyList<Item> items, string corpusRoot)
    {
        // One read per file, however many findings it has.
        Dictionary<string, string[]> lines = new(StringComparer.OrdinalIgnoreCase);

        foreach ( IGrouping<GscDiagnosticCode, Item> group in Ordered(items) )
        {
            DiagnosticSeverity severity = group.First().Severity;

            html.Append("<section id=\"code-").Append((int)group.Key).Append("\">");
            html.Append("<h2>").Append((int)group.Key).Append(' ').Append(Escape(group.Key.ToString()))
                .Append(" <span class=\"sev ").Append(SeverityClass(severity)).Append("\">")
                .Append(Escape(severity.ToString())).Append("</span>")
                .Append(" <span class=\"count\">").Append(group.Count().ToString("N0")).Append("</span></h2>");

            // Identical messages collapse together: 39 copies of the same read-only field write
            // is one fact, not 39, and the per-message count is what says how bad it is.
            foreach ( IGrouping<string, Item> byMessage in group
                .GroupBy(i => i.Message)
                .OrderByDescending(g => g.Count()) )
            {
                html.Append("<details><summary><span class=\"n\">").Append(byMessage.Count())
                    .Append("&times;</span> ").Append(Escape(byMessage.Key)).Append("</summary><ol class=\"sites\">");

                foreach ( Item item in byMessage.OrderBy(i => i.Path, StringComparer.OrdinalIgnoreCase).ThenBy(i => i.Line) )
                {
                    html.Append("<li><code class=\"loc\">").Append(Escape(Relative(item.Path, corpusRoot)))
                        .Append(':').Append(item.Line + 1).Append("</code>");

                    string? source = SourceLine(lines, item);
                    if ( source is not null )
                    {
                        html.Append("<pre>").Append(Escape(source)).Append("</pre>");
                    }

                    html.Append("</li>");
                }

                html.Append("</ol></details>");
            }

            html.Append("</section>");
        }
    }

    private static string? SourceLine(Dictionary<string, string[]> cache, Item item)
    {
        if ( !cache.TryGetValue(item.Path, out string[]? fileLines) )
        {
            try
            {
                fileLines = File.ReadAllLines(item.Path);
            }
            catch ( IOException )
            {
                fileLines = [];
            }

            cache[item.Path] = fileLines;
        }

        if ( item.Line < 0 || item.Line >= fileLines.Length )
        {
            return null;
        }

        // Trimmed: leading tabs in these files are deep, and the point is the statement.
        return fileLines[item.Line].Trim();
    }

    private static IEnumerable<IGrouping<GscDiagnosticCode, Item>> Ordered(IReadOnlyList<Item> items)
    {
        // Severity first, then volume: an Error on shipped code matters more than 2,000 hints.
        return items
            .GroupBy(i => i.Code)
            .OrderBy(g => SeverityRank(g.First().Severity))
            .ThenByDescending(g => g.Count());
    }

    private static int SeverityRank(DiagnosticSeverity severity)
    {
        switch ( severity )
        {
            case DiagnosticSeverity.Error:
                return 0;
            case DiagnosticSeverity.Warning:
                return 1;
            case DiagnosticSeverity.Information:
                return 2;
            default:
                return 3;
        }
    }

    private static string SeverityClass(DiagnosticSeverity severity)
    {
        return severity.ToString().ToLowerInvariant();
    }

    private static string Relative(string path, string corpusRoot)
    {
        return path.StartsWith(corpusRoot, StringComparison.OrdinalIgnoreCase)
            ? path[corpusRoot.Length..].TrimStart('\\', '/')
            : path;
    }

    private static string Escape(string text)
    {
        return WebUtility.HtmlEncode(text);
    }

    private static void AppendStyle(StringBuilder html)
    {
        html.Append("<style>");
        html.Append(":root{--bg:#fff;--fg:#1a1a1a;--muted:#666;--line:#e3e3e3;--code:#f6f6f6;");
        html.Append("--error:#c8102e;--warning:#b26a00;--hint:#4a6fa5;--information:#4a6fa5}");
        html.Append("@media(prefers-color-scheme:dark){:root{--bg:#16181c;--fg:#e6e6e6;--muted:#9aa0a6;");
        html.Append("--line:#2c2f36;--code:#1e2127;--error:#ff6b7f;--warning:#e0a458;--hint:#8fb3e0;--information:#8fb3e0}}");
        html.Append("*{box-sizing:border-box}");
        html.Append("body{margin:0 auto;padding:2rem 1.25rem 6rem;max-width:60rem;background:var(--bg);color:var(--fg);");
        html.Append("font:15px/1.55 ui-sans-serif,system-ui,-apple-system,Segoe UI,Roboto,sans-serif}");
        html.Append("h1{font-size:1.6rem;margin:0 0 .5rem}");
        html.Append("h2{font-size:1.1rem;margin:2.5rem 0 .75rem;padding-bottom:.35rem;border-bottom:1px solid var(--line)}");
        html.Append(".lede{color:var(--muted);max-width:46rem}");
        html.Append(".meta{color:var(--muted);font-size:.85rem}");
        html.Append(".stats{display:flex;flex-wrap:wrap;gap:.75rem;margin:1.25rem 0}");
        html.Append(".stat{border:1px solid var(--line);border-radius:.5rem;padding:.6rem .9rem;min-width:7rem}");
        html.Append(".stat b{display:block;font-size:1.35rem;line-height:1.1}");
        html.Append(".stat span{color:var(--muted);font-size:.8rem}");
        html.Append(".stat.error b{color:var(--error)}.stat.warning b{color:var(--warning)}");
        html.Append("table{border-collapse:collapse;width:100%;font-size:.9rem}");
        html.Append("th,td{text-align:left;padding:.45rem .6rem;border-bottom:1px solid var(--line)}");
        html.Append("th{color:var(--muted);font-weight:600;font-size:.8rem;text-transform:uppercase;letter-spacing:.04em}");
        html.Append(".num{text-align:right;font-variant-numeric:tabular-nums}");
        html.Append("a{color:inherit}");
        html.Append(".sev{font-size:.72rem;text-transform:uppercase;letter-spacing:.05em;font-weight:700}");
        html.Append(".sev.error{color:var(--error)}.sev.warning{color:var(--warning)}");
        html.Append(".sev.hint,.sev.information{color:var(--hint)}");
        html.Append(".count{color:var(--muted);font-weight:400;font-size:.85rem}");
        html.Append("details{border:1px solid var(--line);border-radius:.4rem;margin:.4rem 0;background:var(--code)}");
        html.Append("summary{cursor:pointer;padding:.5rem .7rem;font-size:.9rem}");
        html.Append("summary .n{display:inline-block;min-width:3rem;color:var(--muted);font-variant-numeric:tabular-nums}");
        html.Append(".sites{margin:0;padding:.25rem .7rem .7rem 2.5rem;max-height:32rem;overflow:auto}");
        html.Append(".sites li{margin:.35rem 0}");
        html.Append(".loc{font-size:.8rem;color:var(--muted)}");
        html.Append("pre{margin:.15rem 0 0;padding:.35rem .5rem;background:var(--bg);border:1px solid var(--line);");
        html.Append("border-radius:.3rem;overflow-x:auto;font-size:.82rem}");
        html.Append("code{font-family:ui-monospace,SFMono-Regular,Consolas,monospace}");
        html.Append("#filter{width:100%;padding:.5rem .7rem;margin:1rem 0;border:1px solid var(--line);");
        html.Append("border-radius:.4rem;background:var(--code);color:var(--fg);font:inherit;font-size:.9rem}");
        html.Append("</style>");
    }

    private static void AppendScript(StringBuilder html)
    {
        // A filter box, injected above the first section: with a couple of thousand findings the
        // useful question is usually "show me everything mentioning damageTaken".
        html.Append("<script>");
        html.Append("(function(){");
        html.Append("var box=document.createElement('input');");
        html.Append("box.id='filter';box.type='search';box.placeholder='Filter by message, file or code…';");
        html.Append("var first=document.querySelector('section');");
        html.Append("if(!first)return;first.parentNode.insertBefore(box,first);");
        html.Append("box.addEventListener('input',function(){");
        html.Append("var q=box.value.toLowerCase();");
        html.Append("document.querySelectorAll('section').forEach(function(s){");
        html.Append("var any=false;");
        html.Append("s.querySelectorAll('details').forEach(function(d){");
        html.Append("var hit=!q||d.textContent.toLowerCase().indexOf(q)>=0;");
        html.Append("d.style.display=hit?'':'none';if(hit)any=true;");
        html.Append("if(q&&hit)d.open=true;else if(!q)d.open=false;});");
        html.Append("s.style.display=any?'':'none';});");
        html.Append("});})();");
        html.Append("</script>");
    }
}
