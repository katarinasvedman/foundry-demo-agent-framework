using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Serilog;
using Microsoft.Extensions.Configuration;
using Foundry.Agents.Agents.Shared;
using Foundry.Agents.Tools.OpenApi;
using Azure.Identity;
using Azure.Security.KeyVault.Secrets;
using Azure.Extensions.AspNetCore.Configuration.Secrets;
using Foundry.Agents.Agents;
using System.IO;
using System;
using Microsoft.ApplicationInsights.Extensibility;
using Foundry.Agents.Agents.RemoteData;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using System.Diagnostics;

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
        // Observability: enable OpenTelemetry tracing when requested via env var
        var enableOtel = (System.Environment.GetEnvironmentVariable("ENABLE_OTEL") ?? "false").Equals("true", System.StringComparison.OrdinalIgnoreCase);
        var enableSensitive = (System.Environment.GetEnvironmentVariable("ENABLE_SENSITIVE_DATA") ?? "false").Equals("true", System.StringComparison.OrdinalIgnoreCase);
        // Register a shared ActivitySource for instrumentation
        var activitySourceName = "Foundry.Agents.Workflows";
        var activitySource = new ActivitySource(activitySourceName);
        services.AddSingleton(activitySource);

        if (enableOtel)
        {
            services.AddOpenTelemetryTracing(builder =>
            {
                builder
                    .SetResourceBuilder(ResourceBuilder.CreateDefault().AddService("Foundry.Agents"))
                    .AddSource(activitySourceName)
                    .AddConsoleExporter();
                // Production: add OTLP or Application Insights exporter here when configured
            });
        }
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
        //services.AddSingleton<RemoteDataAgent>();
        // Register the new EnergyAgent
        //services.AddSingleton<Foundry.Agents.Agents.Energy.EnergyAgent>();
        // Register the new ReportAgent
        //services.AddSingleton<Foundry.Agents.Agents.Report.ReportAgent>();
        // Register Compute, Orchestrator and Email assistant
        //services.AddSingleton<Foundry.Agents.Agents.Compute.ComputeAgent>();
        services.AddSingleton<Foundry.Agents.Agents.Orchestrator.OrchestratorAgent>();
        //services.AddSingleton<Foundry.Agents.Agents.EmailAssistant.EmailAssistantAgent>();
        //services.AddHttpClient<OpenApiTool>();
        //services.AddSingleton<OpenApiTool>();

        // Register LogicAppTool (HttpClient + DefaultAzureCredential)
        /*services.AddHttpClient<Foundry.Agents.Tools.LogicApp.LogicAppTool>();
        services.AddSingleton<Foundry.Agents.Tools.LogicApp.LogicAppTool>(sp =>
        {
            var http = sp.GetRequiredService<System.Net.Http.HttpClient>();
            var cfg = sp.GetRequiredService<IConfiguration>();
            var logger = sp.GetRequiredService<ILogger<Foundry.Agents.Tools.LogicApp.LogicAppTool>>();
            var cred = new DefaultAzureCredential();
            return new Foundry.Agents.Tools.LogicApp.LogicAppTool(http, cred, logger, cfg);
        });*/

        // register the real adapter using the PROJECT_ENDPOINT from config
        var endpoint = context.Configuration["Project:Endpoint"] ?? throw new InvalidOperationException("Configuration 'Project:Endpoint' is required");
        if (endpoint.StartsWith("http://localhost", StringComparison.OrdinalIgnoreCase) || endpoint.StartsWith("http://127.0.0.1", StringComparison.OrdinalIgnoreCase))
        {
            // use local mock adapter for development to avoid Azure SDK requiring HTTPS for bearer tokens
            services.AddSingleton<IPersistentAgentsClientAdapter>(sp => new Foundry.Agents.Agents.Shared.LocalPersistentAgentsClientAdapter(endpoint, sp.GetRequiredService<IConfiguration>(), sp.GetService<Microsoft.Extensions.Logging.ILogger<Foundry.Agents.Agents.Shared.LocalPersistentAgentsClientAdapter>>()));
        }
        else
        {
            services.AddSingleton<IPersistentAgentsClientAdapter>(sp => new Foundry.Agents.Agents.Shared.RealPersistentAgentsClientAdapter(endpoint, sp.GetRequiredService<IConfiguration>(), sp.GetService<Microsoft.Extensions.Logging.ILogger<Foundry.Agents.Agents.Shared.RealPersistentAgentsClientAdapter>>()));
        }

        services.AddHostedService<HostedAgentRunner>();
    });

await builder.RunConsoleAsync();
