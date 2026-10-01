using System.Globalization;
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
/// Hints are most of every sweep, and the findings that decide whether a rule ships are the errors
/// and warnings, so each severity can be switched off, and the filter says how many findings are
/// left in view.
/// </summary>
internal static class SweepReport
{
    internal readonly record struct Item(
        GscDiagnosticCode Code, DiagnosticSeverity Severity, string Message, string Path, int Line, int Character);

    private static readonly DiagnosticSeverity[] s_severities =
    [
        DiagnosticSeverity.Error,
        DiagnosticSeverity.Warning,
        DiagnosticSeverity.Information,
        DiagnosticSeverity.Hint,
    ];

    /// <summary>
    /// <paramref name="game"/> is the profile the sweep ran under, passed rather than inferred. The
    /// page used to name no game at all, after an earlier version named BO3 on every report — and a
    /// report that names the wrong game is how a BO3-measured conclusion got applied to four other
    /// games once already.
    /// </summary>
    public static void Write(string outputPath, string game, IReadOnlyList<Item> items, string corpusRoot)
    {
        string directory = Path.GetDirectoryName(Path.GetFullPath(outputPath))!;
        StringBuilder html = new();

        ReportPage.Head(html, $"GSCode diagnostics - {game}");
        ReportPage.GameNav(html, directory, game, "diagnostics");

        AppendHeader(html, game, items, corpusRoot);
        AppendSummary(html, items);
        AppendToolbar(html);
        AppendGroups(html, items, corpusRoot);

        ReportPage.Foot(html);
        AppendScript(html);

        ReportPage.Save(outputPath, html);
        PerfReport.WriteAggregate(directory);
    }

    private static void AppendHeader(StringBuilder html, string game, IReadOnlyList<Item> items, string corpusRoot)
    {
        int files = items.Select(static i => i.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count();

        html.AppendLine($"<h1>Corpus diagnostic sweep - {ReportPage.Escape(game)}</h1>");
        html.AppendLine("<div class=\"sub\">Every diagnostic the editor would raise over these shipped "
            + "scripts. They shipped in a released game, so each finding here is either a real defect in "
            + "them or — far more often — a false positive in GSCode. From "
            + $"<code>{ReportPage.Escape(corpusRoot)}</code>, run at "
            + $"{ReportPage.Escape(DateTime.Now.ToString("s", CultureInfo.InvariantCulture))}.</div>");

        html.AppendLine("<div class=\"stats\">");
        ReportPage.Stat(html, "diagnostics", items.Count.ToString("N0", CultureInfo.InvariantCulture));
        ReportPage.Stat(html, "files", files.ToString("N0", CultureInfo.InvariantCulture));

        foreach ( DiagnosticSeverity severity in s_severities )
        {
            int count = items.Count(i => i.Severity == severity);
            bool loud = severity == DiagnosticSeverity.Error || severity == DiagnosticSeverity.Warning;
            string cssClass = count > 0 && loud ? SeverityClass(severity) : "";
            ReportPage.Stat(html, Plural(severity), count.ToString("N0", CultureInfo.InvariantCulture), cssClass);
        }

        html.AppendLine("</div>");
    }

    private static void AppendSummary(StringBuilder html, IReadOnlyList<Item> items)
    {
        html.AppendLine("<h2>By diagnostic</h2>");
        ReportPage.TableStart(html, null, "code", "name", "severity", "count", "files");

        foreach ( IGrouping<GscDiagnosticCode, Item> group in Ordered(items) )
        {
            int files = group.Select(static i => i.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count();
            string severity = SeverityClass(group.First().Severity);

            html.AppendLine($"<tr data-sev=\"{severity}\"><td><a href=\"#code-{(int)group.Key}\">{(int)group.Key}</a></td>"
                + $"<td>{ReportPage.Escape(group.Key.ToString())}</td>"
                + $"<td><span class=\"sev {severity}\">{severity}</span></td>"
                + $"<td class=\"n\">{group.Count()}</td>"
                + $"<td class=\"n\">{files}</td></tr>");
        }

        ReportPage.TableEnd(html);
    }

    /// <summary>The filter box, one switch per severity, and the count of what is left in view.</summary>
    private static void AppendToolbar(StringBuilder html)
    {
        html.AppendLine("<h2>Findings</h2>");
        html.Append("<div class=\"toolbar\"><input id=\"filter\" class=\"q\" type=\"search\" "
            + "placeholder=\"filter by message, file or source line...\">");

        foreach ( DiagnosticSeverity severity in s_severities )
        {
            string name = SeverityClass(severity);
            html.Append($"<label><input type=\"checkbox\" data-sev=\"{name}\" checked> "
                + $"<span class=\"sev {name}\">{name}</span></label>");
        }

        html.AppendLine("<span id=\"shown\" class=\"count\"></span></div>");
    }

    private static void AppendGroups(StringBuilder html, IReadOnlyList<Item> items, string corpusRoot)
    {
        // One read per file, however many findings it has.
        Dictionary<string, string[]> lines = new(StringComparer.OrdinalIgnoreCase);

        foreach ( IGrouping<GscDiagnosticCode, Item> group in Ordered(items) )
        {
            string severity = SeverityClass(group.First().Severity);

            html.Append($"<section id=\"code-{(int)group.Key}\" data-sev=\"{severity}\">");
            html.Append($"<h3>{(int)group.Key} {ReportPage.Escape(group.Key.ToString())} "
                + $"<span class=\"sev {severity}\">{severity}</span> "
                + $"<span class=\"count\">{group.Count():N0}</span></h3>");

            // Identical messages collapse together: 39 copies of the same read-only field write
            // is one fact, not 39, and the per-message count is what says how bad it is.
            foreach ( IGrouping<string, Item> byMessage in group
                .GroupBy(static i => i.Message)
                .OrderByDescending(static g => g.Count()) )
            {
                html.Append($"<details data-count=\"{byMessage.Count()}\"><summary><span class=\"times\">"
                    + $"{byMessage.Count()}&times;</span> {ReportPage.Escape(byMessage.Key)}</summary><ol class=\"sites\">");

                foreach ( Item item in byMessage.OrderBy(static i => i.Path, StringComparer.OrdinalIgnoreCase).ThenBy(static i => i.Line) )
                {
                    html.Append($"<li><code class=\"loc\">{ReportPage.Escape(ReportPage.Relative(item.Path, corpusRoot))}"
                        + $":{item.Line + 1}</code>");

                    string? source = SourceLine(lines, item);
                    if ( source is not null )
                    {
                        html.Append($"<pre>{ReportPage.Escape(source)}</pre>");
                    }

                    html.Append("</li>");
                }

                html.Append("</ol></details>");
            }

            html.AppendLine("</section>");
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
            .GroupBy(static i => i.Code)
            .OrderBy(static g => SeverityRank(g.First().Severity))
            .ThenByDescending(static g => g.Count());
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

    private static string Plural(DiagnosticSeverity severity)
    {
        switch ( severity )
        {
            case DiagnosticSeverity.Error:
                return "errors";
            case DiagnosticSeverity.Warning:
                return "warnings";
            case DiagnosticSeverity.Information:
                return "information";
            default:
                return "hints";
        }
    }

    /// <summary>
    /// The filter and the severity switches, applied together. Each message group's text is lowered
    /// once up front rather than on every keystroke, since a sweep can hold well over a thousand.
    /// </summary>
    private static void AppendScript(StringBuilder html)
    {
        html.AppendLine("""
            <script>
            (function(){
              var box=document.getElementById('filter'),shown=document.getElementById('shown');
              var switches=Array.prototype.slice.call(document.querySelectorAll('.toolbar input[data-sev]'));
              var sections=Array.prototype.slice.call(document.querySelectorAll('section[data-sev]'));
              var groups=sections.map(function(s){
                return Array.prototype.map.call(s.querySelectorAll('details'),function(d){
                  return {el:d,text:d.textContent.toLowerCase(),count:parseInt(d.getAttribute('data-count'),10)};
                });
              });
              function apply(){
                var q=box.value.toLowerCase(),on={},total=0;
                switches.forEach(function(c){on[c.getAttribute('data-sev')]=c.checked;});
                Array.prototype.forEach.call(document.querySelectorAll('tr[data-sev]'),function(r){
                  r.style.display=on[r.getAttribute('data-sev')]?'':'none';
                });
                sections.forEach(function(s,i){
                  var any=false,enabled=on[s.getAttribute('data-sev')];
                  groups[i].forEach(function(g){
                    var hit=enabled&&(!q||g.text.indexOf(q)>=0);
                    g.el.style.display=hit?'':'none';
                    if(hit){any=true;total+=g.count;}
                    if(q&&hit)g.el.open=true;else if(!q)g.el.open=false;
                  });
                  s.style.display=any?'':'none';
                });
                shown.textContent=total.toLocaleString()+' findings shown';
              }
              box.addEventListener('input',apply);
              switches.forEach(function(c){c.addEventListener('change',apply);});
              apply();
            })();
            </script>
            """);
    }
}
