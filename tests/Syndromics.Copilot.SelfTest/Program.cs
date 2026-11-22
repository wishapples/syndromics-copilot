// Self-test with no external packages: checks the analysis on the three real ibm_kingston runs, the report,
// memory, the offline answers, and the NVIDIA NIM tool-calling loop against a local mock server.
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Syndromics.Copilot;

int failed = 0, passed = 0;
void Check(bool ok, string what)
{
    if (ok) { passed++; Console.WriteLine($"  ok   {what}"); }
    else { failed++; Console.ForegroundColor = ConsoleColor.Red; Console.WriteLine($"  FAIL {what}"); Console.ResetColor(); }
}

string samples = new[] { "samples/kingston", "../samples/kingston", "../../samples/kingston", "../../../samples/kingston", "../../../../samples/kingston" }
    .First(Directory.Exists);
// Most checks describe the state after run 3 (19 Nov); run 6 is checked separately at the end.
string upTo3 = Path.Combine(Path.GetTempPath(), "copilot_selftest_runs1to3");
if (Directory.Exists(upTo3)) Directory.Delete(upTo3, true);
foreach (var r in new[] { "run1", "run2", "run3" })
    foreach (var f in Directory.GetFiles(Path.Combine(samples, r, "results")))
    {
        Directory.CreateDirectory(Path.Combine(upTo3, r, "results"));
        File.Copy(f, Path.Combine(upTo3, r, "results", Path.GetFileName(f)));
    }
var store = new RunStore(upTo3);
var memory = Memory.InMemory();
var a = new Analyzer(store, ChipMap.Kingston(), memory);
var tools = new Tools(a, Path.Combine(Path.GetTempPath(), "copilot_selftest_report.html"));

Console.WriteLine("Runs and findings (real ibm_kingston data)");
Check(store.Runs.Count == 3, "three runs loaded in time order");
Check(store.Runs[0].JobId == "dbukcjvf8rhs73bmf9l0" && store.Latest.JobId == "dbvdl8ups3fc73bkad70", "job ids and order");
var worst = a.Findings()[0];
Check(worst.Component == "q129-q128 two-qubit gate" && worst.Severity == Severity.Critical, "worst finding is the q129-q128 gate, critical");
Check(worst.TimesFlagged == 3 && worst.Trend == Trend.Persistent, "flagged in all three runs, persistent");
Check(a.Findings().First(f => f.Component == "q108-q109 two-qubit gate").Trend == Trend.Resolved, "q108-q109 gate recovered after run 1");
Check(!a.WhatChanged().Any(l => l.Contains("q108-q109")), "no false 'new' alarm from per-window fits");
var avoid = a.QubitsToAvoid().Select(x => x.Qubit).ToHashSet();
Check(avoid.SetEquals(new[] { 105, 108, 109, 128, 129 }), "qubits to avoid: 105, 108, 109, 128, 129");
var alt = a.AlternativeLine(9);
Check(alt is not null && alt.Length == 9 && !alt.Any(avoid.Contains), "alternative 9-qubit line avoids every flagged qubit");
Check(alt is not null && alt.Zip(alt.Skip(1)).All(p => a.Map.Connected(p.First, p.Second)), "alternative line is connected on the real chip");
Check(store.Latest.Comparison is { Kind: "transfer", FrozenErrors: 69, CalibrationErrors: 150 }, "transfer result read from run 3");

Console.WriteLine("Tools, memory and offline answers");
Check(tools.Resolve("the 128 gate") == "q129-q128 two-qubit gate", "loose names resolve ('the 128 gate')");
Check(tools.Execute("component_history", "{\"component\":\"q129-q128\"}").Contains("17×"), "history cites run 1's 17×");
Check(a.Ticket("q129-q128 two-qubit gate").Contains("dbvc3lv82tmc73dupa7g"), "ticket cites the job ids");
memory.Set("ignore", "q105 measurement");
Check(!a.QubitsToAvoid().Any(x => x.Qubit == 105), "ignoring a component removes it from the avoid list");
memory.Set("unignore", "q105 measurement");
var agent = new Agent(a, tools, null);
Check((await agent.AskAsync("which qubits should I avoid today?")).Contains("q129"), "offline: which qubits to avoid");
Check((await agent.AskAsync("how do I fix q129-q128?")).Contains("randomized benchmarking"), "offline: repair steps");
Check((await agent.AskAsync("alert me above 5x")).Contains("alert_threshold") && memory.AlertThreshold == 5, "offline: remembers an alert threshold");
memory.AlertThreshold = 3;

Console.WriteLine("Report");
var html = Report.Build(a);
Check(html.Contains("<svg") && html.Contains("show('q129-q128 two-qubit gate')"), "report has the chip map with a clickable fault");
Check(html.Contains("repeat × 20 rounds"), "report draws the run-3 circuit");
Check(!html.Contains("http://") && !html.Contains("<script src"), "report is self-contained (no external files)");

Console.WriteLine("NVIDIA NIM tool-calling loop (local mock server)");
var port = Random.Shared.Next(40000, 50000);
using var listener = new HttpListener();
listener.Prefixes.Add($"http://127.0.0.1:{port}/");
listener.Start();
var seen = new List<JsonObject>();
var server = Task.Run(async () =>
{
    for (int i = 0; i < 2; i++)
    {
        var ctx = await listener.GetContextAsync();
        var body = JsonNode.Parse(new StreamReader(ctx.Request.InputStream).ReadToEnd())!.AsObject();
        seen.Add(body);
        JsonObject message = i == 0
            ? new() { ["role"] = "assistant", ["content"] = "", ["tool_calls"] = new JsonArray(new JsonObject
                { ["id"] = "call_1", ["type"] = "function", ["function"] = new JsonObject { ["name"] = "qubits_to_avoid", ["arguments"] = "{}" } }) }
            : new() { ["role"] = "assistant", ["content"] = "Avoid q128 and q129: the gate between them is 15× its calibration (run3)." };
        var resp = Encoding.UTF8.GetBytes(new JsonObject { ["choices"] = new JsonArray(new JsonObject { ["message"] = message }) }.ToJsonString());
        ctx.Response.ContentType = "application/json";
        await ctx.Response.OutputStream.WriteAsync(resp);
        ctx.Response.Close();
    }
});
var nimAgent = new Agent(a, tools, new NimClient(new NimSettings("test-key", "test-model", $"http://127.0.0.1:{port}")));
var answer = await nimAgent.AskAsync("Which qubits should I avoid?");
await server;
Check(answer.Contains("q129"), "final answer returned after a tool call");
Check(seen.Count == 2 && seen[0]["tools"] is JsonArray { Count: > 5 }, "tool definitions sent to the model");
var toolMsg = (seen[1]["messages"] as JsonArray)!.Last()!;
Check(toolMsg["role"]!.ToString() == "tool" && toolMsg["content"]!.ToString().Contains("q129"), "tool result (real data) sent back to the model");

// Run 6 (21 Nov): the q129-q128 fault cleared; the transfer verdict must not claim a win it did not get.
var all = new Analyzer(new RunStore(samples), ChipMap.Kingston(), Memory.InMemory());
Check(all.Store.Runs.Count == 4 && all.Store.Latest.JobId == "dc0os4mq4uuc73a2u790", "run 6 loaded as the latest run");
Check(all.Findings(includeIgnored: true).First(f => f.Component == "q129-q128 two-qubit gate").Trend == Trend.Resolved, "q129-q128 fault shown as resolved after run 6");
Check(all.WhatChanged().Any(l => l.StartsWith("RECOVERED: q129-q128")), "what changed reports the recovery");
Check(Analyzer.TransferVerdict(136, 123, 15000).Contains("about the same") && Analyzer.TransferVerdict(200, 120, 15000).Contains("does better") && Analyzer.TransferVerdict(40, 39, 15000).Contains("about the same")
      && Analyzer.TransferVerdict(69, 77, 15000).Contains("about the same") && Analyzer.TransferVerdict(30, 77, 15000).Contains("better than"), "transfer verdict is honest");

// MCP server: the protocol a Claude Desktop / VS Code / Cursor client speaks.
var mcpLog = new StringWriter();
var mcp = new McpServer(() => new Tools(all, Path.Combine(Path.GetTempPath(), "copilot_selftest_mcp.html")), mcpLog);
JsonObject Rpc(string line) => (JsonObject)JsonNode.Parse(mcp.Handle(line)!)!;
var init = Rpc("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":{\"protocolVersion\":\"2025-03-26\",\"capabilities\":{}}}");
Check(init["result"]!["protocolVersion"]!.ToString() == "2025-03-26" && init["result"]!["capabilities"]!["tools"] is not null, "mcp: initialize negotiates the client's version");
Check(Rpc("{\"jsonrpc\":\"2.0\",\"id\":9,\"method\":\"initialize\",\"params\":{\"protocolVersion\":\"1999-01-01\"}}")["result"]!["protocolVersion"]!.ToString() == McpServer.SupportedVersions[0], "mcp: unknown version falls back to the latest");
Check(mcp.Handle("{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\"}") is null, "mcp: notifications get no reply");
var listed = (JsonArray)Rpc("{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"tools/list\"}")["result"]!["tools"]!;
Check(listed.Count == 13 && listed.All(t => t!["inputSchema"]!["type"]!.ToString() == "object"), "mcp: 13 tools with input schemas");
Check(listed.First(t => t!["name"]!.ToString() == "remember")!["annotations"]!["readOnlyHint"]!.GetValue<bool>() == false
      && listed.First(t => t!["name"]!.ToString() == "findings")!["annotations"]!["readOnlyHint"]!.GetValue<bool>(), "mcp: read-only hints");
var called = Rpc("{\"jsonrpc\":\"2.0\",\"id\":\"a\",\"method\":\"tools/call\",\"params\":{\"name\":\"what_changed\",\"arguments\":{}}}");
Check(called["id"]!.ToString() == "a" && called["result"]!["isError"]!.GetValue<bool>() == false
      && called["result"]!["content"]![0]!["text"]!.ToString().Contains("RECOVERED: q129-q128"), "mcp: tools/call returns real findings, string id kept");
Check(Rpc("{\"jsonrpc\":\"2.0\",\"id\":3,\"method\":\"tools/call\",\"params\":{\"name\":\"nope\"}}")["result"]!["isError"]!.GetValue<bool>(), "mcp: unknown tool is an error result");
Check(Rpc("{\"jsonrpc\":\"2.0\",\"id\":4,\"method\":\"bogus\"}")["error"]!["code"]!.GetValue<int>() == -32601, "mcp: unknown method -32601");
Check(Rpc("not json")["error"]!["code"]!.GetValue<int>() == -32700, "mcp: parse error -32700");

Console.WriteLine($"\n{passed} passed, {failed} failed");
return failed == 0 ? 0 : 1;
