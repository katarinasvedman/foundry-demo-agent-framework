using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Azure;
using Azure.Core;
using Azure.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Azure.AI.Agents.Persistent;
using System.Reflection;
using Foundry.Agents.Agents;

namespace Foundry.Agents.Agents.Shared
{
    public class RealPersistentAgentsClientAdapter : IPersistentAgentsClientAdapter
    {
        private readonly string _endpoint;
        private readonly ILogger<RealPersistentAgentsClientAdapter> _logger;
        private readonly IConfiguration _configuration;

        public RealPersistentAgentsClientAdapter(string endpoint, IConfiguration configuration, ILogger<RealPersistentAgentsClientAdapter>? logger = null)
        {
            if (string.IsNullOrEmpty(endpoint)) throw new ArgumentNullException(nameof(endpoint));
            _endpoint = endpoint;
            _logger = logger ?? Microsoft.Extensions.Logging.LoggerFactory.Create(builder => { }).CreateLogger<RealPersistentAgentsClientAdapter>();
            _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        }

        public async Task<bool> AgentExistsAsync(string agentId)
        {
            try
            {
                var client = new PersistentAgentsClient(_endpoint, new DefaultAzureCredential());
                var admin = client.Administration;

                try
                {
                    var resp = await admin.GetAgentAsync(agentId);
                    return resp != null && resp.Value != null;
                }
                catch (RequestFailedException rf) when (rf.Status == 404)
                {
                    return false;
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "GetAgentAsync threw; treating as not found");
                    return false;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to check agent existence");
                throw;
            }
        }

        public async Task<string?> CreateAgentAsync(string modelDeploymentName, string name, string? instructions, IEnumerable<string>? toolTypes = null)
        {
            try
            {
                var client = new PersistentAgentsClient(_endpoint, new DefaultAzureCredential());
                var admin = client.Administration;

                // Use provided instructions if non-empty.
                var instructionsToUse = !string.IsNullOrWhiteSpace(instructions) ? instructions : null;

                // Load inline OpenAPI spec JSON if configured
                BinaryData? specData = null;
                try
                {
                    var specPath = _configuration["OpenApi:SpecPath"];
                    // If not configured, fall back to the bundled spec file path so the tool is attached by default.
                    if (string.IsNullOrWhiteSpace(specPath))
                    {
                        specPath = Path.Combine("Tools", "OpenApi", "apispec.json");
                    }
                    if (!string.IsNullOrEmpty(specPath))
                    {
                        var fullPath = specPath;
                        if (!Path.IsPathRooted(fullPath)) fullPath = Path.Combine(AppContext.BaseDirectory, specPath);
                        if (File.Exists(fullPath))
                        {
                            var specJson = await File.ReadAllTextAsync(fullPath);
                            specData = BinaryData.FromString(specJson);
                        }
                        else
                        {
                            _logger.LogWarning("OpenAPI spec not found at {SpecPath}", specPath);
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to read OpenAPI spec");
                }

                // Build tools list
                var tools = new List<ToolDefinition>();
                // Normalize toolTypes to a set for lookups
                var requested = toolTypes != null ? new HashSet<string>(toolTypes, StringComparer.OrdinalIgnoreCase) : new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                // Attach OpenAPI tool if requested and specData is available
                if ((requested.Count == 0 || requested.Contains("openapi")) && specData != null)
                {
                    var openApiAuth = new OpenApiAnonymousAuthDetails();
                    var openApiTool = new OpenApiToolDefinition("external_signals", "Fetch SE3 prices & Stockholm weather (24h)", specData!, openApiAuth, new List<string>());
                    tools.Add(openApiTool);
                }

                // Attach Code Interpreter tool if requested
                if (requested.Count == 0 || requested.Contains("code_interpreter") || requested.Contains("code-interpreter") || requested.Contains("interpreter"))
                {
                    // Create a CodeInterpreterToolDefinition using the SDK type. Use defaults as no special payload is required for basic usage.
                    try
                    {
                        var codeTool = new CodeInterpreterToolDefinition();
                        tools.Add(codeTool);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "CodeInterpreterToolDefinition not available in SDK or failed to construct; skipping code interpreter tool registration");
                    }
                }

                // Attach Connected Agent tools when requested in the format "connected:<AgentName>"
                try
                {
                    var connectedRequests = requested.Where(t => t.StartsWith("connected:", StringComparison.OrdinalIgnoreCase)).ToList();
                    if (connectedRequests.Count > 0)
                    {
                        var asm = typeof(PersistentAgentsClient).Assembly;
                        var connectedType = asm.GetType("Azure.AI.Agents.Persistent.ConnectedAgentToolDefinition");
                        foreach (var req in connectedRequests)
                        {
                            var parts = req.Split(new[] { ':' }, 2);
                            if (parts.Length != 2) continue;
                            var targetAgentName = parts[1];
                            if (string.IsNullOrWhiteSpace(targetAgentName)) continue;

                            // Try to read persisted agent id for that target agent name
                            var persisted = await AgentFileHelpers.ReadPersistedAgentIdAsync(_configuration, targetAgentName, _logger);
                            if (string.IsNullOrEmpty(persisted))
                            {
                                // No persisted id found for this connected agent; do not attach a connected tool.
                                _logger.LogWarning("Requested connected agent '{Name}' but no persisted agent id found. Skipping connected tool.", targetAgentName);
                                continue;
                            }

                            if (connectedType != null)
                            {
                                try
                                {
                                    bool attached = false;

                                    // 1) Try constructor with three string parameters (agentId, name, description)
                                    var threeStringCtor = connectedType.GetConstructor(new[] { typeof(string), typeof(string), typeof(string) });
                                    if (threeStringCtor != null)
                                    {
                                        try
                                        {
                                            var desc = "Fetches SE3 prices & Stockholm weather (24h)";
                                            var inst = threeStringCtor.Invoke(new object[] { persisted, targetAgentName, desc }) as ToolDefinition;
                                            if (inst != null)
                                            {
                                                tools.Add(inst);
                                                _logger.LogInformation("Attached connected agent tool (3-string ctor) for {AgentName} -> {AgentId}", targetAgentName, persisted);
                                                attached = true;
                                            }
                                        }
                                        catch (Exception ex)
                                        {
                                            _logger.LogDebug(ex, "3-string constructor invocation on ConnectedAgentToolDefinition failed");
                                        }
                                    }

                                    if (!attached)
                                    {
                                        // 2) Try a constructor where first parameter is the agent id (string) and fill defaults for other params
                                        var ctors = connectedType.GetConstructors(BindingFlags.Public | BindingFlags.Instance);
                                        ConstructorInfo? chosen = null;
                                        foreach (var c in ctors)
                                        {
                                            var ps = c.GetParameters();
                                            if (ps.Length >= 1 && ps[0].ParameterType == typeof(string))
                                            {
                                                chosen = c;
                                                break;
                                            }
                                        }

                                        if (chosen != null)
                                        {
                                            try
                                            {
                                                object?[] parms = new object?[chosen.GetParameters().Length];
                                                parms[0] = persisted;
                                                var ctorParams = chosen.GetParameters();
                                                for (int i = 1; i < ctorParams.Length; i++)
                                                {
                                                    var p = ctorParams[i];
                                                    if (p.ParameterType == typeof(string)) parms[i] = targetAgentName;
                                                    else if (p.ParameterType == typeof(IEnumerable<string>)) parms[i] = new List<string>();
                                                    else parms[i] = null;
                                                }

                                                var instance = Activator.CreateInstance(connectedType, parms) as ToolDefinition;
                                                if (instance != null)
                                                {
                                                    tools.Add(instance);
                                                    _logger.LogInformation("Attached connected agent tool for {AgentName} -> {AgentId}", targetAgentName, persisted);
                                                    attached = true;
                                                }
                                            }
                                            catch (Exception ex)
                                            {
                                                _logger.LogDebug(ex, "String-first constructor invocation on ConnectedAgentToolDefinition failed");
                                            }
                                        }
                                    }

                                    if (!attached)
                                    {
                                        // 3) Fallback: build a ConnectedAgentDetails and attempt to construct the tool from that
                                        try
                                        {
                                            var detailsType = asm.GetType("Azure.AI.Agents.Persistent.ConnectedAgentDetails");
                                            if (detailsType != null)
                                            {
                                                var detailsCtor = detailsType.GetConstructor(new[] { typeof(string), typeof(string), typeof(string) });
                                                if (detailsCtor != null)
                                                {
                                                    // API requires the connected agent 'name' to match ^[a-zA-Z_]+$; sanitize to a valid token and keep a friendly display in the description.
                                                    var sanitizedName = System.Text.RegularExpressions.Regex.Replace(targetAgentName, "[^a-zA-Z_]", "_");
                                                    // Provide an activation-oriented description so the calling agent knows when to invoke RemoteData.
                                                    var description = string.Equals(targetAgentName, "RemoteData", StringComparison.OrdinalIgnoreCase)
                                                        ? "Activation: CALL THIS AGENT when you need authoritative SE3 day-ahead prices or hourly Stockholm weather.\n" +
                                                          "How to call: Use DayAheadPrice(zone=SE3,date=YYYY-MM-DD) then WeatherHourly(city=Stockholm,date=YYYY-MM-DD).\n" +
                                                          "Rule: Always call this connected agent before asserting price or weather values in your response; embed the RemoteData JSON envelope (agent/thread/task/status/data) and cite the source.\n" +
                                                          "Goal: Provide exact 24-hour numeric arrays for prices and temperature; do not hallucinate or approximate."
                                                        : $"{targetAgentName} - Fetches SE3 prices & Stockholm weather (24h)";
                                                    var detailsInstance = detailsCtor.Invoke(new object[] { persisted, sanitizedName, description });
                                                    if (detailsInstance != null)
                                                    {
                                                        var ctors2 = connectedType.GetConstructors(BindingFlags.Public | BindingFlags.Instance);
                                                        foreach (var c2 in ctors2)
                                                        {
                                                            var ps2 = c2.GetParameters();
                                                            if (ps2.Length >= 1 && ps2[0].ParameterType == detailsType)
                                                            {
                                                                try
                                                                {
                                                                    var inst2 = c2.Invoke(new object[] { detailsInstance }) as ToolDefinition;
                                                                    if (inst2 != null)
                                                                    {
                                                                        tools.Add(inst2);
                                                                        _logger.LogInformation("Attached connected agent tool (via ConnectedAgentDetails) for {AgentName} -> {AgentId}", targetAgentName, persisted);
                                                                        attached = true;
                                                                        break;
                                                                    }
                                                                }
                                                                catch (Exception ex)
                                                                {
                                                                    _logger.LogDebug(ex, "ConnectedAgentToolDefinition ctor accepting ConnectedAgentDetails failed");
                                                                }
                                                            }
                                                        }
                                                    }
                                                }
                                            }
                                        }
                                        catch (Exception ex)
                                        {
                                            _logger.LogDebug(ex, "ConnectedAgentDetails fallback failed");
                                        }
                                    }

                                    if (!attached)
                                    {
                                        _logger.LogWarning("ConnectedAgentToolDefinition exists but could not be instantiated for agent {AgentName}.", targetAgentName);
                                    }
                                }
                                catch (Exception ex)
                                {
                                    _logger.LogWarning(ex, "Failed to construct ConnectedAgentToolDefinition for {AgentName}; skipping.", targetAgentName);
                                }
                            }
                            else
                            {
                                _logger.LogWarning("ConnectedAgentToolDefinition type not found in SDK; cannot attach connected agent {AgentName}.", targetAgentName);
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Error while processing connected agent tool requests");
                }

                // Call CreateAgentAsync with strongly-typed parameters (pass CancellationToken)
                var createResp = await admin.CreateAgentAsync(modelDeploymentName, name, null, instructionsToUse, tools, null, null, null, null, null, CancellationToken.None);
                var created = createResp.Value;
                return created?.Id;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to create agent via Persistent SDK");
                throw;
            }
        }
    }
}
