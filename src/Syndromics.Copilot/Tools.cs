using System.Text.Json;
using System.Text.Json.Nodes;

namespace Syndromics.Copilot;

/// <summary>The functions the AI model may call. Each one reads the run files; the model never sees raw guesses.</summary>
public sealed class Tools
{
    readonly Analyzer _a;
    readonly string _reportPath;
    public Tools(Analyzer a, string reportPath) { _a = a; _reportPath = reportPath; }

    static JsonObject Fn(string name, string description, JsonObject? props = null, params string[] required) => new()
    {
        ["type"] = "function",
        ["function"] = new JsonObject
        {
            ["name"] = name,
            ["description"] = description,
            ["parameters"] = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = props ?? new JsonObject(),
                ["required"] = new JsonArray(required.Select(r => (JsonNode)r).ToArray()),
            },
        },
    };

    static JsonObject Str(string d) => new() { ["type"] = "string", ["description"] = d };
    static JsonObject Num(string d) => new() { ["type"] = "number", ["description"] = d };

    public JsonArray Definitions() =>
    [
        Fn("list_runs", "List every hardware run Copilot has, oldest first, with job id, date, qubits and circuit."),
        Fn("run_summary", "Summary of one run: decoding results, flagged components, erratic shots, drift alerts.",
           new JsonObject { ["run"] = Str("Run number (1 = oldest), job id prefix, folder name, or 'latest'.") }),
        Fn("findings", "Every flagged component across all runs, worst first, with severity, trend and history."),
        Fn("component_history", "Ratio to calibration of one component in every run that measured it.",
           new JsonObject { ["component"] = Str("Component name, e.g. 'q129-q128 two-qubit gate' or 'q107 measurement'.") }, "component"),
        Fn("qubits_to_avoid", "Qubits to keep out of today's layouts, with the reason for each.",
           new JsonObject { ["threshold"] = Num("Optional ratio to calibration; default is the team's alert threshold.") }),
        Fn("alternative_line", "A connected line of qubits on the chip that avoids today's flagged qubits.",
           new JsonObject { ["length"] = Num("Number of qubits; default the length of the latest line.") }),
        Fn("repair_checklist", "Diagnostic and repair steps an engineer can take for one component.",
           new JsonObject { ["component"] = Str("Component name.") }, "component"),
        Fn("draft_ticket", "A ready-to-send ticket for the hardware team about one component.",
           new JsonObject { ["component"] = Str("Component name.") }, "component"),
        Fn("what_changed", "What changed between the two most recent runs."),
        Fn("suggestions", "Recommended next steps, most useful first."),
        Fn("remember", "Store a team preference: ignore/unignore a component, alert_threshold, audience (engineer or manager), or a note.",
           new JsonObject { ["key"] = Str("ignore, unignore, alert_threshold, audience, or any note name."), ["value"] = Str("The value.") }, "key", "value"),
        Fn("show_preferences", "Show what Copilot remembers about the team's preferences."),
        Fn("make_report", "Write the visual HTML report (chip map, circuit, history charts, repair steps) and return its path."),
    ];

    public string Execute(string name, string argsJson)
    {
        JsonObject args;
        try { args = JsonNode.Parse(string.IsNullOrWhiteSpace(argsJson) ? "{}" : argsJson) as JsonObject ?? new(); }
        catch (JsonException) { args = new(); }
        string? S(string k) => args[k]?.ToString();
        double? D(string k) => double.TryParse(args[k]?.ToString(), System.Globalization.NumberStyles.Float,
                                               System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : null;
        try
        {
            return name switch
            {
                "list_runs" => string.Join("\n", _a.Store.Runs.Select((r, i) =>
                    $"{i + 1}. {r.Label}, {r.Backend}, job {r.JobId}, line {string.Join(",", r.Line)}{(r.Simulated ? " (SIMULATED)" : "")}")),
                "run_summary" => _a.Store.Find(S("run")) is { } run ? _a.Summary(run, _a.Memory.Audience) : $"No run matches '{S("run")}'.",
                "findings" => string.Join("\n", _a.Findings().Select(f => f.Trend == Trend.Resolved
                    ? $"{f.Component}: back to normal (<2× calibration); history {_a.HistoryText(f)}"
                    : $"{f.Component}: {Names.X(f.LatestRatio)} calibration, {f.Severity}, {Analyzer.Describe(f.Trend)}; history {_a.HistoryText(f)}")),
                "component_history" => History(S("component") ?? ""),
                "qubits_to_avoid" => _a.QubitsToAvoid(D("threshold")) is { Count: > 0 } av
                    ? string.Join("\n", av.Select(x => $"q{x.Qubit}: {x.Reason}")) : "No qubit is above the threshold.",
                "alternative_line" => _a.AlternativeLine((int?)D("length")) is { } l
                    ? $"{string.Join(",", l)} (avoids flagged qubits; qubits never measured are not known to be healthy)"
                    : "No line found that avoids every flagged qubit.",
                "repair_checklist" => string.Join("\n", Analyzer.RepairChecklist(Resolve(S("component") ?? "")).Select((s, i) => $"{i + 1}. {s}")),
                "draft_ticket" => _a.Ticket(Resolve(S("component") ?? "")),
                "what_changed" => string.Join("\n", _a.WhatChanged()),
                "suggestions" => string.Join("\n", _a.Suggestions().Select((s, i) => $"{i + 1}. {s}")),
                "remember" => _a.Memory.Set(S("key") ?? "note", S("value") ?? ""),
                "show_preferences" => _a.Memory.Describe(),
                "make_report" => $"Report written to {Report.Write(_a, _reportPath)}",
                _ => $"Unknown tool '{name}'.",
            };
        }
        catch (Exception e) { return $"Tool error: {e.Message}"; }
    }

    /// <summary>Accepts loose names ("q129-q128", "the 128 gate") and returns the closest component name.</summary>
    public string Resolve(string text)
    {
        var all = _a.Findings(includeIgnored: true).Select(f => f.Component).ToList();
        var exact = all.FirstOrDefault(c => c.Equals(text, StringComparison.OrdinalIgnoreCase));
        if (exact is not null) return exact;
        var qs = Names.QubitsOf(text.Replace("Q", "q"));
        if (qs.Length == 0) qs = System.Text.RegularExpressions.Regex.Matches(text, @"\d{2,3}").Select(m => int.Parse(m.Value)).ToArray();
        bool wantsGate = text.Contains("gate", StringComparison.OrdinalIgnoreCase) || qs.Length >= 2;
        var match = all.Where(c => qs.All(q => Names.QubitsOf(c).Contains(q)))
                       .OrderByDescending(c => c.Contains("gate") == wantsGate).FirstOrDefault();
        return match ?? text;
    }

    string History(string text)
    {
        var c = Resolve(text);
        var rows = _a.Store.Runs.Where(r => r.Covers(c)).Select(r => $"{r.Name} ({r.RunAtUtc:dd MMM}, job {r.ShortJob}): " +
                                                                    (r.IsFlagged(c) ? Names.Reading(r, c) + " calibration" : "within 2× of calibration (not flagged)"));
        var s = string.Join("\n", rows);
        return s.Length > 0 ? $"{c}\n{s}" : $"No run measured '{text}'.";
    }
}
