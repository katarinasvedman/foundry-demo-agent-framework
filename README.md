
# Foundry Demo — persisted AI agents (C#)

This repository demonstrates persisted AI "agents" hosted in a .NET app and a small in-process Orchestrator that runs a sequential agent pipeline and captures final outputs.

What you'll find
- `src/Foundry.Agents` — the host and DI wiring; the console app that builds and runs agents
- `src/Foundry.Agents/Agents` — agent wrappers and the `Orchestrator` implementation
- `Agents/` — agent instruction markdown and runtime persisted artifacts (ignored by git)
- `docs/` — orchestrator outputs (e.g. `last_agent_output.json`) and generated plots
- `tests/` — unit tests

High-level behavior
- The host creates-or-fetches persisted agents (RemoteData, Energy, etc.) using the Persistent Agents SDK.
- `Orchestrator` runs a sequential pipeline (RemoteData -> Energy) via an in-process workflow and streams events; the final assembled output is captured in-memory.
- When the Energy output is present, it is pretty-printed to `docs/last_agent_output.json` and a plotting script (Python) is invoked to produce a PNG under `docs/`.

Quick run (recommended)
1. Build the solution and run the host (use environment variables or `appsettings.*.json` for configuration):

```powershell
dotnet restore
dotnet build foundry-demo-take4.sln -c Debug

# Configure PROJECT_ENDPOINT and any other env vars required, then:
dotnet run --project src/Foundry.Agents
```

2. After a successful run the orchestrator will write `docs/last_agent_output.json` and may create `docs/energy_measures_<timestamp>.png` when Energy output is available.

Configuration
- `Project:Endpoint` — persistent agents service endpoint
- `Project:ModelDeploymentName` — model deployment id (when creating agents)

Notes
- Agent instruction markdown is stored in `Agents/<Agent>/` and is loaded at runtime by the host. Runtime artifacts (agent ids and temporary run locks) are intentionally ignored by git.
- The repository previously contained step-by-step instructions for running local tool servers (ExternalSignals.Api). The current focus is on the persisted-agent workflow and the Orchestrator; if you still want to run a local ExternalSignals service, start it separately and set `OpenApi:BaseUrl`.

Testing

```powershell
dotnet test tests/Foundry.Agents.Tests/Foundry.Agents.Tests.csproj -c Debug --no-build
```
