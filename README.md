Foundry Agents - C# project scaffold

This workspace contains a scaffold for building AI Foundry agents in C#.

Folders:
- src/Foundry.Agents: the main .NET console/worker app hosting agents
- infra: Bicep files and parameters for Azure deployment

How to build:
- dotnet restore
- dotnet build

How to run locally:
- Configure OpenApi:BaseUrl in appsettings.json or environment variables
- dotnet run --project src/Foundry.Agents

Next steps:
- Replace the placeholder AI.Foundry.SDK package with the official SDK package name and version
- Add secrets management (Azure Key Vault) and Managed Identity for Azure deployments
- Integrate a generated OpenAPI client (NSwag / AutoRest) if you have an OpenAPI spec
