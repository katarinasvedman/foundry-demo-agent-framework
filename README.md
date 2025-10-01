# Foundry Agents - C# project scaffold

This workspace contains a scaffold for building AI Foundry agents in C#.

## Overview
- `src/Foundry.Agents`: the main .NET console/worker app hosting agents
- `infra`: Bicep files and parameters for Azure deployment
- `Agents/`: local runtime artifacts (agent ids, thread mappings) — these are ignored by git

## Prerequisites
- .NET SDK 8 or later (dotnet 8+)
- Git
- (Optional) Azure access if you plan to use Key Vault or the Persistent Agents service

## Quick setup
1. Restore packages:
   dotnet restore

2. Build the project:
   dotnet build src/Foundry.Agents

3. Run tests:
   dotnet test
   - Or use the provided PowerShell wrapper: `./run-tests.ps1`

## Configuration
The application reads configuration from `appsettings.json` and environment variables. The most important settings are:

- `Project:Endpoint` (required at runtime)
  - The URL to the Persistent Agents service. Program.cs will throw if this is missing.

- `Project:ModelDeploymentName` or `Model:DeploymentName` (required by Energy agent)
  - Model deployment identifier used when creating agents.

- `OpenApi:SpecPath` (optional)
  - Path to an OpenAPI spec JSON. Defaults to `src/Foundry.Agents/Tools/OpenApi/apispec.json` if not set.

- Azure Key Vault (optional)
  - `Azure:UseManagedIdentity` = true and `Azure:KeyVaultName` to persist & read agent ids from Key Vault.

- Application Insights (optional)
  - `ApplicationInsights:ConnectionString` to enable telemetry.

Example minimal `appsettings.json` (do NOT commit secrets):

```json
{
  "Project": {
    "Endpoint": "https://your-persistent-agents-endpoint",
    "ModelDeploymentName": "your-model-deployment"
  }
}
```

You can also set these as environment variables using `:` (or `__` for some shells/CI):
- `Project:Endpoint`
- `Project:ModelDeploymentName`

## Running locally
# Foundry Demo (C#)

A small demo repo showing persisted agents using Azure Persistent Agents and an OpenAPI-backed tool.

This README is intentionally short and focuses on what contributors need to get started.

## Quick start

Prerequisites
- .NET 8 SDK
- Git
- (Optional) Azure login for cloud-based Persistent Agents

Commands
- Restore: `dotnet restore`
- Build: `dotnet build foundry-demo-take4.sln -c Debug`
- Tests: `dotnet test foundry-demo-take4.sln -c Debug --no-build`
- Run host: `dotnet run --project src/Foundry.Agents`

Note: configuration comes from `appsettings.json` and environment variables. Do not commit secrets.

## OpenAPI function (optional)

The Function used by the demo lives in `src/ExternalSignals.Api`.
- Run locally: `cd src/ExternalSignals.Api && func start`
- If the Persistent Agents service must call your function, expose it with a tunnel (ngrok / Azure Dev Tunnels) and set `OpenApi:BaseUrl` to the public HTTPS URL.

## Adapters (where to add shared code)

Canonical location for shared adapters (interfaces/implementations):

`src/Foundry.Agents/Agents/Shared/`

Please add new adapters and shared types under `Agents/Shared` to avoid duplicate definitions in agent folders.

## Recent cleanup

- Removed dev-only utility `tools/inspect-remote-thread`.
- Removed duplicate adapter placeholders under agent folders and consolidated the implementations in `Agents/Shared`.

## Developer checks

- Build & test locally (recommended):
  - `dotnet build foundry-demo-take4.sln -c Debug`
  - `dotnet test foundry-demo-take4.sln -c Debug --no-build`

## Contributing

- Branch from `main`, make small focused changes, run tests, and open a PR.

Optional additions I can make:
- `appsettings.json.example` (no secrets)
- GitHub Actions to run tests on push
## Contributing

