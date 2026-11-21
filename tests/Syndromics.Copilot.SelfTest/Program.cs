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
var store = new RunStore(samples);
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

Console.WriteLine($"\n{passed} passed, {failed} failed");
return failed == 0 ? 0 : 1;
