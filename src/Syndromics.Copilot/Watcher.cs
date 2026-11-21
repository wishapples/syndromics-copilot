using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;

namespace Syndromics.Copilot;

/// <summary>Watches a folder for new kit results and raises alerts when something is new or worse.</summary>
public sealed class Watcher
{
    readonly string _root;
    readonly Memory _memory;
    readonly string? _webhook;
    readonly Action<string> _say;

    public Watcher(string root, Memory memory, string? webhook, Action<string> say)
    { _root = root; _memory = memory; _webhook = webhook; _say = say; }

    /// <summary>Checks once. Returns the alert text, or null if no new run appeared.</summary>
    public async Task<string?> CheckOnceAsync(CancellationToken ct = default)
    {
        var store = new RunStore(_root);
        var fresh = store.Runs.Where(r => !_memory.SeenJobs.Contains(r.JobId)).ToList();
        if (fresh.Count == 0) return null;
        var a = new Analyzer(store, ChipMap.Kingston(), _memory);
        var sb = new StringBuilder($"Syndromics Copilot: {fresh.Count} new run(s) on {store.Latest.Backend}: {string.Join(", ", fresh.Select(r => r.Name + " job " + r.ShortJob))}.\n");
        var important = a.WhatChanged().Where(l => !l.StartsWith("No ") && !l.StartsWith("Only ")).ToList();
        foreach (var l in important) sb.AppendLine("• " + l);
        var avoid = a.QubitsToAvoid();
        if (avoid.Count > 0) sb.AppendLine($"Avoid today: {string.Join(", ", avoid.Select(x => "q" + x.Qubit))}.");
        foreach (var s in a.Suggestions().Take(2)) sb.AppendLine("→ " + s);
        var text = sb.ToString().TrimEnd();

        foreach (var r in fresh) _memory.SeenJobs.Add(r.JobId);
        _memory.Save();
        File.AppendAllText(Path.Combine(_root, "copilot_alerts.md"), $"\n## {DateTime.UtcNow:yyyy-MM-dd HH:mm} UTC\n{text}\n");
        _say(text);
        if (!string.IsNullOrWhiteSpace(_webhook)) await PostAsync(_webhook, text, ct);
        return text;
    }

    /// <summary>Posts {"text": ...}: the format Slack and Microsoft Teams incoming webhooks accept.</summary>
    static async Task PostAsync(string url, string text, CancellationToken ct)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
            var body = new JsonObject { ["text"] = text }.ToJsonString();
            using var resp = await http.PostAsync(url, new StringContent(body, Encoding.UTF8, "application/json"), ct);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException) { Console.Error.WriteLine($"Webhook failed: {e.Message}"); }
    }

    public async Task RunAsync(TimeSpan every, CancellationToken ct)
    {
        _say($"Watching {Path.GetFullPath(_root)} every {every.TotalMinutes:0.#} min. Ctrl+C to stop.");
        while (!ct.IsCancellationRequested)
        {
            try { await CheckOnceAsync(ct); }
            catch (Exception e) when (e is IOException or InvalidOperationException or System.Text.Json.JsonException) { _say($"Skipped a check: {e.Message}"); }
            try { await Task.Delay(every, ct); } catch (TaskCanceledException) { break; }
        }
    }
}

/// <summary>Runs the Syndromics IBM demo kit. Without explicit confirmation it only previews (no QPU time is used).</summary>
public static class KitRunner
{
    public static async Task<int> ValidateAsync(string workDir, string backend, string? line, bool confirmQpu, Action<string> say)
    {
        var args = $"validate --backend {backend}" + (line is null ? "" : $" --line {line}") + (confirmQpu ? " --yes" : "");
        say(confirmQpu ? $"Submitting to {backend} (uses QPU time): syndromics-ibm-demo {args}"
                       : $"Preview only, nothing submitted: syndromics-ibm-demo {args}");
        Directory.CreateDirectory(workDir);
        var psi = new ProcessStartInfo("syndromics-ibm-demo", args)
        { WorkingDirectory = workDir, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        try
        {
            using var p = Process.Start(psi)!;
            p.OutputDataReceived += (_, e) => { if (e.Data is not null) say(e.Data); };
            p.ErrorDataReceived += (_, e) => { if (e.Data is not null) say(e.Data); };
            p.BeginOutputReadLine(); p.BeginErrorReadLine();
            await p.WaitForExitAsync();
            return p.ExitCode;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            say("Could not start 'syndromics-ibm-demo'. Activate the kit's Python environment first (syndromics-env\\Scripts\\activate).");
            return 127;
        }
    }
}
