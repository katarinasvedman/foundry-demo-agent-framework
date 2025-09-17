using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Serilog;
using Microsoft.Extensions.Configuration;
using Foundry.Agents.Agents.RemoteData;
using Foundry.Agents.Tools.OpenApi;
using Azure.Identity;
using Azure.Security.KeyVault.Secrets;
using Azure.Extensions.AspNetCore.Configuration.Secrets;
using Foundry.Agents.Agents;
using System.IO;
using System;
using Microsoft.ApplicationInsights.Extensibility;

var builder = Host.CreateDefaultBuilder(args)
    .ConfigureAppConfiguration((context, cfg) =>
    {
        // Try multiple candidate locations for appsettings.json so running from repo root (--project) works.
        var envName = context.HostingEnvironment.EnvironmentName;
        var candidates = new[]
        {
            Path.Combine(Directory.GetCurrentDirectory(), "appsettings.json"),
            Path.Combine(Directory.GetCurrentDirectory(), "src", "Foundry.Agents", "appsettings.json"),
            Path.Combine(AppContext.BaseDirectory, "appsettings.json"),
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "src", "Foundry.Agents", "appsettings.json")
        };

        foreach (var candidate in candidates)
        {
            if (File.Exists(candidate))
            {
                cfg.AddJsonFile(candidate, optional: false, reloadOnChange: true);
            }
        }

        // Also try environment-specific variants next to any candidate
        foreach (var candidate in candidates)
        {
            var envPath = candidate.Replace("appsettings.json", $"appsettings.{envName}.json");
            if (File.Exists(envPath)) cfg.AddJsonFile(envPath, optional: true, reloadOnChange: true);
        }

        cfg.AddEnvironmentVariables();

        // Bind to read UseManagedIdentity flag or key vault name
        var tmp = cfg.Build();
        var useManagedIdentity = tmp.GetValue<bool?>("Azure:UseManagedIdentity") ?? false;
        var keyVaultName = tmp["Azure:KeyVaultName"];
        if (useManagedIdentity && !string.IsNullOrEmpty(keyVaultName))
        {
            var kvUri = new Uri($"https://{keyVaultName}.vault.azure.net/");
            var credential = new DefaultAzureCredential();
            cfg.AddAzureKeyVault(kvUri, credential);
        }
    })
    .ConfigureServices((context, services) =>
    {
        services.AddSingleton(context.Configuration);
        // Application Insights: register telemetry if a connection string is provided
        var aiConn = context.Configuration["ApplicationInsights:ConnectionString"];
        if (!string.IsNullOrEmpty(aiConn))
        {
            services.AddApplicationInsightsTelemetry(options =>
            {
                options.ConnectionString = aiConn;
            });
        }
        services.AddSingleton<RemoteDataAgent>();
        // Register the new EnergyAgent
        services.AddSingleton<Foundry.Agents.Agents.Energy.EnergyAgent>();
        services.AddHttpClient<OpenApiTool>();
        services.AddSingleton<OpenApiTool>();

        // register the real adapter using the PROJECT_ENDPOINT from config
        var endpoint = context.Configuration["Project:Endpoint"] ?? throw new InvalidOperationException("Configuration 'Project:Endpoint' is required");
        services.AddSingleton<IPersistentAgentsClientAdapter>(sp => new RealPersistentAgentsClientAdapter(endpoint, sp.GetRequiredService<IConfiguration>(), sp.GetService<Microsoft.Extensions.Logging.ILogger<RealPersistentAgentsClientAdapter>>()));

        services.AddHostedService<HostedAgentRunner>();
    });

await builder.RunConsoleAsync();
