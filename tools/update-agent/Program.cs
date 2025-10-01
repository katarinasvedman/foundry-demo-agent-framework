using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging;
using Foundry.Agents.Agents.Shared;

class Program
{
    static async Task<int> Main(string[] args)
    {
        var builder = new ConfigurationBuilder()
            // prefer local appsettings, fall back to the agents project's appsettings
            .AddJsonFile("appsettings.json", optional: true)
            .AddJsonFile(Path.Combine("src", "Foundry.Agents", "appsettings.json"), optional: true)
            .AddEnvironmentVariables();
        var config = builder.Build();

        var endpoint = Environment.GetEnvironmentVariable("PROJECT_ENDPOINT") ?? config["Project:Endpoint"];
        if (string.IsNullOrEmpty(endpoint))
        {
            // Try reading src/Foundry.Agents/appsettings.json directly as a last resort
            var fallbackPath = Path.Combine("src", "Foundry.Agents", "appsettings.json");
            if (File.Exists(fallbackPath))
            {
                try
                {
                    var json = await File.ReadAllTextAsync(fallbackPath);
                    using var doc = System.Text.Json.JsonDocument.Parse(json);
                    if (doc.RootElement.TryGetProperty("Project", out var proj) && proj.TryGetProperty("Endpoint", out var ep))
                    {
                        endpoint = ep.GetString();
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Failed to parse fallback appsettings.json: {ex.Message}");
                }
            }
        }

        if (string.IsNullOrEmpty(endpoint))
        {
            Console.WriteLine("PROJECT_ENDPOINT not set (env) and Project:Endpoint not present in config. Aborting.");
            return 2;
        }

        var agentIdPath = Path.Combine("Agents", "RemoteData", "agent-id.txt");
        if (!File.Exists(agentIdPath))
        {
            Console.WriteLine($"Persisted agent id file not found: {agentIdPath}");
            return 3;
        }
        var agentId = (await File.ReadAllTextAsync(agentIdPath)).Trim();
        if (string.IsNullOrEmpty(agentId))
        {
            Console.WriteLine("Agent id empty in file.");
            return 4;
        }

        // Load spec from file or optionally from SPEC_URL env var
        string specJson = string.Empty;
        var specUrl = Environment.GetEnvironmentVariable("SPEC_URL");
        if (!string.IsNullOrEmpty(specUrl))
        {
            Console.WriteLine($"Fetching OpenAPI spec from URL: {specUrl}");
            using var http = new System.Net.Http.HttpClient();
            specJson = await http.GetStringAsync(specUrl);
        }
        else
        {
            var specPath = Path.Combine("src", "Foundry.Agents", "Tools", "OpenApi", "apispec.json");
            if (!File.Exists(specPath))
            {
                Console.WriteLine($"Spec file not found at {specPath}. Aborting.");
                return 5;
            }
            specJson = await File.ReadAllTextAsync(specPath);
        }

    // Configure console logging for diagnostics
    using var loggerFactory = LoggerFactory.Create(lb => lb.AddConsole());
    var logger = loggerFactory.CreateLogger<RealPersistentAgentsClientAdapter>();
    var adapter = new RealPersistentAgentsClientAdapter(endpoint, config, logger);
        Console.WriteLine($"Updating agent {agentId} at endpoint {endpoint}...");
        var ok = await adapter.UpdateAgentOpenApiToolAsync(agentId, specJson);
        Console.WriteLine(ok ? "Update succeeded" : "Update failed");
        return ok ? 0 : 1;
    }
}
