using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;

namespace Syndromics.Copilot;

/// <summary>Settings for an OpenAI-compatible chat endpoint: NVIDIA NIM by default.</summary>
public sealed record NimSettings(string ApiKey, string Model, string BaseUrl)
{
    public const string DefaultBaseUrl = "https://integrate.api.nvidia.com/v1";
    public const string DefaultModel = "meta/llama-3.3-70b-instruct";

    /// <summary>Reads NVIDIA_API_KEY, NIM_MODEL and NIM_BASE_URL. Returns null when no key is set (offline mode).</summary>
    public static NimSettings? FromEnvironment()
    {
        var key = Environment.GetEnvironmentVariable("NVIDIA_API_KEY");
        if (string.IsNullOrWhiteSpace(key)) return null;
        return new NimSettings(key,
            Environment.GetEnvironmentVariable("NIM_MODEL") is { Length: > 0 } m ? m : DefaultModel,
            Environment.GetEnvironmentVariable("NIM_BASE_URL") is { Length: > 0 } u ? u.TrimEnd('/') : DefaultBaseUrl);
    }
}

/// <summary>Minimal client for /chat/completions with tool calling (no SDK needed).</summary>
public sealed class NimClient
{
    readonly HttpClient _http;
    readonly NimSettings _s;
    public NimClient(NimSettings s, HttpClient? http = null)
    {
        _s = s;
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(90) };
    }

    public async Task<JsonObject> CompleteAsync(JsonArray messages, JsonArray tools, CancellationToken ct = default)
    {
        var body = new JsonObject
        {
            ["model"] = _s.Model,
            ["messages"] = messages.DeepClone(),
            ["tools"] = tools.DeepClone(),
            ["tool_choice"] = "auto",
            ["temperature"] = 0.2,
            ["max_tokens"] = 900,
        };
        using var req = new HttpRequestMessage(HttpMethod.Post, _s.BaseUrl + "/chat/completions")
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _s.ApiKey);
        using var resp = await _http.SendAsync(req, ct);
        var text = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw new HttpRequestException($"NIM returned {(int)resp.StatusCode}: {Trim(text)}");
        return (JsonNode.Parse(text)?["choices"]?[0]?["message"] as JsonObject)
               ?? throw new HttpRequestException("NIM response had no message.");
    }

    static string Trim(string s) => s.Length > 300 ? s[..300] + "…" : s;
}

/// <summary>The Copilot: answers questions about the runs by calling tools. With an NVIDIA NIM key it uses an
/// LLM to plan and phrase answers; without one (or if the call fails) it answers from built-in rules.
/// Either way every fact comes from a tool reading the run files.</summary>
public sealed class Agent
{
    readonly Tools _tools;
    readonly Analyzer _a;
    readonly NimClient? _nim;
    readonly JsonArray _history = new();
    public string Mode => _nim is null ? "offline" : "NVIDIA NIM";
    public string? LastError { get; private set; }

    public Agent(Analyzer analyzer, Tools tools, NimClient? nim)
    {
        _a = analyzer; _tools = tools; _nim = nim;
        _history.Add(new JsonObject { ["role"] = "system", ["content"] = SystemPrompt() });
    }

    string SystemPrompt() =>
        "You are Syndromics Copilot, an assistant for quantum-hardware engineers. Syndromics measures a quantum processor's " +
        "real noise from its own error-correction data (syndromes) and flags components worse than their published calibration " +
        "(ratio to calibration, e.g. 15x). Rules: (1) Get every number, qubit, job id and date from a tool call; never invent or " +
        "estimate one. (2) Cite the run name or job id behind each claim. (3) A repetition code locates a fault to a qubit or pair but " +
        "cannot separate a gate fault from a neighbouring measurement fault; say so when asked about causes. (4) Qubits never " +
        "measured are unknown, not healthy. (5) Never claim to have run hardware or spent QPU time. (6) Keep answers short and " +
        $"practical. Current audience: {_a.Memory.Audience} (manager = plain language, no jargon; engineer = precise numbers). " +
        $"Team preferences: {_a.Memory.Describe()}. Use the remember tool when the user states a preference.";

    public async Task<string> AskAsync(string question, CancellationToken ct = default)
    {
        if (_nim is not null)
        {
            try { return await AskLlmAsync(question, ct); }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException or InvalidOperationException)
            {
                LastError = e.Message;
                return Offline(question) + $"\n\n(NVIDIA NIM unavailable: {e.Message}. Answered from built-in rules.)";
            }
        }
        return Offline(question);
    }

    async Task<string> AskLlmAsync(string question, CancellationToken ct)
    {
        _history[0] = new JsonObject { ["role"] = "system", ["content"] = SystemPrompt() };
        _history.Add(new JsonObject { ["role"] = "user", ["content"] = question });
        var defs = _tools.Definitions();
        for (int step = 0; step < 6; step++)
        {
            var msg = await _nim!.CompleteAsync(_history, defs, ct);
            var calls = msg["tool_calls"] as JsonArray;
            if (calls is null || calls.Count == 0)
            {
                var answer = msg["content"]?.ToString() ?? "";
                _history.Add(new JsonObject { ["role"] = "assistant", ["content"] = answer });
                return answer.Trim();
            }
            _history.Add(new JsonObject { ["role"] = "assistant", ["content"] = msg["content"]?.ToString() ?? "", ["tool_calls"] = calls.DeepClone() });
            foreach (var c in calls)
            {
                var fn = c!["function"]!;
                var result = _tools.Execute(fn["name"]!.ToString(), fn["arguments"]?.ToString() ?? "{}");
                _history.Add(new JsonObject { ["role"] = "tool", ["tool_call_id"] = c["id"]?.ToString() ?? "", ["content"] = result });
            }
        }
        throw new InvalidOperationException("Too many tool steps without an answer.");
    }

    /// <summary>Rule-based answers for offline use and demos without a network.</summary>
    public string Offline(string q)
    {
        var s = q.ToLowerInvariant();
        string T(string name, string args = "{}") => _tools.Execute(name, args);
        string Arg(string k, string v) => new JsonObject { [k] = v }.ToJsonString();
        var comp = _tools.Resolve(q);
        bool namesComponent = Names.QubitsOf(q.Replace("Q", "q")).Length > 0 || System.Text.RegularExpressions.Regex.IsMatch(q, @"\b\d{2,3}\b");

        if (s.StartsWith("ignore ")) return T("remember", new JsonObject { ["key"] = "ignore", ["value"] = _tools.Resolve(q[7..]) }.ToJsonString());
        if (s.Contains("alert") && System.Text.RegularExpressions.Regex.Match(s, @"(\d+(\.\d+)?)\s*(x|×|times)") is { Success: true } m)
            return T("remember", new JsonObject { ["key"] = "alert_threshold", ["value"] = m.Groups[1].Value }.ToJsonString());
        if (s.Contains("manager") || s.Contains("simple") || s.Contains("investor")) { _a.Memory.Set("audience", "manager"); return "From now on I'll explain in plain language.\n\n" + T("run_summary"); }
        if (s.Contains("engineer") || s.Contains("technical") || s.Contains("detail")) { _a.Memory.Set("audience", "engineer"); return "From now on I'll give precise numbers.\n\n" + T("run_summary"); }
        if (s.Contains("ticket")) return T("draft_ticket", Arg("component", comp));
        if (s.Contains("repair") || s.Contains("fix") || s.Contains("cause") || s.Contains("why"))
            return $"{comp}\n" + T("repair_checklist", Arg("component", comp));
        if (s.Contains("history") || s.Contains("trend") || (namesComponent && (s.Contains("how") || s.Contains("bad"))))
            return T("component_history", Arg("component", comp));
        if (s.Contains("avoid") || s.Contains("which qubit") || s.Contains("healthy"))
            return T("qubits_to_avoid") + "\n\nAlternative line: " + T("alternative_line");
        if (s.Contains("line") || s.Contains("layout") || s.Contains("place")) return T("alternative_line");
        if (s.Contains("change") || s.Contains("new") || s.Contains("drift") || s.Contains("today")) return T("what_changed");
        if (s.Contains("report") || s.Contains("map") || s.Contains("visual") || s.Contains("show")) return T("make_report");
        if (s.Contains("suggest") || s.Contains("next") || s.Contains("should") || s.Contains("recommend")) return T("suggestions");
        if (s.Contains("fault") || s.Contains("problem") || s.Contains("worst") || s.Contains("finding") || s.Contains("wrong")) return T("findings");
        if (s.Contains("runs") || s.Contains("list")) return T("list_runs");
        if (s.Contains("preference") || s.Contains("remember")) return T("show_preferences");
        if (System.Text.RegularExpressions.Regex.Match(s, @"run\s*(\d+)") is { Success: true } r) return T("run_summary", Arg("run", r.Groups[1].Value));
        if (s.Contains("summary") || s.Contains("result") || s.Contains("latest") || s.Contains("status")) return T("run_summary");
        return "I can answer: what's wrong with the chip, which qubits to avoid, history of a component (e.g. 'how bad is q129-q128'), " +
               "what changed since the last run, how to repair a component, draft a ticket, suggest next steps, or 'show the report'. " +
               "Say 'explain like a manager' or 'like an engineer' to switch style.";
    }
}
