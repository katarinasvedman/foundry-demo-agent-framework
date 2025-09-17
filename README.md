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
From repository root:
- dotnet run --project src/Foundry.Agents

Program.cs tries multiple candidate locations for `appsettings.json` so you can run from the repo root or the project folder.

## Files of interest
- `src/Foundry.Agents/Program.cs` — host bootstrap, DI, Key Vault support
- `src/Foundry.Agents/Agents/` — agent implementations and helpers
- `src/Foundry.Agents/Tools/OpenApi/` — bundled OpenAPI spec and helper tool
- `infra/` — Bicep files for Azure deployments

## Notes & troubleshooting
- The code persists agent ids and thread mappings to `Agents/<AgentName>/` by default. This path is included in `.gitignore`.
- If you plan to run multiple processes concurrently, note the simple file-locking used for thread operations.
- The adapter uses reflection when attaching connected-agent tools; SDK changes may require updates.

## Contributing
- Open a branch, make changes, run `dotnet test`, and submit a PR targeting `main`.

---

If you'd like, I can:
- add a sample `appsettings.json.example` file (without secrets),
- run `dotnet restore` and `dotnet build` now and report errors, or
- add a GitHub Actions workflow to run tests on push.

Tell me which you'd like next.
