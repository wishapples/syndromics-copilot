using System.Text;
using Syndromics.Copilot;

Console.OutputEncoding = Encoding.UTF8;

var argv = args.ToList();
string? Opt(string name, string? def = null)
{
    var i = argv.IndexOf(name);
    if (i < 0 || i + 1 >= argv.Count) return def;
    var v = argv[i + 1]; argv.RemoveRange(i, 2); return v;
}
bool Flag(string name) => argv.Remove(name);

var runs = Opt("--runs", Environment.GetEnvironmentVariable("SYNDROMICS_RUNS") ?? DefaultRuns())!;
var reportPath = Opt("--out", Path.Combine(runs, "syndromics_report.html"))!;
var memoryPath = Opt("--memory", Path.Combine(runs, "copilot_memory.json"))!;
var audience = Opt("--audience");
var command = argv.Count > 0 ? argv[0].ToLowerInvariant() : "help";
var rest = string.Join(' ', argv.Skip(1)).Trim();

static string DefaultRuns()
{
    foreach (var dir in new[] { "runs", "samples/kingston", "../samples/kingston", "../../samples/kingston", "../../../samples/kingston" })
        if (Directory.Exists(dir)) return dir;
    // An MCP client may start us from any folder: also look upwards from the program's own folder.
    for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
        if (Directory.Exists(Path.Combine(d.FullName, "samples", "kingston"))) return Path.Combine(d.FullName, "samples", "kingston");
    return ".";
}

if (command is "help" or "--help" or "-h")
{
    Console.WriteLine("""
Syndromics Copilot: ask your quantum processor's noise data questions in plain English.

  copilot report                 write the visual report (chip map, circuit, history, repair steps)
  copilot ask "question"         answer one question, e.g. ask "which qubits should I avoid today?"
  copilot chat                   interactive conversation
  copilot brief                  findings, what changed and next steps, in one screen
  copilot ticket "q129-q128"     draft a ticket for the hardware team
  copilot watch [--every 30] [--webhook URL] [--once]   alert when new kit results appear
  copilot mcp                    run as an MCP server (stdio) for Claude Desktop, VS Code, Cursor and other AI apps
  copilot check --backend ibm_kingston [--line 117,105,...] [--confirm-qpu]
                                 run the kit (preview only unless --confirm-qpu)

Options: --runs <folder with kit results> (default: samples/kingston), --audience engineer|manager,
         --out report.html, --memory memory.json
AI: set NVIDIA_API_KEY to use NVIDIA NIM (free for development at build.nvidia.com);
    NIM_MODEL to choose a model (default meta/llama-3.3-70b-instruct). Without a key it answers offline.
""");
    return 0;
}

if (command == "check")
{
    var backend = Opt("--backend", "ibm_kingston")!;
    var line = Opt("--line");
    var confirm = Flag("--confirm-qpu");
    var dir = Path.Combine(runs, $"run_{DateTime.UtcNow:yyyyMMdd_HHmm}");
    return await KitRunner.ValidateAsync(dir, backend, line, confirm, Console.WriteLine);
}

if (!Directory.Exists(runs)) { Console.Error.WriteLine($"Runs folder not found: {runs}"); return 2; }
var memory = Memory.Load(memoryPath);
if (audience is not null) memory.Set("audience", audience);

if (command == "watch")
{
    var every = TimeSpan.FromMinutes(double.Parse(Opt("--every", "30")!, System.Globalization.CultureInfo.InvariantCulture));
    var hook = Opt("--webhook", Environment.GetEnvironmentVariable("SYNDROMICS_WEBHOOK"));
    if (Flag("--once"))
    {
        var w = new Watcher(runs, memory, hook, Console.WriteLine);
        if (await w.CheckOnceAsync() is null) Console.WriteLine("No new runs since the last check.");
        return 0;
    }
    using var cts = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
    await new Watcher(runs, memory, hook, Console.WriteLine).RunAsync(every, cts.Token);
    return 0;
}

if (command == "mcp")
{
    // stdout carries only JSON-RPC; everything else goes to stderr.
    var full = Path.GetFullPath(runs);
    if (new RunStore(full).Runs.Count == 0) Console.Error.WriteLine($"[mcp] warning: no kit results under {full}");
    Tools Fresh() => new(new Analyzer(new RunStore(full), ChipMap.Kingston(), memory), Path.GetFullPath(reportPath));
    var stdout = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true };
    var stdin = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false));
    Console.Error.WriteLine($"[mcp] Syndromics Copilot MCP server, runs: {full}");
    await new McpServer(Fresh, Console.Error).RunAsync(stdin, stdout);
    return 0;
}

var store = new RunStore(runs);
if (store.Runs.Count == 0) { Console.Error.WriteLine($"No kit results (A_validate.json) under {Path.GetFullPath(runs)}."); return 2; }
var analyzer = new Analyzer(store, ChipMap.Kingston(), memory);
var tools = new Tools(analyzer, reportPath);
var nim = NimSettings.FromEnvironment() is { } s ? new NimClient(s) : null;
var agent = new Agent(analyzer, tools, nim);

switch (command)
{
    case "report":
        Console.WriteLine($"Report written to {Report.Write(analyzer, reportPath)}");
        Console.WriteLine("Open it in a browser: click any coloured part of the chip for its history and repair steps.");
        return 0;
    case "brief":
        Console.WriteLine(Header());
        Console.WriteLine(tools.Execute("run_summary", "{}"));
        Console.WriteLine("\nWhat changed:\n" + tools.Execute("what_changed", "{}"));
        Console.WriteLine("\nQubits to avoid:\n" + tools.Execute("qubits_to_avoid", "{}"));
        Console.WriteLine("\nNext steps:\n" + tools.Execute("suggestions", "{}"));
        return 0;
    case "ticket":
        Console.WriteLine(analyzer.Ticket(tools.Resolve(rest)));
        return 0;
    case "ask":
        if (rest.Length == 0) { Console.Error.WriteLine("Usage: copilot ask \"your question\""); return 2; }
        Console.WriteLine(await agent.AskAsync(rest));
        return 0;
    case "chat":
        Console.WriteLine(Header());
        Console.WriteLine("Ask anything about the runs. Try: \"what's wrong with the chip?\", \"which qubits should I avoid?\",");
        Console.WriteLine("\"how do I fix q129-q128?\", \"explain like a manager\", \"show the report\". Type 'exit' to quit.\n");
        while (true)
        {
            Console.ForegroundColor = ConsoleColor.Cyan; Console.Write("you › "); Console.ResetColor();
            var q = Console.ReadLine();
            if (q is null || q.Trim() is "exit" or "quit") break;
            if (q.Trim().Length == 0) continue;
            var answer = await agent.AskAsync(q);
            Console.ForegroundColor = ConsoleColor.Green; Console.Write("copilot › "); Console.ResetColor();
            Console.WriteLine(answer + "\n");
        }
        return 0;
    default:
        Console.Error.WriteLine($"Unknown command '{command}'. Run 'copilot help'.");
        return 2;
}

string Header() =>
    $"Syndromics Copilot · {store.Latest.Backend} · {store.Runs.Count} run(s), latest {store.Latest.RunAtUtc:dd MMM yyyy} · AI: {agent.Mode}" +
    (nim is null ? " (set NVIDIA_API_KEY for NVIDIA NIM)" : "") + $" · audience: {memory.Audience}";
