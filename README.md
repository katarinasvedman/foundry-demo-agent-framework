# Foundry Demo — persisted AI agents (C#)

This repository is a compact demo that hosts AI "agents" in a .NET app and shows how to persist, update, and orchestrate agents at runtime.
# Foundry Agents - C# project scaffold

This workspace contains a scaffold for building AI Foundry agents in C#.
# Foundry Demo — persisted AI agents (C#)

A compact demo that hosts AI "agents" in a .NET app and shows how to persist and update agents at runtime. It includes three demo agents used by the sample flows:

- Energy — produces a JSON "GlobalEnvelope" and a small plot
- RemoteData — a small data-fetching agent that uses an OpenAPI-backed tool to provide data to other agents
- EmailAssistant — posts an email request to an Azure Logic App (invoked via a host-provided LogicAppTool using AAD tokens)

Keep this repo lightweight: agent instructions are stored under `Agents/<Agent>/` (markdown), and runtime artifacts (agent ids, thread mappings) are persisted under `Agents/` and intentionally ignored by git.

## Quick overview
- Host: `src/Foundry.Agents` — the .NET console/worker that creates and runs agents
- Persisted data: `Agents/<Agent>/agent-id.txt` and `Agents/<Agent>/threads.json`
- Tools: OpenAPI-backed HTTP tool (see `src/ExternalSignals.Api`) used by `RemoteData`, and a Logic App helper that uses Azure AD (DefaultAzureCredential)

## What this demo shows
- Create and persist agents so they can be updated in-place (no recreate required)
- Use a connected-agent pattern for multi-agent orchestration(Energy → RemoteData, EmailAssistant)
- Use OpenAPI (Azure Function) as tool for RemoteData; EmailAssistant uses the LogicAppTool with AAD tokens
- Invoke a Logic App, as a tool, securely with AAD tokens from an agent (EmailAssistant)

## Quick start (local)
Prereqs:
- .NET 8 SDK
- Git
- (Optional) Azure CLI + az login for Logic App AAD testing


Start the ExternalSignals API (Function) first

The `RemoteData` agent uses the local OpenAPI server in `src/ExternalSignals.Api`. Start that function before launching the host so `RemoteData` can call it.

From the repo root (PowerShell):

```powershell
cd src/ExternalSignals.Api
# If you have Azure Functions Core Tools installed:
func start
# Or, if the project is a regular .NET app:
dotnet run
```

Note the HTTP base URL printed by the function host (usually `http://localhost:7071`). Then, in the same PowerShell session set the `OpenApi:BaseUrl` for the host so the `RemoteData` tool points to your local function:

## Top-level agents in this demo

- Energy — single entry point for the scenario; produces a GlobalEnvelope JSON and small plot outputs
- RemoteData — data-fetching agent that calls a local OpenAPI-backed tool (ExternalSignals)
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
- Agent instruction markdown is stored in `Agents/<Agent>/` and is loaded at runtime by the host. Runtime artifacts (agent ids, thread maps, locks) are intentionally ignored by git.
- The repository previously contained step-by-step instructions for running local tool servers (ExternalSignals.Api). The current focus is on the persisted-agent workflow and the Orchestrator; if you still want to run a local ExternalSignals service, start it separately and set `OpenApi:BaseUrl`.

Testing

```powershell
dotnet test tests/Foundry.Agents.Tests/Foundry.Agents.Tests.csproj -c Debug --no-build
```

If you'd like
- I can add a `tools/cleanup.ps1` to tidy runtime artifacts.
- I can add a small integration script to run a canned orchestrator scenario and write outputs to `docs/`.

Security
- Do not commit secrets. Use environment variables or a local `appsettings.Development.json` excluded from source control.
