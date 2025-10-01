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
                                                        ? @"Activation:
                                                            Call this agent whenever authoritative SE3 24h day-ahead prices or hourly Stockholm weather are needed.

                                                            How to call:
                                                            Use operations:
                                                            - DayAheadPrice(zone=""SE3"", date=""yyyy-MM-dd"")
                                                            - WeatherHourly(city=""Stockholm"", date=""yyyy-MM-dd"")

                                                            Rule:
                                                            Always call this agent before asserting or using price or weather data. Do not hallucinate.

                                                            Goal:
                                                            Return validated 24-element numeric arrays for prices and temperatures inside the RemoteData JSON envelope."
                                                        : string.Empty;
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

        // Replace or attach an OpenAPI tool on an existing agent without recreating the agent.
        public async Task<bool> UpdateAgentOpenApiToolAsync(string agentId, string openApiSpecJson)
        {
            if (string.IsNullOrEmpty(agentId)) throw new ArgumentNullException(nameof(agentId));
            if (string.IsNullOrEmpty(openApiSpecJson)) throw new ArgumentNullException(nameof(openApiSpecJson));

            try
            {
                var client = new PersistentAgentsClient(_endpoint, new DefaultAzureCredential());
                var admin = client.Administration;

                // Fetch existing agent
                var getResp = await admin.GetAgentAsync(agentId);
                var agentDef = getResp?.Value;
                if (agentDef == null)
                {
                    _logger.LogWarning("Agent {AgentId} not found when attempting to update OpenAPI tool.", agentId);
                    return false;
                }

                // Prepare new spec BinaryData
                var specData = BinaryData.FromString(openApiSpecJson);

                // Attempt to create an OpenApiToolDefinition instance via reflection from the SDK assembly
                var asm = typeof(PersistentAgentsClient).Assembly;
                var openApiToolType = asm.GetType("Azure.AI.Agents.Persistent.OpenApiToolDefinition");
                var openApiAuthType = asm.GetType("Azure.AI.Agents.Persistent.OpenApiAnonymousAuthDetails");

                object? newOpenApiTool = null;
                if (openApiToolType != null && openApiAuthType != null)
                {
                    try
                    {
                        // Try constructor: OpenApiToolDefinition(string name, string description, BinaryData spec, OpenApiAuthDetails auth, IEnumerable<string> operations)
                        var ctors = openApiToolType.GetConstructors();
                        ConstructorInfo? chosen = null;
                        foreach (var c in ctors)
                        {
                            var ps = c.GetParameters();
                            if (ps.Length >= 4 && ps[0].ParameterType == typeof(string) && ps[2].ParameterType == typeof(BinaryData))
                            {
                                chosen = c;
                                break;
                            }
                        }

                        if (chosen != null)
                        {
                            // create auth instance
                            var authInstance = Activator.CreateInstance(openApiAuthType);
                            var parms = new object?[] { "external_signals", "Fetch SE3 prices & Stockholm weather (24h)", specData, authInstance, new List<string>() };
                            newOpenApiTool = chosen.Invoke(parms);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to construct OpenApiToolDefinition via reflection");
                    }
                }

                if (newOpenApiTool == null)
                {
                    _logger.LogWarning("Could not construct OpenApiToolDefinition via SDK reflection; aborting update.");
                    return false;
                }

                // Replace existing OpenAPI tool if found, otherwise add it. Use a mutable list copy so we can update even if agentDef.Tools is read-only.
                var currentTools = agentDef.Tools;
                var toolsList = new System.Collections.Generic.List<ToolDefinition>();
                if (currentTools != null)
                {
                    foreach (var t in currentTools)
                    {
                        toolsList.Add(t);
                    }
                }

                int replaced = 0;
                for (int i = 0; i < toolsList.Count; i++)
                {
                    var t = toolsList[i];
                    if (t == null) continue;
                    var tTypeName = t.GetType().Name ?? string.Empty;
                    if (tTypeName.IndexOf("OpenApi", StringComparison.OrdinalIgnoreCase) >= 0 || tTypeName.IndexOf("OpenApiTool", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        toolsList[i] = (ToolDefinition)newOpenApiTool!;
                        replaced++;
                    }
                }
                if (replaced == 0)
                {
                    // No existing OpenAPI tool found; append
                    toolsList.Add((ToolDefinition)newOpenApiTool!);
                }

                // Try to set the Tools property on the agent definition if writable via reflection
                var agentType = agentDef.GetType();
                var toolsProp = agentType.GetProperty("Tools");
                if (toolsProp != null && toolsProp.CanWrite && toolsProp.PropertyType.IsAssignableFrom(typeof(System.Collections.Generic.IEnumerable<ToolDefinition>)))
                {
                    toolsProp.SetValue(agentDef, toolsList);
                }
                else
                {
                    // If Tools is read-only, attempt to find a method on admin to update tools directly when updating the agent.
                    // We'll proceed and hope the UpdateAgentAsync will accept the modified agentDef object (some SDKs use mutable DTOs).
                    _logger.LogInformation("Agent.Tools property is read-only or not assignable; proceeding with UpdateAgentAsync and hoping the SDK accepts tool changes via the agent object.");
                }

                // Update the agent. Use the Administration.UpdateAgentAsync method if available.
                try
                {
                    // Attempt to find UpdateAgentAsync overloads via reflection on admin
                    var adminType = admin.GetType();
                    var updateMethod = adminType.GetMethod("UpdateAgentAsync", new Type[] { typeof(string), agentDef.GetType(), typeof(CancellationToken) });
                    if (updateMethod != null)
                    {
                        // call UpdateAgentAsync(agentId, agentDef, CancellationToken.None)
                        var task = (System.Threading.Tasks.Task)updateMethod.Invoke(admin, new object[] { agentId, agentDef, CancellationToken.None })!;
                        await task.ConfigureAwait(false);
                    }
                    else
                    {
                        // Fallback: try UpdateAgentAsync(agentDef) or UpdateAgentAsync(string, AgentDefinition)
                        // Try method with single parameter
                        updateMethod = adminType.GetMethod("UpdateAgentAsync", new Type[] { agentDef.GetType() });
                        if (updateMethod != null)
                        {
                            var task = (System.Threading.Tasks.Task)updateMethod.Invoke(admin, new object[] { agentDef })!;
                            await task.ConfigureAwait(false);
                        }
                        else
                        {
                            // As a last resort, enumerate available methods for diagnostics and attempt a RequestContent-based call
                            try
                            {
                                var methods = adminType.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                                    .OrderBy(m => m.Name)
                                    .Select(m =>
                                    {
                                        var ps = m.GetParameters();
                                        var ptypes = string.Join(",", ps.Select(p => p.ParameterType.Name + " " + p.Name));
                                        return $"{m.ReturnType.Name} {m.Name}({ptypes})";
                                    });

                                _logger.LogWarning("Administration.UpdateAgentAsync method not found via reflection; available Administration methods:\n{Methods}", string.Join("\n", methods));
                            }
                            catch (Exception ex)
                            {
                                _logger.LogWarning(ex, "Administration.UpdateAgentAsync method not found and failed to enumerate admin methods.");
                            }

                            // First, try to find a strongly-typed UpdateAgentAsync overload: (string assistantId, string model, string name, string description, string instructions, IEnumerable<ToolDefinition> tools, ToolResources toolResources, Nullable<double> temperature, Nullable<double> topP, BinaryData responseFormat, IReadOnlyDictionary<string,string> metadata, CancellationToken cancellationToken)
                            try
                            {
                                // Look for an UpdateAgentAsync with many parameters (string, string, string, string, string, IEnumerable<ToolDefinition>, ...)
                                var candidate = adminType.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                                    .FirstOrDefault(m => m.Name == "UpdateAgentAsync" && m.GetParameters().Length >= 11 && m.GetParameters()[0].ParameterType == typeof(string));

                                if (candidate != null)
                                {
                                    // Extract model/name/description/instructions from agentDef via reflection (case-insensitive)
                                    string GetStringProp(string[] names)
                                    {
                                        foreach (var n in names)
                                        {
                                            var p = agentType.GetProperty(n, BindingFlags.IgnoreCase | BindingFlags.Public | BindingFlags.Instance);
                                            if (p != null)
                                            {
                                                var v = p.GetValue(agentDef);
                                                if (v != null) return v.ToString()!;
                                            }
                                        }
                                        return string.Empty;
                                    }

                                    var modelVal = GetStringProp(new[] { "Model", "ModelId", "model" });
                                    var nameVal = GetStringProp(new[] { "Name", "name" });
                                    var descriptionVal = GetStringProp(new[] { "Description", "description" });
                                    var instructionsVal = GetStringProp(new[] { "Instructions", "instructions" });

                                    // Ensure description is non-null (API rejects null description)
                                    if (descriptionVal == null) descriptionVal = string.Empty;

                                    // Build parameters for the candidate method. We'll pass null for complex optional types.
                                    var paramCount = candidate.GetParameters().Length;
                                    var args = new object?[paramCount];
                                    args[0] = agentId;
                                    args[1] = modelVal;
                                    args[2] = nameVal;
                                    args[3] = descriptionVal ?? string.Empty;
                                    args[4] = instructionsVal ?? string.Empty;
                                    args[5] = toolsList;
                                    // Fill remaining parameters with null/defaults (toolResources, temperature, topP, responseFormat, metadata)
                                    for (int i = 6; i < paramCount - 1; i++) args[i] = null;
                                    // Last parameter likely CancellationToken
                                    args[paramCount - 1] = CancellationToken.None;

                                    var taskObj = (System.Threading.Tasks.Task)candidate.Invoke(admin, args)!;
                                    await taskObj.ConfigureAwait(false);
                                    _logger.LogInformation("Successfully updated agent {AgentId} via parameter-based UpdateAgentAsync.", agentId);
                                    return true;
                                }
                            }
                            catch (TargetInvocationException tie)
                            {
                                _logger.LogWarning(tie.InnerException ?? tie, "Parameter-based UpdateAgentAsync invocation failed.");
                            }
                            catch (Exception ex)
                            {
                                _logger.LogDebug(ex, "Parameter-based UpdateAgentAsync attempt failed.");
                            }

                            // Try to find a RequestContent-based UpdateAgentAsync: (string assistantId, RequestContent content, RequestContext context)
                            try
                            {
                                
                                var reqContentType = typeof(Azure.Core.RequestContent);
                                var targetMethod = adminType.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                                    .FirstOrDefault(m => m.Name == "UpdateAgentAsync" && m.GetParameters().Length == 3 && m.GetParameters()[1].ParameterType == reqContentType);

                                if (targetMethod != null)
                                {
                                    // Build a serializable payload from agentDef
                                    var payload = new System.Collections.Generic.Dictionary<string, object?>();
                                    string[] keys = new[] { "model", "name", "description", "instructions", "tools", "toolResources", "temperature", "topP", "responseFormat", "metadata" };
                                    foreach (var key in keys)
                                    {
                                        try
                                        {
                                            // Try various casing variants to find a property
                                            var prop = agentType.GetProperty(key, BindingFlags.IgnoreCase | BindingFlags.Public | BindingFlags.Instance);
                                            if (prop != null)
                                            {
                                                var val = prop.GetValue(agentDef);
                                                payload[key] = val;
                                                continue;
                                            }

                                            // Special-case tools: use the toolsList we built earlier
                                            if (string.Equals(key, "tools", StringComparison.OrdinalIgnoreCase))
                                            {
                                                payload[key] = toolsList;
                                            }
                                        }
                                        catch (Exception ex)
                                        {
                                            _logger.LogDebug(ex, "Failed to extract property {Key} from agentDef for payload", key);
                                        }
                                    }

                                    // Serialize payload to JSON (ignore nulls)
                                    var json = Newtonsoft.Json.JsonConvert.SerializeObject(payload, new Newtonsoft.Json.JsonSerializerSettings { NullValueHandling = Newtonsoft.Json.NullValueHandling.Ignore });
                                    _logger.LogDebug("RequestContent UpdateAgentAsync payload JSON: {Json}", json);
                                    var requestContent = Azure.Core.RequestContent.Create(BinaryData.FromString(json));

                                    // Invoke UpdateAgentAsync(agentId, RequestContent, RequestContext) with null RequestContext (SDK tolerates null)
                                    var requestContextType = asm.GetType("Azure.Core.RequestContext");
                                    object? nullContext = null;
                                    if (requestContextType != null)
                                    {
                                        nullContext = null; // keep as null but typed object
                                    }
                                    var invokeParams = new object?[] { agentId, requestContent, nullContext };
                                    var taskObj = (System.Threading.Tasks.Task)targetMethod.Invoke(admin, invokeParams)!;
                                    await taskObj.ConfigureAwait(false);
                                    _logger.LogInformation("Successfully updated agent {AgentId} via RequestContent UpdateAgentAsync.", agentId);
                                    return true;
                                }
                                else
                                {
                                    _logger.LogWarning("No RequestContent-style UpdateAgentAsync found on Administration.");
                                }
                            }
                            catch (Exception ex)
                            {
                                _logger.LogWarning(ex, "Failed to call RequestContent-based UpdateAgentAsync fallback.");
                            }

                            return false;
                        }
                    }
                }
                catch (TargetInvocationException tie)
                {
                    _logger.LogError(tie.InnerException ?? tie, "UpdateAgentAsync failed when trying to persist updated agent definition.");
                    throw;
                }

                _logger.LogInformation("Successfully updated OpenAPI tool for agent {AgentId} (replaced {Count} tools).", agentId, replaced);
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to update OpenAPI tool for agent {AgentId}", agentId);
                return false;
            }
        }
    }
}
