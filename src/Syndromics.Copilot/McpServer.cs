using System.Text.Json;
using System.Text.Json.Nodes;

namespace Syndromics.Copilot;

/// <summary>
/// Model Context Protocol server over stdio (newline-delimited JSON-RPC 2.0). It exposes the Copilot tools to any
/// MCP client: Claude Desktop, VS Code, Cursor and others. Only JSON-RPC goes to the output stream; logs go to the
/// log writer (stderr). No network access and no QPU use: every tool reads the kit's result files.
/// </summary>
public sealed class McpServer
{
    public static readonly string[] SupportedVersions = ["2025-06-18", "2025-03-26", "2024-11-05"];
    static readonly HashSet<string> Writes = ["remember", "make_report"];

    const string Instructions =
        "Syndromics reads a quantum chip's real noise from its error-correction data (IBM Quantum hardware runs). " +
        "Quote numbers only from tool results. A ratio is 'times the published calibration'. Repetition codes see " +
        "bit-flip errors only; say so when it matters. Small error counts are uncertain: give counts, not only rates.";

    readonly Func<Tools> _tools;
    readonly TextWriter _log;
    readonly string _version;

    /// <param name="tools">Called for each tool call, so new runs in the results folder are picked up without a restart.</param>
    public McpServer(Func<Tools> tools, TextWriter log, string version = "0.2.0")
    {
        _tools = tools; _log = log; _version = version;
    }

    public async Task RunAsync(TextReader input, TextWriter output, CancellationToken ct = default)
    {
        while (!ct.IsCancellationRequested && await input.ReadLineAsync(ct) is { } line)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var reply = Handle(line);
            if (reply is null) continue;
            await output.WriteLineAsync(reply);
            await output.FlushAsync(ct);
        }
    }

    /// <summary>Handles one JSON-RPC message; returns the reply line, or null for notifications.</summary>
    public string? Handle(string line)
    {
        JsonObject msg;
        try { msg = JsonNode.Parse(line) as JsonObject ?? throw new JsonException("not an object"); }
        catch (JsonException e) { return Error(null, -32700, "Parse error: " + e.Message); }

        var id = msg["id"]?.DeepClone();
        var method = msg["method"]?.GetValue<string>();
        if (method is null) return id is null ? null : Error(id, -32600, "Invalid request: no method");
        var p = msg["params"] as JsonObject ?? new JsonObject();

        if (id is null) // notification: never answered
        {
            _log.WriteLine($"[mcp] notification {method}");
            return null;
        }

        try
        {
            return method switch
            {
                "initialize" => Result(id, Initialize(p)),
                "ping" => Result(id, new JsonObject()),
                "tools/list" => Result(id, new JsonObject { ["tools"] = ListTools() }),
                "tools/call" => Result(id, CallTool(p)),
                "resources/list" => Result(id, new JsonObject { ["resources"] = new JsonArray() }),
                "prompts/list" => Result(id, new JsonObject { ["prompts"] = new JsonArray() }),
                _ => Error(id, -32601, $"Method not found: {method}"),
            };
        }
        catch (Exception e)
        {
            _log.WriteLine($"[mcp] {method} failed: {e}");
            return Error(id, -32603, "Internal error: " + e.Message);
        }
    }

    JsonObject Initialize(JsonObject p)
    {
        var asked = p["protocolVersion"]?.GetValue<string>();
        var version = asked is not null && SupportedVersions.Contains(asked) ? asked : SupportedVersions[0];
        _log.WriteLine($"[mcp] initialize: client asked {asked ?? "none"}, using {version}");
        return new JsonObject
        {
            ["protocolVersion"] = version,
            ["capabilities"] = new JsonObject { ["tools"] = new JsonObject { ["listChanged"] = false } },
            ["serverInfo"] = new JsonObject { ["name"] = "syndromics-copilot", ["title"] = "Syndromics Copilot", ["version"] = _version },
            ["instructions"] = Instructions,
        };
    }

    JsonArray ListTools()
    {
        var list = new JsonArray();
        foreach (var d in _tools().Definitions())
        {
            var f = d!["function"]!;
            var name = f["name"]!.GetValue<string>();
            list.Add(new JsonObject
            {
                ["name"] = name,
                ["description"] = f["description"]!.GetValue<string>(),
                ["inputSchema"] = f["parameters"]!.DeepClone(),
                ["annotations"] = new JsonObject
                {
                    ["readOnlyHint"] = !Writes.Contains(name),
                    ["destructiveHint"] = false,
                    ["openWorldHint"] = false,
                },
            });
        }
        return list;
    }

    JsonObject CallTool(JsonObject p)
    {
        var name = p["name"]?.GetValue<string>() ?? "";
        var tools = _tools();
        var known = tools.Definitions().Any(d => d!["function"]!["name"]!.GetValue<string>() == name);
        var args = p["arguments"]?.ToJsonString() ?? "{}";
        _log.WriteLine($"[mcp] call {name} {args}");
        var text = known ? tools.Execute(name, args) : $"Unknown tool '{name}'.";
        var isError = !known || text.StartsWith("Tool error:", StringComparison.Ordinal);
        return new JsonObject
        {
            ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text }),
            ["isError"] = isError,
        };
    }

    static string Result(JsonNode id, JsonNode result) =>
        new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = result }.ToJsonString();

    static string Error(JsonNode? id, int code, string message) =>
        new JsonObject
        {
            ["jsonrpc"] = "2.0", ["id"] = id,
            ["error"] = new JsonObject { ["code"] = code, ["message"] = message },
        }.ToJsonString();
}
