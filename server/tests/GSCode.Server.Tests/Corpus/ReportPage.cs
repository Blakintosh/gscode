using System.Globalization;
using System.Net;
using System.Text;

namespace GSCode.Server.Tests.Corpus;

/// <summary>One page a game can have, and the label its links carry.</summary>
internal sealed record PageLink(string File, string Label);

/// <summary>
/// What every report page shares: where they are written, what they are called, the stylesheet, the
/// script, and the small table and stat helpers. In one place so the pages read as one set and link
/// to each other by names that cannot drift apart.
///
/// Everything is inlined into each page. They are opened straight off disk and copied around, and a
/// report that needs a sibling CSS file or a network fetch is a report that arrives broken.
/// </summary>
internal static class ReportPage
{
    /// <summary>The hub: every perf sweep's numbers, and a link to every page on disk.</summary>
    public const string AllGamesPage = "gscode-perf-all.html";

    public const string ScalePage = "gscode-scale.md";

    public static string PerfPage(string sidecarName)
    {
        return $"gscode-perf-{sidecarName}.html";
    }

    public static string BudgetPage(string game)
    {
        return $"gscode-lint-budget-{game}.html";
    }

    public static string SweepPage(string game)
    {
        return $"gscode-sweep-{game}.html";
    }

    /// <summary>Every page one game can have, in the order the navigation lists them.</summary>
    public static IReadOnlyList<PageLink> GamePages(string game)
    {
        return
        [
            new PageLink(SweepPage(game), "diagnostics"),
            new PageLink(PerfPage(PerfReport.SidecarName(game, PerfSweep.Analysis)), "analysis"),
            new PageLink(PerfPage(PerfReport.SidecarName(game, PerfSweep.Lints)), "lints"),
            new PageLink(PerfPage(PerfReport.SidecarName(game, PerfSweep.Completion)), "completion"),
            new PageLink(BudgetPage(game), "lint budget"),
        ];
    }

    /// <summary>
    /// Where reports go: the variable when it is set, otherwise the repository's <c>temp/</c> folder,
    /// found by walking up to the <c>.git</c> entry — a directory in a clone and a FILE in a worktree,
    /// so both are checked. Falls back to the system temp folder when there is no repository above,
    /// as in a packaged run.
    ///
    /// <c>temp/</c> is gitignored as a whole rather than by file name: the pages are a snapshot of
    /// whichever game installs are on this machine, and a name pattern only protects the names
    /// somebody thought of.
    /// </summary>
    public static string OutputDirectory(string environmentVariable)
    {
        string? configured = Environment.GetEnvironmentVariable(environmentVariable);
        if ( !string.IsNullOrEmpty(configured) )
        {
            return configured;
        }

        DirectoryInfo? current = new(AppContext.BaseDirectory);
        while ( current is not null )
        {
            string git = Path.Combine(current.FullName, ".git");
            if ( Directory.Exists(git) || File.Exists(git) )
            {
                return Path.Combine(current.FullName, "temp");
            }

            current = current.Parent;
        }

        return Path.GetTempPath();
    }

    /// <summary>
    /// Links to the hub and to this game's other pages. Only pages already on disk are linked, so a
    /// game never swept for lints gets no dead link; the hub is the page that always lists them all.
    /// </summary>
    public static void GameNav(StringBuilder html, string directory, string game, string current)
    {
        List<string> links = [$"<a href=\"{AllGamesPage}\">all games</a>"];

        foreach ( PageLink page in GamePages(game) )
        {
            if ( page.Label == current )
            {
                links.Add($"<b>{Escape(page.Label)}</b>");
            }
            else if ( File.Exists(Path.Combine(directory, page.File)) )
            {
                links.Add($"<a href=\"{Escape(page.File)}\">{Escape(page.Label)}</a>");
            }
        }

        html.AppendLine($"<nav>{string.Join(" · ", links)}</nav>");
    }

    /// <summary>Doctype, title and the shared stylesheet.</summary>
    public static void Head(StringBuilder html, string title)
    {
        html.AppendLine("<!doctype html><html lang=\"en\"><meta charset=\"utf-8\">");
        html.AppendLine("<meta name=\"viewport\" content=\"width=device-width,initial-scale=1\">");
        html.AppendLine($"<title>{Escape(title)}</title>");
        html.AppendLine("<style>");
        html.AppendLine(":root{color-scheme:light dark;--fg:#1a1a1a;--muted:#666;--line:#e5e5e5;--panel:#fafafa;--bar:#6a8fd8;--hot:#fde2e0;--link:#2458b3;");
        html.AppendLine("--error:#c8102e;--warning:#b26a00;--hint:#4a6fa5}");
        html.AppendLine("@media(prefers-color-scheme:dark){:root{--fg:#eee;--muted:#999;--line:#2a2a2a;--panel:#1a1a1a;--bar:#5b7cc0;--hot:#4a2320;--link:#8ab4f8;");
        html.AppendLine("--error:#ff6b7f;--warning:#e0a458;--hint:#8fb3e0}}");
        html.AppendLine("body{font:14px/1.5 system-ui,sans-serif;margin:2rem auto;padding:0 1rem;max-width:72rem;color:var(--fg);background:Canvas}");
        html.AppendLine("h1{font-size:1.4rem;margin-bottom:.2rem}h2{font-size:1.1rem;margin-top:2.2rem}h3{font-size:.95rem;margin-top:1.4rem}");
        html.AppendLine("a{color:var(--link)}nav{margin-bottom:1rem;color:var(--muted)}");
        html.AppendLine(".sub{color:var(--muted);margin-bottom:.8rem}");
        html.AppendLine("table{border-collapse:collapse;width:100%;margin-top:.5rem}");
        html.AppendLine("th,td{text-align:left;padding:.35rem .6rem;border-bottom:1px solid var(--line)}");
        html.AppendLine("th{background:var(--panel);font-weight:600;cursor:pointer;user-select:none;position:sticky;top:0}");
        html.AppendLine("th[data-d=desc]::after{content:' \\25BC'}th[data-d=asc]::after{content:' \\25B2'}");
        html.AppendLine("td.n{text-align:right;font-variant-numeric:tabular-nums}td.hot{background:var(--hot)}");
        html.AppendLine("td.bar{min-width:9rem;font-variant-numeric:tabular-nums}");
        html.AppendLine("td.bar i{display:inline-block;height:.65rem;margin-right:.4rem;background:var(--bar);vertical-align:middle}");
        html.AppendLine("tr.stale td{opacity:.5}.tag{font-size:.75rem;padding:0 .3rem;border:1px solid var(--muted);border-radius:3px}");
        html.AppendLine("code{font:13px ui-monospace,monospace}");
        html.AppendLine(".q{width:100%;box-sizing:border-box;padding:.4rem .6rem;font:13px ui-monospace,monospace;");
        html.AppendLine("border:1px solid var(--line);border-radius:3px;background:transparent;color:inherit}");
        html.AppendLine(".stats{display:flex;gap:2rem;flex-wrap:wrap;margin:1rem 0;padding:1rem;background:var(--panel);border:1px solid var(--line)}");
        html.AppendLine(".stat b{display:block;font-size:1.2rem;font-variant-numeric:tabular-nums}");
        html.AppendLine(".stat span{color:var(--muted);font-size:.85rem}");
        html.AppendLine(".stat.error b,.sev.error{color:var(--error)}.stat.warning b,.sev.warning{color:var(--warning)}");
        html.AppendLine(".sev{font-size:.72rem;text-transform:uppercase;letter-spacing:.05em;font-weight:700}");
        html.AppendLine(".sev.hint,.sev.information{color:var(--hint)}.count{color:var(--muted);font-weight:400;font-size:.85rem}");
        html.AppendLine("details{border:1px solid var(--line);border-radius:4px;margin:.4rem 0;background:var(--panel)}");
        html.AppendLine("summary{cursor:pointer;padding:.45rem .7rem}");
        html.AppendLine("summary .times{display:inline-block;min-width:3.2rem;color:var(--muted);font-variant-numeric:tabular-nums}");
        html.AppendLine(".sites{margin:0;padding:.25rem .7rem .7rem 2.5rem;max-height:32rem;overflow:auto}.sites li{margin:.35rem 0}");
        html.AppendLine(".loc{color:var(--muted)}pre{margin:.15rem 0 0;padding:.35rem .5rem;background:Canvas;border:1px solid var(--line);");
        html.AppendLine("border-radius:3px;overflow-x:auto;font-size:12px}");
        html.AppendLine(".toolbar{display:flex;gap:1rem;flex-wrap:wrap;align-items:center;margin:1rem 0}.toolbar .q{flex:1;min-width:16rem}");
        html.AppendLine("</style>");
    }

    /// <summary>
    /// Sorting and filtering for every table on the page, in a dozen lines of vanilla JS rather than
    /// a grid library — the page has to open from a file:// path with no network. A cell sorts as a
    /// number only when its whole text is one (group separators and a trailing % allowed), so a
    /// timestamp or a path sorts as text; the first click on a header sorts descending, the usual
    /// question of these tables being "what is worst".
    /// </summary>
    public static void Foot(StringBuilder html)
    {
        html.AppendLine("""
            <script>
            (function(){
              function key(cell){
                var s=cell.textContent.trim().replace(/,/g,'');
                return /^-?\d+(\.\d+)?%?$/.test(s)?parseFloat(s):null;
              }
              Array.prototype.forEach.call(document.querySelectorAll('table'),function(t){
                if(!t.tHead)return;
                var first=t.tBodies[0].rows[0];
                Array.prototype.forEach.call(t.tHead.rows[0].cells,function(th,i){
                  if(first&&first.cells[i]&&first.cells[i].classList.contains('n'))th.style.textAlign='right';
                });
                t.tHead.addEventListener('click',function(e){
                  var th=e.target.closest('th'); if(!th)return;
                  var i=Array.prototype.indexOf.call(th.parentNode.children,th),
                      dir=th.getAttribute('data-d')==='desc'?1:-1;
                  Array.prototype.forEach.call(th.parentNode.children,function(h){h.removeAttribute('data-d');});
                  th.setAttribute('data-d',dir<0?'desc':'asc');
                  var b=t.tBodies[0],rows=Array.prototype.slice.call(b.rows);
                  rows.sort(function(a,c){
                    var x=key(a.cells[i]),y=key(c.cells[i]);
                    if(x!==null&&y!==null)return (x-y)*dir;
                    if(x!==null)return -1;
                    if(y!==null)return 1;
                    return a.cells[i].textContent.localeCompare(c.cells[i].textContent)*dir;
                  });
                  for(var k=0;k<rows.length;k++)b.appendChild(rows[k]);
                });
              });
              Array.prototype.forEach.call(document.querySelectorAll('input[data-filter]'),function(q){
                var b=document.getElementById(q.getAttribute('data-filter')).tBodies[0];
                q.addEventListener('input',function(){
                  var v=q.value.toLowerCase();
                  for(var r=0;r<b.rows.length;r++){
                    var p=b.rows[r].querySelector('td.path');
                    b.rows[r].style.display=p&&p.textContent.toLowerCase().indexOf(v)<0?'none':'';
                  }
                });
              });
            })();
            </script>
            """);
    }

    /// <summary>Opens a table with a real header row, which is what the sort script and sticky header hook.</summary>
    public static void TableStart(StringBuilder html, string? id, params string[] columns)
    {
        string idAttribute = id is null ? "" : $" id=\"{Escape(id)}\"";
        StringBuilder header = new();
        foreach ( string column in columns )
        {
            header.Append($"<th>{Escape(column)}</th>");
        }

        html.AppendLine($"<table{idAttribute}><thead><tr>{header}</tr></thead><tbody>");
    }

    public static void TableEnd(StringBuilder html)
    {
        html.AppendLine("</tbody></table>");
    }

    /// <summary>A percentage with a bar drawn to scale, capped at the cell's width.</summary>
    public static string BarCell(double percent)
    {
        double width = Math.Clamp(percent, 0, 100) * 0.06;
        string rem = width.ToString("0.##", CultureInfo.InvariantCulture);
        return $"<td class=\"bar\"><i style=\"width:{rem}rem\"></i>{percent:F1}%</td>";
    }

    /// <summary>One headline figure. <paramref name="cssClass"/> colours it, as a severity does.</summary>
    public static void Stat(StringBuilder html, string label, string value, string cssClass = "")
    {
        string classes = cssClass.Length == 0 ? "stat" : $"stat {cssClass}";
        html.AppendLine($"<div class=\"{classes}\"><b>{Escape(value)}</b><span>{Escape(label)}</span></div>");
    }

    public static string Relative(string path, string root)
    {
        return path.StartsWith(root, StringComparison.OrdinalIgnoreCase)
            ? path[root.Length..].TrimStart('\\', '/')
            : path;
    }

    /// <summary>Full HTML encoding, quotes included, since some of these land inside attributes.</summary>
    public static string Escape(string text)
    {
        return WebUtility.HtmlEncode(text);
    }

    /// <summary>
    /// Writes a page as UTF-8 WITHOUT a byte-order mark: <c>Encoding.UTF8</c> writes one, which would
    /// sit in front of the doctype. The <c>meta charset</c> already declares the encoding.
    /// </summary>
    public static void Save(string path, StringBuilder html)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllText(path, html.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }
}
