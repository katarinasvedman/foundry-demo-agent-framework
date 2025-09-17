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

## Running the OpenAPI Function and dev-tunnel

This repository includes a small OpenAPI-backed Azure Function used by the demo agents (see `src/ExternalSignals.Api`). When running agents locally you can run the Function on localhost and either point the agents at `http://localhost:7071` (local-only) or expose the Function with a public tunnel so the Persistent Agents OpenAPI tool can call it.

1. Run the Function locally
   - Change to the function project and start the Functions host:
     cd src/ExternalSignals.Api
     func start --verbose
   - Verify a sample endpoint (PowerShell):
     Invoke-RestMethod -Uri 'http://localhost:7071/api/price/dayahead?zone=SE3&date=YYYY-MM-DD' -UseBasicParsing | ConvertTo-Json -Depth 5
   - Or with curl (Windows):
     curl -s "http://localhost:7071/api/price/dayahead?zone=SE3&date=YYYY-MM-DD" -H "Accept: application/json"

2. Expose the Function with a tunnel (optional)
   - If you need the Function to be reachable from outside your machine (for example when using the Persistent Agents service which calls your OpenAPI tool), expose the local host with a tunnel.
   - Two common options:
     - Azure Dev Tunnels (VS Code or azd dev tunnel workflows) — follow your preferred Azure Dev Tunnels setup and copy the public HTTPS URL.
     - ngrok (quick alternative):
       ngrok http 7071
       Copy the generated https://... URL.

3. Configure Foundry.Agents to use the OpenAPI Function
   - Update `appsettings.Development.json` or set the environment variable `OpenApi:BaseUrl` to the public tunnel (or `http://localhost:7071` if running locally).
     Example (partial):
     {
       "OpenApi": { "BaseUrl": "https://<your-tunnel-host>/api" }
     }
   - Ensure `Project:Endpoint` points to your Persistent Agents service when running against cloud agents. For local-only testing that does not require cloud Persistent Agents, you can run the agent code in a development mode if available.

4. Authentication & function keys
   - If your Function requires a function key or other auth, ensure the OpenAPI tool is reachable with the required headers or modify the Function to allow anonymous access for local testing only.

5. Troubleshooting
   - If the agent run cannot reach the OpenAPI tool, verify:
     - The Functions host is running and returns 200 on the test endpoint.
     - The tunnel URL is HTTPS (Persistent Agents/OpenAPI tools expect secure endpoints).
     - Any function keys or authentication are configured in your local settings and the OpenApi:BaseUrl includes the correct path.

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
