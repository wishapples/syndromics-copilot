using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Syndromics.Copilot;

/// <summary>One component the fit says is worse (or better) than its calibration.</summary>
public sealed record ComponentReading(string Component, double Ratio, double Err, double Calibration, double Measured)
{
    /// <summary>Physical qubits named in the component, e.g. "q129-q128 two-qubit gate" -> [129, 128].</summary>
    public int[] Qubits => Names.QubitsOf(Component);
    public string Kind => Names.KindOf(Component);
}

public sealed record DecodingResult(double Calibration, double Reweighting, double Syndromics,
                                    double SyndromicsAdaptive, double ReweightingAdaptive, int TestShots, int Splits);

public sealed record BurstResult(double RealShare, double ModelShare, IReadOnlyDictionary<int, double> PerMeasureQubit);

public sealed record DriftAlert(string Component, int Window, double Before, double Now, double Z);

/// <summary>A frozen-model comparison written by `compare-runs` (same circuit or a transfer to a new circuit).</summary>
public sealed record FrozenComparison(string Kind, string PreviousJob, int CalibrationErrors, int FrozenErrors,
                                      int FrozenAdaptiveErrors, int TestShots,
                                      IReadOnlyList<(int TrainShots, int Errors)> ReweightingOnNewData,
                                      IReadOnlyList<(string Component, double Before, double After)> Changes);

/// <summary>Everything Copilot knows about one hardware run, read from a kit `results` folder.</summary>
public sealed class Run
{
    public required string Folder { get; init; }
    public required string Name { get; init; }
    public required string JobId { get; init; }
    public required string Backend { get; init; }
    public required DateTimeOffset RunAtUtc { get; init; }
    public string? CalibrationUpdated { get; init; }
    public required int[] Line { get; init; }
    public int NData { get; init; }
    public int Rounds { get; init; }
    public int Shots { get; init; }
    public bool Simulated { get; init; }
    public DecodingResult? Decoding { get; init; }
    public required IReadOnlyList<ComponentReading> Flagged { get; init; }
    /// <summary>Mean ratio to calibration of every fitted component over the job's time windows.</summary>
    public required IReadOnlyDictionary<string, double> AllRatios { get; init; }
    public BurstResult? Bursts { get; init; }
    public required IReadOnlyList<DriftAlert> DriftAlerts { get; init; }
    public FrozenComparison? Comparison { get; init; }

    public string ShortJob => JobId.Length > 8 ? JobId[..8] : JobId;
    public string Label => $"{Name} ({RunAtUtc:dd MMM}, {Line.Length} qubits, {Rounds} rounds)";

    /// <summary>Ratio to calibration from the run's main fit, or null if this run did not measure the component.
    /// A measured component that the fit did not flag is reported as 1 (within 2× of its calibration). Per-window
    /// ratios (AllRatios) are not used here: short windows cannot separate a gate from its neighbouring measurement.</summary>
    public double? RatioOf(string component)
    {
        var f = Flagged.FirstOrDefault(c => c.Component == component);
        if (f is not null) return f.Ratio;
        return Covers(component) ? 1.0 : null;
    }

    public bool IsFlagged(string component) => Flagged.Any(c => c.Component == component);

    public bool Covers(string component) => Names.QubitsOf(component).All(q => Line.Contains(q));

    public static Run Load(string resultsFolder, string name)
    {
        var v = JsonNode.Parse(File.ReadAllText(Path.Combine(resultsFolder, "A_validate.json")))!;
        var cfgPath = Path.Combine(resultsFolder, "A_config.json");
        var cfg = File.Exists(cfgPath) ? JsonNode.Parse(File.ReadAllText(cfgPath)) : null;
        var meta = v["meta"]!;

        DecodingResult? dec = null;
        if (v["decoding"]?["mean"] is JsonObject m)
        {
            double G(string k) => m[k]?.GetValue<double>() ?? double.NaN;
            dec = new DecodingResult(G("calibration"), G("reweighting"), G("syndromics"), G("syndromics_la"),
                                     G("reweighting_la"), v["decoding"]?["test_shots"]?.GetValue<int>() ?? 0,
                                     (v["decoding"]?["tests"] as JsonArray)?.Count ?? 0);
        }

        var flagged = (v["changed_components"] as JsonArray ?? [])
            .Select(c => new ComponentReading(c!["component"]!.GetValue<string>(), c["ratio"]!.GetValue<double>(),
                                              c["err"]?.GetValue<double>() ?? 0, c["calibration"]?.GetValue<double>() ?? 0,
                                              c["measured"]?.GetValue<double>() ?? 0))
            .OrderByDescending(c => c.Ratio).ToList();

        var sums = new Dictionary<string, (double s, int n)>();
        foreach (var w in v["drift"]?["windows"] as JsonArray ?? [])
            foreach (var kv in w!.AsObject())
            {
                var x = kv.Value!.GetValue<double>();
                sums[kv.Key] = sums.TryGetValue(kv.Key, out var a) ? (a.s + x, a.n + 1) : (x, 1);
            }
        var all = sums.ToDictionary(k => k.Key, k => k.Value.s / k.Value.n);

        var line = (v["line"] as JsonArray)!.Select(x => x!.GetValue<int>()).ToArray();
        BurstResult? bursts = null;
        if (v["bursts"] is JsonObject b)
        {
            var anc = (b["anc"] as JsonArray ?? []).Select(x => x!.GetValue<int>()).ToList();
            var per = (b["per_qubit_real"] as JsonArray ?? []).Select(x => x!.GetValue<double>()).ToList();
            var map = new Dictionary<int, double>();
            for (int i = 0; i < Math.Min(anc.Count, per.Count); i++)
                if (anc[i] >= 0 && anc[i] < line.Length) map[line[anc[i]]] = per[i];
            bursts = new BurstResult(b["real"]?.GetValue<double>() ?? 0, b["model"]?.GetValue<double>() ?? 0, map);
        }

        var alerts = (v["drift"]?["alerts"] as JsonArray ?? [])
            .Select(a => new DriftAlert(a!["component"]!.GetValue<string>(), a["window"]?.GetValue<int>() ?? 0,
                                        a["before"]?.GetValue<double>() ?? 0, a["now"]?.GetValue<double>() ?? 0,
                                        a["z"]?.GetValue<double>() ?? 0)).ToList();

        return new Run
        {
            Folder = resultsFolder, Name = name,
            JobId = v["job_id"]?.GetValue<string>() ?? "unknown",
            Backend = meta["backend"]?.GetValue<string>() ?? "unknown",
            RunAtUtc = DateTimeOffset.Parse(meta["run_at_utc"]!.GetValue<string>(), CultureInfo.InvariantCulture),
            CalibrationUpdated = meta["calibration_last_updated"]?.GetValue<string>(),
            Simulated = meta["fake"]?.GetValue<bool>() ?? false,
            Line = line,
            NData = cfg?["n_data"]?.GetValue<int>() ?? (line.Length + 1) / 2,
            Rounds = v["rounds"]?.GetValue<int>() ?? cfg?["rounds"]?.GetValue<int>() ?? 0,
            Shots = v["shots"]?.GetValue<int>() ?? 0,
            Decoding = dec, Flagged = flagged, AllRatios = all, Bursts = bursts, DriftAlerts = alerts,
            Comparison = LoadComparison(resultsFolder),
        };
    }

    static FrozenComparison? LoadComparison(string folder)
    {
        foreach (var (file, kind) in new[] { ("B_transfer.json", "transfer"), ("B_compare_runs.json", "next run") })
        {
            var p = Path.Combine(folder, file);
            if (!File.Exists(p)) continue;
            var j = JsonNode.Parse(File.ReadAllText(p))!;
            int E(string k) => j[k]?["errors"]?.GetValue<int>() ?? 0;
            var prev = j["run1"] is JsonValue pv ? pv.GetValue<string>() : j["run1"]?["line"] is not null ? "(previous circuit)" : "";
            var rw = (j["reweighting_on_run2"] as JsonArray ?? [])
                .Select(x => (x!["train_shots"]!.GetValue<int>(), x["errors"]!.GetValue<int>())).ToList();
            var ch = (j["component_changes"] as JsonArray ?? [])
                .Select(x => (x!["component"]!.GetValue<string>(), x["run1"]!.GetValue<double>(), x["run2"]!.GetValue<double>())).ToList();
            int shots = j["test_shots"]?.GetValue<int>() ?? j["shots_run2"]?.GetValue<int>() ?? 0;
            return new FrozenComparison(kind, prev, E("run2_calibration"), E("frozen_syndromics"), E("frozen_syndromics_la"),
                                        shots, rw, ch);
        }
        return null;
    }
}

public static class Names
{
    static readonly Regex Q = new(@"q(\d+)", RegexOptions.Compiled);
    public static int[] QubitsOf(string component) =>
        Q.Matches(component).Select(m => int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture)).ToArray();
    public static string KindOf(string component)
    {
        var i = component.IndexOf(' ');
        return i < 0 ? "component" : component[(i + 1)..];
    }
    public static string Pct(double x) => (x * 100).ToString(x < 0.001 ? "0.000" : "0.00", CultureInfo.InvariantCulture) + "%";
    public static string X(double r) => r.ToString(r >= 10 ? "0" : "0.0", CultureInfo.InvariantCulture) + "×";
    /// <summary>"15×" for a flagged reading, "<2×" for a measured component the fit did not flag.</summary>
    public static string Reading(Run run, string component) =>
        run.IsFlagged(component) ? X(run.RatioOf(component)!.Value) : run.Covers(component) ? "<2×" : "n/a";
}

/// <summary>Finds every kit results folder under a root and loads them in time order.</summary>
public sealed class RunStore
{
    public IReadOnlyList<Run> Runs { get; }
    public string Root { get; }

    public RunStore(string root)
    {
        Root = root;
        var found = Directory.EnumerateFiles(root, "A_validate.json", SearchOption.AllDirectories)
            .Select(Path.GetDirectoryName).OfType<string>()
            .Select(d => Run.Load(d, FolderName(d, root)))
            .OrderBy(r => r.RunAtUtc).ToList();
        Runs = found;
    }

    static string FolderName(string resultsDir, string root)
    {
        var dir = new DirectoryInfo(resultsDir);
        var named = dir.Name.Equals("results", StringComparison.OrdinalIgnoreCase) ? dir.Parent ?? dir : dir;
        return Path.GetFullPath(named.FullName) == Path.GetFullPath(root) ? "run" : named.Name;
    }

    public Run Latest => Runs.Count > 0 ? Runs[^1] : throw new InvalidOperationException("No runs found.");

    public Run? Find(string? key)
    {
        if (string.IsNullOrWhiteSpace(key) || key == "latest") return Runs.LastOrDefault();
        if (int.TryParse(key, out var n) && n >= 1 && n <= Runs.Count) return Runs[n - 1];
        return Runs.FirstOrDefault(r => r.JobId.StartsWith(key, StringComparison.OrdinalIgnoreCase)
                                     || r.Name.Equals(key, StringComparison.OrdinalIgnoreCase));
    }
}
