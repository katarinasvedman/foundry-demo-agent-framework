# Foundry Demo — Run Summary

Date: 2025-09-16

This document summarizes the recent changes and the run that created a fresh RemoteData agent and an Energy agent that uses it as a connected agent. It's intended as a clear, audience-friendly summary you can share.

## What I changed

- Removed the hard-coded RemoteData id from `src/Foundry.Agents/appsettings.json`.
- Cleared persisted RemoteData files so the app would create a new RemoteData agent at startup.
- Ran the host so `HostedAgentRunner` initializes RemoteData first, then creates Energy and attaches RemoteData as a connected tool.

## Key files (after the run)

- `src/Foundry.Agents/appsettings.json` — AgentId removed.
- `Agents/RemoteData/agent-id.txt` — contains the newly created RemoteData agent id.
- `Agents/Energy/agent-id.txt` — contains the Energy agent id.
- `Agents/RemoteData/threads.json` — thread mapping for RemoteData (if present).

## How I ran it (commands)

Run from repository root (PowerShell / pwsh):

```powershell
# remove any persisted RemoteData id & threads, then run the app
Remove-Item -Force -ErrorAction SilentlyContinue .\Agents\RemoteData\agent-id.txt
Remove-Item -Force -ErrorAction SilentlyContinue .\Agents\RemoteData\threads.json
dotnet run --project ./src/Foundry.Agents -c Debug
```

Or, to just re-run after a change:

```powershell
dotnet run --project ./src/Foundry.Agents -c Debug
```

## Representative console excerpts (trimmed)

- RemoteData created and persisted:

```
Created agent with id asst_5i9MysAg2nQVXOkw0tvk344h
Persisted agent id to local file Agents\RemoteData\agent-id.txt
Invoking agent asst_5i9MysAg2nQVXOkw0tvk344h with prompt: Fetch today's SE3 price and Stockholm hourly temperature. Return the JSON envelope only
```

- Energy created and invoked; output was a strict JSON GlobalEnvelope and included citations:

```
EnergyAgent created and persisted with id asst_SJly1WrUHybAZ1gPjNa7bAqh
Created agent asst_SJly1WrUHybAZ1gPjNa7bAqh has 3 tools attached:
 - Tool: OpenApiToolDefinition
 - Tool: CodeInterpreterToolDefinition
 - Tool: ConnectedAgentToolDefinition
Run run_KBxNCPgO8t45SRf515KJX5uJ completed successfully.
{ "agent":"Energy", "status":"ok", "data": { ... }, "citations":["openapi.external_signals_DayAheadPrice","openapi.external_signals_WeatherHourly"] }
Detected RemoteData activity in thread thread_rS4IdzfIUaqtsLbUYLqXVeCg: message contains RemoteData envelope or OpenAPI calls.
```

## Why this matters

- The connected-agent configuration and persisted ID allow Energy to call an authoritative data-provider (RemoteData) rather than hallucinating price/weather values.
- Removing the hard-coded id ensures a reproducible workflow: the system creates and records the authoritative agent at startup.

## Next steps (optional)

- Make Energy's prompt explicitly require: "CALL the connected agent RemoteData and embed its JSON envelope (agent/thread/task/status/data)" to force deterministic delegation.
- Clean up any remaining `threads.json` entries you don't want preserved.
- Convert the summary above into a README or add a run script (`run.ps1`) for team use.

If you want, I can: (A) create a `docs/README.md` with a step-by-step guide, (B) add a `run.ps1` wrapper script, or (C) modify Energy's instructions to mandate calling RemoteData. Which would you prefer?