using System.Globalization;
using System.Text;

namespace Syndromics.Copilot;

public enum Severity { Watch, High, Critical }
public enum Trend { New, Persistent, Worsening, Improving, Resolved }

/// <summary>A component's story across every run that measured it.</summary>
public sealed record Finding(string Component, Severity Severity, Trend Trend, double LatestRatio,
                             IReadOnlyList<(Run Run, double? Ratio)> History, double Calibration, double Measured)
{
    public int[] Qubits => Names.QubitsOf(Component);
    public string Kind => Names.KindOf(Component);
    public int TimesFlagged => History.Count(h => h.Ratio >= Analyzer.FlagRatio);
}

/// <summary>Turns run results into findings, advice and repair steps. Every number it states comes from a run file.</summary>
public sealed class Analyzer
{
    public const double FlagRatio = 2.0;
    public RunStore Store { get; }
    public ChipMap Map { get; }
    public Memory Memory { get; }

    public Analyzer(RunStore store, ChipMap map, Memory memory) { Store = store; Map = map; Memory = memory; }

    double AlertAt => Memory.AlertThreshold;

    public static Severity SeverityOf(double r) => r >= 8 ? Severity.Critical : r >= 3 ? Severity.High : Severity.Watch;

    /// <summary>Components flagged in any run, with their history and trend, worst first; the team's ignore list applied.</summary>
    public IReadOnlyList<Finding> Findings(bool includeIgnored = false)
    {
        var names = Store.Runs.SelectMany(r => r.Flagged.Select(f => f.Component)).Distinct();
        var list = new List<Finding>();
        foreach (var c in names)
        {
            if (!includeIgnored && Memory.Ignored.Contains(c)) continue;
            var hist = Store.Runs.Where(r => r.Covers(c)).Select(r => (r, r.RatioOf(c))).ToList();
            var measured = hist.Where(h => h.Item2.HasValue).ToList();
            if (measured.Count == 0) continue;
            var latest = measured[^1];
            var latestReading = Store.Runs.Last(r => r.Flagged.Any(f => f.Component == c)).Flagged.First(f => f.Component == c);
            var trend = TrendOf(measured.Select(h => h.Item2!.Value).ToList());
            list.Add(new Finding(c, SeverityOf(latest.Item2!.Value), trend, latest.Item2!.Value,
                                 hist.Select(h => (h.r, h.Item2)).ToList(), latestReading.Calibration, latestReading.Measured));
        }
        return list.OrderByDescending(f => f.Trend != Trend.Resolved).ThenByDescending(f => f.LatestRatio).ToList();
    }

    static Trend TrendOf(IReadOnlyList<double> r)
    {
        var last = r[^1];
        if (r.Count == 1) return last >= FlagRatio ? Trend.New : Trend.Resolved;
        var prev = r[^2];
        if (last < FlagRatio) return Trend.Resolved;
        if (prev < FlagRatio) return Trend.New;
        if (last > prev * 1.5) return Trend.Worsening;
        if (last < prev / 1.5) return Trend.Improving;
        return Trend.Persistent;
    }

    /// <summary>Qubits in components at or above the team's alert threshold in the latest run that measured them.</summary>
    public IReadOnlyList<(int Qubit, string Reason)> QubitsToAvoid(double? threshold = null)
    {
        var t = threshold ?? AlertAt;
        var outp = new Dictionary<int, string>();
        foreach (var f in Findings().Where(f => f.Trend != Trend.Resolved && f.LatestRatio >= t))
            foreach (var q in f.Qubits)
                outp.TryAdd(q, $"{f.Component}: {Names.X(f.LatestRatio)} calibration ({Describe(f.Trend)})");
        foreach (var r in Store.Runs.TakeLast(1))
            foreach (var (q, share) in r.Bursts?.PerMeasureQubit ?? new Dictionary<int, double>())
                if (share >= 0.006) outp.TryAdd(q, $"erratic in {Names.Pct(share)} of shots in {r.Name}");
        return outp.Select(k => (k.Key, k.Value)).OrderBy(k => k.Key).ToList();
    }

    public int[]? AlternativeLine(int? length = null)
    {
        var latest = Store.Latest;
        var avoid = new HashSet<int>(QubitsToAvoid().Select(a => a.Qubit));
        return Map.FindLine(length ?? latest.Line.Length, avoid, latest.Line);
    }

    public static string Describe(Trend t) => t switch
    {
        Trend.New => "new in its latest run",
        Trend.Persistent => "persistent",
        Trend.Worsening => "getting worse",
        Trend.Improving => "improving",
        _ => "back to normal",
    };

    public string HistoryText(Finding f) => string.Join(", ",
        f.History.Select(h => $"{h.Run.Name} {Names.Reading(h.Run, f.Component)}"));

    /// <summary>What an engineer can check to find the physical cause. A repetition code locates a fault to a
    /// qubit or pair but cannot separate a gate from its neighbouring measurement, so the list covers both.</summary>
    public static IReadOnlyList<string> RepairChecklist(string component)
    {
        var q = Names.QubitsOf(component);
        var kind = Names.KindOf(component);
        var steps = new List<string>();
        if (kind.Contains("gate") && q.Length == 2)
        {
            steps.Add($"Benchmark the q{q[0]}-q{q[1]} two-qubit gate on its own (interleaved randomized benchmarking) and compare with the published error.");
            steps.Add($"Measure T1 and T2 of q{q[0]} and q{q[1]}; a drop on one qubit points to a defect (for example a two-level system) rather than the gate.");
            steps.Add($"Check mid-circuit readout and reset on q{q[0]} and q{q[1]}: a repetition code cannot fully separate a gate fault from a neighbouring measurement fault.");
            steps.Add("If the gate itself is at fault, recalibrate it (amplitude, phase and duration); if a qubit is degraded, flag it so layout avoids it.");
        }
        else if (kind.Contains("measurement"))
        {
            steps.Add($"Re-measure q{q[0]}'s readout assignment matrix mid-circuit and at the end of the circuit; mid-circuit readout is what error correction uses.");
            steps.Add($"Check q{q[0]}'s reset: shots where it fires repeatedly suggest leakage or failed reset.");
            steps.Add($"Check its neighbouring two-qubit gates; part of a measurement excess can come from them.");
            steps.Add("Recalibrate the readout (frequency, amplitude, integration weights) or exclude the qubit from layouts until it recovers.");
        }
        else
        {
            steps.Add($"Measure T1 and T2 on {string.Join(", ", q.Select(x => "q" + x))}; scan for two-level-system defects if they dropped.");
            steps.Add("Recalibrate or exclude the qubit from layouts until it recovers.");
        }
        steps.Add("Rerun Syndromics after the fix to confirm the component is back near its calibration.");
        return steps;
    }

    public string Ticket(string component)
    {
        var f = Findings(includeIgnored: true).FirstOrDefault(x => x.Component.Equals(component, StringComparison.OrdinalIgnoreCase))
                ?? throw new ArgumentException($"No finding for '{component}'.");
        var sb = new StringBuilder();
        sb.AppendLine($"Title: {f.Component} at {Names.X(f.LatestRatio)} its calibrated error on {Store.Latest.Backend} ({Describe(f.Trend)})");
        sb.AppendLine($"Severity: {f.Severity}");
        sb.AppendLine($"Evidence: {HistoryText(f)} (ratio to calibration, Syndromics fit on syndrome data).");
        if (f.Calibration > 0) sb.AppendLine($"Published value {f.Calibration:0.0000}; measured about {f.Measured:0.0000}.");
        sb.AppendLine($"Runs: {string.Join(", ", f.History.Select(h => $"{h.Run.Name} job {h.Run.JobId} ({h.Run.RunAtUtc:yyyy-MM-dd})"))}.");
        sb.AppendLine("Impact: avoidable logical errors for circuits that use these qubits; decoders given the published value mis-weight this component.");
        sb.AppendLine("Suggested checks:");
        foreach (var s in RepairChecklist(f.Component)) sb.AppendLine($"  - {s}");
        sb.AppendLine("Workaround until fixed: avoid these qubits in layouts, or give the decoder the Syndromics model.");
        return sb.ToString();
    }

    /// <summary>Plain-language summary of a run, for an engineer or a manager.</summary>
    public string Summary(Run r, string audience)
    {
        var sb = new StringBuilder();
        var mgr = audience == "manager";
        sb.AppendLine($"{r.Label} on {r.Backend}, job {r.JobId}.");
        if (r.Decoding is { } d)
            sb.AppendLine(mgr
                ? $"With our measured model the error-correction decoder made {Ratio(d.Calibration, d.Syndromics)} fewer mistakes than with the published calibration, and {Ratio(d.Calibration, d.SyndromicsAdaptive)} fewer with adaptive decoding."
                : $"Held-out logical error ({d.Splits} splits, {d.TestShots:N0} test shots): calibration {Names.Pct(d.Calibration)}, re-weighting {Names.Pct(d.Reweighting)}, Syndromics {Names.Pct(d.Syndromics)}, Syndromics + adaptive {Names.Pct(d.SyndromicsAdaptive)} (re-weighting + adaptive {Names.Pct(d.ReweightingAdaptive)}).");
        if (r.Comparison is { } c)
            sb.AppendLine(c.Kind == "transfer"
                ? $"Transfer test: model from the previous circuit, no new data: {c.FrozenErrors} errors ({c.FrozenAdaptiveErrors} with adaptive decoding) vs {c.CalibrationErrors} for the calibration on {c.TestShots:N0} shots" +
                  (c.ReweightingOnNewData.Count > 0 ? TransferVerdict(c.FrozenErrors, c.ReweightingOnNewData[^1].Errors, c.ReweightingOnNewData[^1].TrainShots) : ".")
                : $"Frozen model from the previous run: {c.FrozenErrors} errors vs {c.CalibrationErrors} for the fresh calibration ({c.FrozenAdaptiveErrors} with adaptive decoding) on {c.TestShots:N0} shots.");
        foreach (var f in r.Flagged.Take(mgr ? 2 : 5))
            sb.AppendLine(mgr ? $"Problem part: {f.Component}, about {Names.X(f.Ratio)} worse than the published figure."
                              : $"Flagged: {f.Component} {Names.X(f.Ratio)} ± {f.Err:0.0} (calibration {f.Calibration:0.0000}, measured {f.Measured:0.0000}).");
        if (r.Bursts is { } b)
            sb.AppendLine(mgr ? $"In {Names.Pct(b.RealShare)} of shots one measuring qubit behaved erratically; adaptive decoding handles those shots."
                              : $"Erratic-measure-qubit shots: {Names.Pct(b.RealShare)} real vs {Names.Pct(b.ModelShare)} predicted by the fitted model.");
        foreach (var a in r.DriftAlerts)
            sb.AppendLine($"Drift alert in the job: {a.Component} {Names.X(a.Before)} → {Names.X(a.Now)} (window {a.Window}).");
        return sb.ToString().TrimEnd();
    }

    static string Ratio(double a, double b) => b > 0 ? (a / b).ToString("0.0", CultureInfo.InvariantCulture) + "×" : "n/a";

    /// <summary>What changed between the two most recent runs that share components.</summary>
    public IReadOnlyList<string> WhatChanged()
    {
        if (Store.Runs.Count < 2) return ["Only one run so far: nothing to compare yet."];
        var outp = new List<string>();
        var latest = Store.Latest; var prev = Store.Runs[^2];
        foreach (var f in Findings())
        {
            var a = prev.RatioOf(f.Component); var b = latest.RatioOf(f.Component);
            if (a is null || b is null) continue;
            var ra = Names.Reading(prev, f.Component); var rb = Names.Reading(latest, f.Component);
            if (b >= FlagRatio && a < FlagRatio) outp.Add($"NEW: {f.Component} {ra} → {rb}");
            else if (a >= FlagRatio && b < FlagRatio) outp.Add($"RECOVERED: {f.Component} {ra} → {rb}");
            else if (b > a * 1.5 && b >= FlagRatio) outp.Add($"WORSE: {f.Component} {ra} → {rb}");
            else if (b >= FlagRatio) outp.Add($"STILL BAD: {f.Component} {ra} → {rb}");
        }
        if (outp.Count == 0) outp.Add($"No flagged component changed between {prev.Name} and {latest.Name}.");
        return outp;
    }

    /// <summary>Next steps, ordered by what helps most now.</summary>
    public IReadOnlyList<string> Suggestions()
    {
        var s = new List<string>();
        var avoid = QubitsToAvoid();
        if (avoid.Count > 0)
        {
            s.Add($"Avoid {string.Join(", ", avoid.Select(a => "q" + a.Qubit))} in today's layouts.");
            if (AlternativeLine() is { } alt)
                s.Add($"A {alt.Length}-qubit line that avoids them: {string.Join(",", alt)} (only avoids measured problems; run the chip-wide health check to confirm the rest).");
        }
        var worst = Findings().FirstOrDefault(f => f.Trend != Trend.Resolved);
        if (worst is not null)
            s.Add($"Send the {worst.Component} finding to the hardware team (seen in {worst.TimesFlagged} run(s)); a ticket draft is ready.");
        if (Store.Latest.Bursts is { RealShare: > 0.01 } b)
            s.Add($"Keep adaptive decoding on: {Names.Pct(b.RealShare)} of shots had an erratic measure qubit.");
        if ((DateTimeOffset.UtcNow - Store.Latest.RunAtUtc).TotalHours > 24)
            s.Add($"The latest run is {(int)(DateTimeOffset.UtcNow - Store.Latest.RunAtUtc).TotalDays} day(s) old; rerun the check to see what drifted.");
        return s;
    }

    /// <summary>Honest one-line comparison with re-weighting trained on new data. Error counts are Poisson-like, so a gap
    /// smaller than twice its counting uncertainty, 2*sqrt(a+b), is reported as a tie.</summary>
    public static string TransferVerdict(int frozen, int reweighting, int shots)
    {
        if (Math.Abs(frozen - reweighting) <= 2 * Math.Sqrt(frozen + reweighting))
            return $"; about the same as re-weighting trained on {shots:N0} new shots ({reweighting}), with none.";
        return frozen < reweighting
            ? $"; better than re-weighting trained on {shots:N0} new shots ({reweighting})."
            : $"; re-weighting trained on {shots:N0} new shots does better ({reweighting}), but needs that new data.";
    }
}
