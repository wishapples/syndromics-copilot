# Syndromics Copilot

[![selftest](https://github.com/wishapples/syndromics-copilot/actions/workflows/selftest.yml/badge.svg)](https://github.com/wishapples/syndromics-copilot/actions/workflows/selftest.yml)

**Ask a quantum computer's noise data questions in plain English, see where it is broken, and get the steps to fix it.**

Syndromics Copilot is the operator side of [Syndromics](https://github.com/wishapples/syndromics). Syndromics measures a
quantum processor's real noise from its own error-correction data. Copilot turns those measurements into answers,
a clickable chip map, repair checklists and alerts, using NVIDIA NIM models for conversation. It is also an
**MCP server**, so Claude Desktop, GitHub Copilot in VS Code, Cursor and other AI apps can use it as a tool.

![Chip map of ibm_kingston: flagged qubits, the tested line, a suggested line, and repair steps for q105](docs/report.png)

Built on real data: four runs on IBM's 156-qubit `ibm_kingston` processor (18–21 November 2026) are included in
`samples/kingston`. Open [docs/example_report.html](docs/example_report.html) in a browser to try the report.

## What it does

| | |
|---|---|
| **Answers questions** | "Which qubits should I avoid today?", "How bad is q129-q128?", "What changed since yesterday?", "Explain it like a manager." |
| **Never makes numbers up** | The AI model calls tools that read the run files; every answer cites the run and job ID. |
| **Shows where the fault is** | A map of the real chip, coloured by how much worse each gate and qubit is than its calibration, plus the circuit that was run with the faulty gate marked. |
| **Says how to fix it** | A repair checklist per component (benchmarks, coherence checks, readout checks, recalibration) and a ready-to-send ticket for the hardware team. |
| **Adapts** | Keeps a health history of every component across runs (new, persistent, worsening, recovered) and remembers the team's preferences: alert thresholds, components to ignore, engineer or manager language. |
| **Watches** | Notices new results, compares them with earlier runs, and posts an alert to the console, a file, or a Slack or Microsoft Teams webhook. |
| **Suggests a better layout** | Finds a connected line of qubits on the real chip that avoids today's flagged qubits. |

Example (offline mode, real data):

```
you › how bad is q129-q128?
copilot › q129-q128 two-qubit gate
run1 (18 Nov, job dbukcjvf): 17× calibration
run2 (19 Nov, job dbvc3lv8): 15× calibration
run3 (19 Nov, job dbvdl8up): 15× calibration
run6 (21 Nov, job dc0os4mq): within 2× of calibration (not flagged)

you › what changed?
copilot › RECOVERED: q129-q128 two-qubit gate 15× → <2×
```

The fault cleared between 19 and 21 November while IBM's published calibration stayed the same snapshot; Copilot
noticed from the syndrome data alone.

## Quick start (Windows, macOS or Linux)

Requires the [.NET 10 SDK](https://dotnet.microsoft.com/download). No other packages.

```
git clone https://github.com/wishapples/syndromics-copilot
cd syndromics-copilot
dotnet run --project src/Syndromics.Copilot.Cli -- brief
dotnet run --project src/Syndromics.Copilot.Cli -- report
dotnet run --project src/Syndromics.Copilot.Cli -- chat
```

`report` writes `samples/kingston/syndromics_report.html`; open it in a browser and click any coloured part of the chip.

### Use NVIDIA NIM for conversation

Get a free API key at [build.nvidia.com](https://build.nvidia.com) (NVIDIA Developer Program; free endpoints are for
development and testing, not production). Then:

```
set NVIDIA_API_KEY=nvapi-...          (Windows)
export NVIDIA_API_KEY=nvapi-...       (macOS / Linux)
dotnet run --project src/Syndromics.Copilot.Cli -- chat
```

`NIM_MODEL` selects the model (default `meta/llama-3.3-70b-instruct`; any NIM chat model with tool calling works).
`NIM_BASE_URL` points to a self-hosted NIM or any OpenAI-compatible endpoint. Keep the key out of code and chats.
Without a key, or if the service is unreachable, Copilot answers from built-in rules, so a demo never fails.

### Use your own runs

Point `--runs` at the folder where you run the [Syndromics IBM demo kit](https://github.com/wishapples/syndromics/blob/main/docs/IBM_DEMO.md);
Copilot finds every `results` folder below it.

```
copilot --runs C:\ibmproject\syndromics-ibm-demo-kit report
copilot --runs C:\ibmproject\syndromics-ibm-demo-kit watch --every 60 --webhook https://hooks.slack.com/...
copilot check --backend ibm_kingston --line 117,105,106,107,108,109,118,129,128
```

`check` runs the kit as a preview; it uses QPU time only with `--confirm-qpu`.

## Commands

| Command | |
|---|---|
| `brief` | Findings, what changed, qubits to avoid and next steps on one screen |
| `report` | The visual HTML report (self-contained; can be emailed) |
| `ask "question"` / `chat` | One question, or a conversation |
| `ticket "q129-q128"` | A ticket draft for the hardware team |
| `watch [--every 30] [--webhook URL] [--once]` | Alerts when new results arrive |
| `check --backend B [--line ...] [--confirm-qpu]` | Runs the kit (preview unless confirmed) |
| `mcp` | Runs as an MCP server over stdio for AI apps (below) |

Options: `--runs`, `--audience engineer|manager`, `--out`, `--memory`.

## Use it from Claude, VS Code, Cursor or any MCP app

[MCP](https://modelcontextprotocol.io) (Model Context Protocol) is the open standard AI assistants use to call tools.
`copilot mcp` exposes all 13 Copilot tools to any MCP client. It reads only your result files: no network access, no
QPU use, no API key needed (the client's own model does the talking).

1. Build once:
   ```
   dotnet publish src/Syndromics.Copilot.Cli -c Release -o C:\syndromics-copilot
   ```
2. Add it to your AI app (use your own paths; on macOS/Linux the program is `copilot` without `.exe`).

   **Claude Desktop** (Settings → Developer → Edit Config, `claude_desktop_config.json`) and **Cursor** (`~/.cursor/mcp.json`):
   ```json
   {
     "mcpServers": {
       "syndromics": {
         "command": "C:\\syndromics-copilot\\copilot.exe",
         "args": ["mcp", "--runs", "C:\\syndromics-copilot-src\\samples\\kingston"]
       }
     }
   }
   ```
   **VS Code** (`.vscode/mcp.json`, used by Copilot Chat in agent mode):
   ```json
   {
     "servers": {
       "syndromics": {
         "type": "stdio",
         "command": "C:\\syndromics-copilot\\copilot.exe",
         "args": ["mcp", "--runs", "C:\\syndromics-copilot-src\\samples\\kingston"]
       }
     }
   }
   ```
3. Restart the app and ask: *"Using Syndromics, which qubits on ibm_kingston should I avoid today, and why?"*

Tools that only read are marked read-only; `remember` and `make_report` write a file in the runs folder. Point
`--runs` at your own kit results to use your own data. Protocol versions 2025-06-18, 2025-03-26 and 2024-11-05;
tested with the official MCP Python SDK client.

## How it works

```
kit results (JSON) ─► RunStore ─► Analyzer ─► Tools ─► Agent ─► answer
                                     │                  │
                                     ├─► Report (HTML)  └─► NVIDIA NIM (tool calling) or offline rules
                                     ├─► Watcher ─► alerts (console, file, webhook)
                                     └─► McpServer ─► any MCP client (Claude, VS Code, Cursor)
```

- `Analyzer` builds each component's history across runs from the Syndromics fits, classifies severity
  (≥8× critical, 3–8× high, 2–3× watch) and trend, and produces suggestions, repair checklists and tickets.
- `Tools` exposes 13 functions to the model (list runs, findings, history, qubits to avoid, alternative line, repair
  checklist, ticket, what changed, suggestions, remember, preferences, report).
- `Agent` runs the tool-calling loop against NVIDIA NIM's OpenAI-compatible API with plain `HttpClient`.
- `McpServer` speaks MCP (JSON-RPC 2.0 over stdio) with only the .NET base library.
- `Memory` is a readable JSON file (`copilot_memory.json`) next to the runs.

## Honest limits

- A repetition code locates a fault to a qubit or pair; it cannot fully separate a gate fault from its neighbouring
  measurement. Copilot says so and its repair checklist covers both.
- Qubits never measured are unknown, not healthy. The suggested line only avoids measured problems.
- Copilot reads results; it does not run hardware unless you use `check --confirm-qpu`.

## Tests

```
dotnet run --project tests/Syndromics.Copilot.SelfTest
```

36 checks on the real Kingston data (including the fault that cleared in run 6), the report, memory, offline
answers, the NIM tool-calling loop against a local mock server, and the MCP protocol. They run on every push (GitHub Actions).

## Licence

MIT. © 2026 Wish Apples Ventures Private Limited. Member of NVIDIA Inception.
