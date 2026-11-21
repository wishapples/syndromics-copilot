using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;

namespace Syndromics.Copilot;

/// <summary>Writes a self-contained HTML report: chip map, circuit, history charts, findings, repair steps.
/// No external scripts or fonts, so it opens offline and can be emailed.</summary>
public static class Report
{
    static readonly CultureInfo C = CultureInfo.InvariantCulture;
    static string F(double x) => x.ToString("0.#", C);
    static string H(string s) => WebUtility.HtmlEncode(s);

    public static string Colour(double ratio) => ratio >= 8 ? "#C0392B" : ratio >= 3 ? "#E67E22" : ratio >= 2 ? "#D4AC0D" : "#2E8B57";

    public static string Write(Analyzer a, string path)
    {
        var full = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, Build(a), Encoding.UTF8);
        return full;
    }

    public static string Build(Analyzer a)
    {
        var runs = a.Store.Runs;
        var latest = a.Store.Latest;
        var findings = a.Findings();
        var avoid = a.QubitsToAvoid();
        var alt = a.AlternativeLine();
        var worst = findings.FirstOrDefault(f => f.Trend != Trend.Resolved);

        var details = new JsonObject();
        foreach (var f in findings)
            details[f.Component] = new JsonObject
            {
                ["ratio"] = f.Trend == Trend.Resolved ? "<2×" : Names.X(f.LatestRatio),
                ["trend"] = Analyzer.Describe(f.Trend),
                ["history"] = a.HistoryText(f),
                ["steps"] = new JsonArray(Analyzer.RepairChecklist(f.Component).Select(s => (JsonNode)s).ToArray()),
            };

        var sb = new StringBuilder();
        sb.Append($$$"""
<!DOCTYPE html><html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
<title>Syndromics Copilot · {{{H(latest.Backend)}}} health report</title>
<style>
:root{--ink:#14202E;--soft:#4A5869;--mute:#7A8796;--line:#DDE3EA;--bg:#F6F7F9;--card:#FFFFFF;--navy:#0F2A44;--acc:#1F5FA8}
*{box-sizing:border-box}body{margin:0;font-family:Segoe UI,Inter,Arial,sans-serif;color:var(--ink);background:var(--bg)}
header{background:var(--navy);color:#F2F5F8;padding:28px 40px}header h1{margin:0 0 6px;font-size:26px}header p{margin:0;color:#BCCBDB}
main{max-width:1180px;margin:0 auto;padding:24px 24px 60px}
.kpis{display:grid;grid-template-columns:repeat(4,1fr);gap:16px;margin:-44px 0 24px}
.kpi{background:var(--card);border-radius:14px;padding:18px 20px;box-shadow:0 2px 10px rgba(15,42,68,.08)}
.kpi b{display:block;font-size:30px;color:var(--acc)}.kpi span{font-size:14px;color:var(--soft)}
section{background:var(--card);border-radius:14px;padding:22px 24px;margin-bottom:20px;box-shadow:0 2px 10px rgba(15,42,68,.06)}
h2{margin:0 0 4px;font-size:20px}.sub{margin:0 0 16px;color:var(--soft);font-size:14px}
.grid{display:grid;grid-template-columns:2fr 1fr;gap:20px}
#panel{border:1px solid var(--line);border-radius:12px;padding:16px;font-size:14px;min-height:200px}
#panel h3{margin:0 0 6px;font-size:16px}#panel ol{padding-left:18px;margin:8px 0 0}#panel li{margin-bottom:6px}
table{border-collapse:collapse;width:100%;font-size:14px}th,td{text-align:left;padding:8px 10px;border-bottom:1px solid var(--line)}
th{color:var(--soft);font-weight:600}.pill{display:inline-block;border-radius:999px;padding:2px 10px;color:#fff;font-size:12px;font-weight:600}
.legend span{display:inline-flex;align-items:center;gap:6px;margin-right:16px;font-size:13px;color:var(--soft)}
.legend i{width:14px;height:14px;border-radius:3px;display:inline-block}
pre{white-space:pre-wrap;background:#F1F4F8;border-radius:10px;padding:14px;font-size:13px}
.charts{display:grid;grid-template-columns:repeat(3,1fr);gap:14px}
.click{cursor:pointer}.note{font-size:13px;color:var(--mute)}
@media(max-width:900px){.kpis,.grid,.charts{grid-template-columns:1fr}}
</style></head><body>
<header><h1>Syndromics Copilot · {{{H(latest.Backend)}}}</h1>
<p>{{{runs.Count}}} run(s), latest {{{latest.RunAtUtc:dd MMM yyyy HH:mm}}} UTC · job {{{H(latest.JobId)}}}{{{(latest.Simulated ? " · SIMULATED" : "")}}}</p></header>
<main>
""");
        // KPIs
        var d = latest.Decoding;
        sb.Append("<div class=\"kpis\">");
        Kpi(sb, worst is null ? "–" : Names.X(worst.LatestRatio), worst is null ? "no component flagged" : $"{worst.Component}, {Analyzer.Describe(worst.Trend)}");
        Kpi(sb, d is null ? "–" : $"{d.Calibration / Math.Max(d.SyndromicsAdaptive, 1e-9):0.0}×", "fewer logical errors with Syndromics + adaptive decoding (latest run)");
        Kpi(sb, avoid.Count.ToString(C), "qubits to avoid today");
        Kpi(sb, runs.Count.ToString(C), $"hardware runs since {runs[0].RunAtUtc:dd MMM}");
        sb.Append("</div>");

        // Chip map + side panel
        sb.Append("<section><h2>Where the faults are</h2><p class=\"sub\">Real layout of the chip. Lines are two-qubit gates, dots are qubits; colour = how much worse than IBM's calibration. Click a coloured part for its history and repair steps.</p>");
        sb.Append("<div class=\"grid\"><div>");
        sb.Append(ChipSvg(a, findings, avoid, latest, alt));
        sb.Append("<div class=\"legend\"><span><i style=\"background:#C0392B\"></i>≥ 8× critical</span><span><i style=\"background:#E67E22\"></i>3–8× high</span><span><i style=\"background:#D4AC0D\"></i>2–3× watch</span><span><i style=\"background:#2E8B57\"></i>measured, normal</span><span><i style=\"background:#fff;border:2px solid #1F5FA8\"></i>tested line</span><span><i style=\"background:#fff;border:2px dashed #2E8B57\"></i>suggested line</span></div>");
        sb.Append("</div><div id=\"panel\"><h3>Select a part</h3><p class=\"note\">Click a coloured gate or qubit on the map, or a row in the findings table.</p>");
        if (worst is not null) sb.Append($"<p>Worst now: <b>{H(worst.Component)}</b>, {Names.X(worst.LatestRatio)} its calibrated error ({H(Analyzer.Describe(worst.Trend))}).</p>");
        sb.Append("</div></div></section>");

        // Circuit
        var circRun = runs.LastOrDefault(r => worst is null || r.Covers(worst.Component)) ?? latest;
        sb.Append($"<section><h2>The circuit we ran, and where it breaks</h2><p class=\"sub\">{H(circRun.Label)}: a repetition code. One round shown; it repeats {circRun.Rounds} times, then the data qubits are measured. Flagged parts are coloured.</p>");
        sb.Append(CircuitSvg(circRun, findings));
        sb.Append("</section>");

        // History charts
        sb.Append("<section><h2>History of each flagged part</h2><p class=\"sub\">Ratio to calibration in every run that measured it. Dashed line = 1× (as calibrated).</p><div class=\"charts\">");
        foreach (var f in findings.Take(6)) sb.Append(HistorySvg(f));
        sb.Append("</div></section>");

        // Decoding
        sb.Append("<section><h2>What it does for error correction</h2><p class=\"sub\">Logical error rate on held-out shots in each run (lower is better).</p>");
        sb.Append(DecodingSvg(runs));
        foreach (var r in runs.Where(r => r.Comparison is not null))
        {
            var c = r.Comparison!;
            sb.Append($"<p><b>{H(r.Name)}</b> ({H(c.Kind)} test, model frozen from the earlier run): {c.FrozenErrors} errors vs {c.CalibrationErrors} with IBM's calibration on {c.TestShots:N0} shots" +
                      (c.ReweightingOnNewData.Count > 0 ? $"; re-weighting trained on {c.ReweightingOnNewData[^1].TrainShots:N0} new shots: {c.ReweightingOnNewData[^1].Errors}." : $"; {c.FrozenAdaptiveErrors} with adaptive decoding.") + "</p>");
        }
        sb.Append("</section>");

        // Findings table
        sb.Append("<section><h2>Findings</h2><table><tr><th>Part</th><th>Latest</th><th>Severity</th><th>Trend</th><th>History</th></tr>");
        foreach (var f in findings)
            sb.Append($"<tr class=\"click\" onclick=\"show('{H(f.Component)}')\"><td>{H(f.Component)}</td><td><b>{(f.Trend == Trend.Resolved ? "&lt;2×" : Names.X(f.LatestRatio))}</b></td><td><span class=\"pill\" style=\"background:{(f.Trend == Trend.Resolved ? "#7A8796" : Colour(f.LatestRatio))}\">{(f.Trend == Trend.Resolved ? "OK" : f.Severity.ToString())}</span></td><td>{H(Analyzer.Describe(f.Trend))}</td><td>{H(a.HistoryText(f))}</td></tr>");
        sb.Append("</table></section>");

        // Suggestions + ticket
        sb.Append("<section><h2>What to do now</h2><ol>");
        foreach (var s in a.Suggestions()) sb.Append($"<li>{H(s)}</li>");
        sb.Append("</ol>");
        if (worst is not null) sb.Append($"<h2 style=\"margin-top:18px\">Ticket draft for the hardware team</h2><pre>{H(a.Ticket(worst.Component))}</pre>");
        sb.Append("</section>");

        sb.Append("<p class=\"note\">Every number comes from Syndromics kit results files: ");
        sb.Append(H(string.Join(", ", runs.Select(r => $"{r.Name} job {r.JobId}"))));
        sb.Append(". A repetition code locates a fault to a qubit or pair; it cannot fully separate a gate from its neighbouring measurement. Qubits never measured are unknown, not healthy.</p></main>");
        sb.Append("<script>const D=").Append(details.ToJsonString()).Append(";");
        sb.Append("""
function esc(s){return s.replace(/[&<>"]/g,c=>({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;'}[c]))}
function show(k){const d=D[k];const p=document.getElementById('panel');if(!d){p.innerHTML='<h3>'+esc(k)+'</h3><p class="note">Measured, not flagged.</p>';return}
p.innerHTML='<h3>'+esc(k)+'</h3><p><b>'+esc(d.ratio)+'</b> its calibrated error, '+esc(d.trend)+'.</p><p class="note">'+esc(d.history)+'</p><b>Repair steps</b><ol>'+d.steps.map(s=>'<li>'+esc(s)+'</li>').join('')+'</ol>'}
</script></body></html>
""");
        return sb.ToString();
    }

    static void Kpi(StringBuilder sb, string big, string text) => sb.Append($"<div class=\"kpi\"><b>{H(big)}</b><span>{H(text)}</span></div>");

    static string ChipSvg(Analyzer a, IReadOnlyList<Finding> findings, IReadOnlyList<(int Qubit, string Reason)> avoid, Run latest, int[]? alt)
    {
        const double S = 46, P = 26;
        var pos = a.Map.Pos;
        double W = pos.Values.Max(p => p.X) * S + 2 * P, Hh = pos.Values.Max(p => p.Y) * S + 2 * P;
        (double x, double y) XY(int q) => (P + pos[q].X * S, P + pos[q].Y * S);
        var gateF = findings.Where(f => f.Qubits.Length == 2 && f.Trend != Trend.Resolved)
                            .ToDictionary(f => (Math.Min(f.Qubits[0], f.Qubits[1]), Math.Max(f.Qubits[0], f.Qubits[1])));
        var qubitF = findings.Where(f => f.Qubits.Length == 1 && f.Trend != Trend.Resolved).GroupBy(f => f.Qubits[0])
                             .ToDictionary(g => g.Key, g => g.OrderByDescending(f => f.LatestRatio).First());
        var measured = new HashSet<int>(a.Store.Runs.SelectMany(r => r.Line));
        var lineEdges = new HashSet<(int, int)>(latest.Line.Zip(latest.Line.Skip(1), (x, y) => (Math.Min(x, y), Math.Max(x, y))));
        var altEdges = alt is null ? new HashSet<(int, int)>() : new HashSet<(int, int)>(alt.Zip(alt.Skip(1), (x, y) => (Math.Min(x, y), Math.Max(x, y))));
        var avoidSet = new HashSet<int>(avoid.Select(v => v.Qubit));

        var sb = new StringBuilder($"<svg viewBox=\"0 0 {F(W)} {F(Hh)}\" width=\"100%\" role=\"img\" aria-label=\"Chip map of {H(a.Map.Backend)} with flagged components\">");
        foreach (var (x0, y0) in a.Map.Edges)
        {
            var k = (Math.Min(x0, y0), Math.Max(x0, y0));
            var (ax, ay) = XY(x0); var (bx, by) = XY(y0);
            if (altEdges.Contains(k)) sb.Append($"<line x1=\"{F(ax)}\" y1=\"{F(ay)}\" x2=\"{F(bx)}\" y2=\"{F(by)}\" stroke=\"#2E8B57\" stroke-width=\"9\" stroke-dasharray=\"6 5\" opacity=\".55\"/>");
            if (lineEdges.Contains(k)) sb.Append($"<line x1=\"{F(ax)}\" y1=\"{F(ay)}\" x2=\"{F(bx)}\" y2=\"{F(by)}\" stroke=\"#1F5FA8\" stroke-width=\"12\" opacity=\".35\"/>");
            if (gateF.TryGetValue(k, out var gf))
                sb.Append($"<g class=\"click\" onclick=\"show('{H(gf.Component)}')\"><title>{H(gf.Component)}: {Names.X(gf.LatestRatio)} calibration ({H(Analyzer.Describe(gf.Trend))})</title><line x1=\"{F(ax)}\" y1=\"{F(ay)}\" x2=\"{F(bx)}\" y2=\"{F(by)}\" stroke=\"{Colour(gf.LatestRatio)}\" stroke-width=\"7\" stroke-linecap=\"round\"/></g>");
            else
                sb.Append($"<line x1=\"{F(ax)}\" y1=\"{F(ay)}\" x2=\"{F(bx)}\" y2=\"{F(by)}\" stroke=\"#C9D2DC\" stroke-width=\"2\"/>");
        }
        foreach (var (q, _) in pos)
        {
            var (x, y) = XY(q);
            string fill = "#EEF1F5", stroke = "#B8C3CF"; string? click = null; var title = $"q{q}: not measured yet";
            if (measured.Contains(q)) { fill = "#2E8B57"; stroke = "#2E8B57"; title = $"q{q}: measured, normal"; }
            if (qubitF.TryGetValue(q, out var qf)) { fill = Colour(qf.LatestRatio); stroke = fill; title = $"{qf.Component}: {Names.X(qf.LatestRatio)} calibration"; click = qf.Component; }
            if (avoidSet.Contains(q)) title += " · avoid today";
            sb.Append(click is null ? "<g>" : $"<g class=\"click\" onclick=\"show('{H(click)}')\">");
            sb.Append($"<title>{H(title)}</title>");
            if (avoidSet.Contains(q)) sb.Append($"<circle cx=\"{F(x)}\" cy=\"{F(y)}\" r=\"15\" fill=\"none\" stroke=\"#C0392B\" stroke-width=\"2.5\"/>");
            sb.Append($"<circle cx=\"{F(x)}\" cy=\"{F(y)}\" r=\"10\" fill=\"{fill}\" stroke=\"{stroke}\" stroke-width=\"1.5\"/>");
            if (measured.Contains(q) || avoidSet.Contains(q))
                sb.Append($"<text x=\"{F(x)}\" y=\"{F(y - 15)}\" text-anchor=\"middle\" font-size=\"11\" fill=\"#14202E\">{q}</text>");
            sb.Append("</g>");
        }
        return sb.Append("</svg>").ToString();
    }

    static string CircuitSvg(Run r, IReadOnlyList<Finding> findings)
    {
        var n = r.Line.Length; const double rowH = 40, x0 = 90, top = 24;
        double W = 900, Hh = top + n * rowH + 20;
        double Y(int i) => top + i * rowH + rowH / 2;
        Finding? FOf(string c) => findings.FirstOrDefault(f => f.Component == c && f.Trend != Trend.Resolved);
        var sb = new StringBuilder($"<svg viewBox=\"0 0 {F(W)} {F(Hh)}\" width=\"100%\" role=\"img\" aria-label=\"Repetition-code circuit, one round\">");
        for (int i = 0; i < n; i++)
        {
            bool meas = i % 2 == 1;
            sb.Append($"<text x=\"10\" y=\"{F(Y(i) + 4)}\" font-size=\"13\" fill=\"#14202E\">q{r.Line[i]}</text><text x=\"52\" y=\"{F(Y(i) + 4)}\" font-size=\"11\" fill=\"#7A8796\">{(meas ? "meas" : "data")}</text>");
            sb.Append($"<line x1=\"{F(x0)}\" y1=\"{F(Y(i))}\" x2=\"{F(W - 20)}\" y2=\"{F(Y(i))}\" stroke=\"#B8C3CF\"/>");
        }
        string Box(double x, int i, string label, string col) =>
            $"<rect x=\"{F(x - 14)}\" y=\"{F(Y(i) - 13)}\" width=\"28\" height=\"26\" rx=\"5\" fill=\"#fff\" stroke=\"{col}\" stroke-width=\"1.5\"/><text x=\"{F(x)}\" y=\"{F(Y(i) + 5)}\" text-anchor=\"middle\" font-size=\"12\" fill=\"#14202E\">{label}</text>";
        void Cz(double x, int i, int j)
        {
            var name = $"q{r.Line[Math.Min(i, j)]}-q{r.Line[Math.Max(i, j)]} two-qubit gate";
            var alt = $"q{r.Line[Math.Max(i, j)]}-q{r.Line[Math.Min(i, j)]} two-qubit gate";
            var f = FOf(name) ?? FOf(alt);
            var col = f is null ? "#14202E" : Colour(f.LatestRatio); var w = f is null ? 1.5 : 4;
            sb.Append(f is null ? "<g>" : $"<g class=\"click\" onclick=\"show('{H(f.Component)}')\"><title>{H(f.Component)}: {Names.X(f.LatestRatio)} calibration</title>");
            sb.Append($"<line x1=\"{F(x)}\" y1=\"{F(Y(i))}\" x2=\"{F(x)}\" y2=\"{F(Y(j))}\" stroke=\"{col}\" stroke-width=\"{F(w)}\"/><circle cx=\"{F(x)}\" cy=\"{F(Y(i))}\" r=\"5\" fill=\"{col}\"/><circle cx=\"{F(x)}\" cy=\"{F(Y(j))}\" r=\"5\" fill=\"{col}\"/></g>");
        }
        for (int m = 1; m < n; m += 2) sb.Append(Box(x0 + 40, m, "H", "#14202E"));
        for (int m = 1; m < n; m += 2) Cz(x0 + 110, m, m - 1);
        for (int m = 1; m < n; m += 2) Cz(x0 + 180, m, m + 1);
        for (int m = 1; m < n; m += 2) sb.Append(Box(x0 + 250, m, "H", "#14202E"));
        for (int m = 1; m < n; m += 2)
        {
            var f = FOf($"q{r.Line[m]} measurement");
            var col = f is null ? "#14202E" : Colour(f.LatestRatio);
            sb.Append(f is null ? "<g>" : $"<g class=\"click\" onclick=\"show('{H(f.Component)}')\"><title>{H(f.Component)}: {Names.X(f.LatestRatio)} calibration</title>");
            sb.Append(Box(x0 + 320, m, "M", col)).Append(Box(x0 + 370, m, "R", "#14202E")).Append("</g>");
        }
        sb.Append($"<rect x=\"{F(x0 + 410)}\" y=\"{F(top)}\" width=\"200\" height=\"{F(n * rowH)}\" rx=\"10\" fill=\"#F1F4F8\"/><text x=\"{F(x0 + 510)}\" y=\"{F(top + n * rowH / 2 + 5)}\" text-anchor=\"middle\" font-size=\"14\" fill=\"#4A5869\">repeat × {r.Rounds} rounds</text>");
        for (int dq = 0; dq < n; dq += 2)
        {
            var f = FOf($"q{r.Line[dq]} measurement");
            sb.Append(f is null ? "<g>" : $"<g class=\"click\" onclick=\"show('{H(f.Component)}')\"><title>{H(f.Component)}: {Names.X(f.LatestRatio)} calibration</title>");
            sb.Append(Box(W - 60, dq, "M", f is null ? "#14202E" : Colour(f.LatestRatio))).Append("</g>");
        }
        return sb.Append("</svg>").ToString();
    }

    static string HistorySvg(Finding f)
    {
        const double W = 340, Hh = 170, L = 40, R = 14, T = 34, B = 30;
        var pts = f.History.Where(h => h.Ratio.HasValue).Select(h => (h.Run, h.Ratio!.Value)).ToList();
        var ymax = Math.Max(4, pts.Max(p => p.Value) * 1.15);
        double X(int i) => pts.Count == 1 ? (L + W - R) / 2 : L + i * (W - L - R) / (pts.Count - 1);
        double Y(double v) => T + (1 - v / ymax) * (Hh - T - B);
        var col = Colour(f.LatestRatio);
        var sb = new StringBuilder($"<svg viewBox=\"0 0 {F(W)} {F(Hh)}\" width=\"100%\" class=\"click\" onclick=\"show('{H(f.Component)}')\" role=\"img\" aria-label=\"History of {H(f.Component)}\">");
        sb.Append($"<text x=\"{F(L)}\" y=\"18\" font-size=\"13\" font-weight=\"600\" fill=\"#14202E\">{H(f.Component)}</text>");
        sb.Append($"<line x1=\"{F(L)}\" x2=\"{F(W - R)}\" y1=\"{F(Y(1))}\" y2=\"{F(Y(1))}\" stroke=\"#7A8796\" stroke-dasharray=\"4 4\"/><text x=\"6\" y=\"{F(Y(1) + 4)}\" font-size=\"11\" fill=\"#7A8796\">1×</text>");
        sb.Append($"<polyline fill=\"none\" stroke=\"{col}\" stroke-width=\"2.5\" points=\"{string.Join(" ", pts.Select((p, i) => $"{F(X(i))},{F(Y(p.Value))}"))}\"/>");
        for (int i = 0; i < pts.Count; i++)
        {
            sb.Append($"<circle cx=\"{F(X(i))}\" cy=\"{F(Y(pts[i].Value))}\" r=\"5\" fill=\"{col}\"><title>{H(pts[i].Run.Name)} job {H(pts[i].Run.JobId)}: {Names.Reading(pts[i].Run, f.Component)}</title></circle>");
            sb.Append($"<text x=\"{F(X(i))}\" y=\"{F(Y(pts[i].Value) - 9)}\" text-anchor=\"middle\" font-size=\"11\" fill=\"#14202E\">{Names.Reading(pts[i].Run, f.Component)}</text>");
            sb.Append($"<text x=\"{F(X(i))}\" y=\"{F(Hh - 10)}\" text-anchor=\"middle\" font-size=\"11\" fill=\"#4A5869\">{H(pts[i].Run.Name)}</text>");
        }
        return sb.Append("</svg>").ToString();
    }

    static string DecodingSvg(IReadOnlyList<Run> runs)
    {
        var rs = runs.Where(r => r.Decoding is not null).ToList();
        if (rs.Count == 0) return "<p class=\"note\">No decoding results.</p>";
        var series = new (string Name, string Col, Func<DecodingResult, double> Get)[]
        {
            ("IBM calibration", "#C2621A", d => d.Calibration), ("Re-weighting", "#9AA6B2", d => d.Reweighting),
            ("Syndromics", "#5B8FD0", d => d.Syndromics), ("Syndromics + adaptive", "#1F5FA8", d => d.SyndromicsAdaptive),
        };
        double max = rs.Max(r => r.Decoding!.Calibration) * 1.15;
        const double rowH = 26, L = 200, Wd = 900;
        var groupH = series.Length * rowH + 22;
        var sb = new StringBuilder($"<svg viewBox=\"0 0 {F(Wd)} {F(rs.Count * groupH + 10)}\" width=\"100%\" role=\"img\" aria-label=\"Logical error per run and decoder\">");
        for (int g = 0; g < rs.Count; g++)
        {
            var y0 = g * groupH + 10;
            sb.Append($"<text x=\"0\" y=\"{F(y0 + 12)}\" font-size=\"13\" font-weight=\"600\" fill=\"#14202E\">{H(rs[g].Label)}</text>");
            for (int s = 0; s < series.Length; s++)
            {
                var v = series[s].Get(rs[g].Decoding!); var y = y0 + 20 + s * rowH;
                var w = (Wd - L - 90) * v / max;
                sb.Append($"<text x=\"12\" y=\"{F(y + 13)}\" font-size=\"12\" fill=\"#4A5869\">{series[s].Name}</text><rect x=\"{F(L)}\" y=\"{F(y + 2)}\" width=\"{F(w)}\" height=\"16\" rx=\"4\" fill=\"{series[s].Col}\"/><text x=\"{F(L + w + 6)}\" y=\"{F(y + 15)}\" font-size=\"12\" fill=\"#14202E\">{Names.Pct(v)}</text>");
            }
        }
        return sb.Append("</svg>").ToString();
    }
}
