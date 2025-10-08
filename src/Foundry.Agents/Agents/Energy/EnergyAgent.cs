using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Foundry.Agents.Agents;
using Foundry.Agents.Agents.RemoteData;
using System.Threading;
using Azure.AI.Agents.Persistent;
using Azure.Identity;
using System.Linq;
using Azure;
using System.Text.Json;
using System.Diagnostics;

namespace Foundry.Agents.Agents.Energy
{
    /// <summary>
    /// EnergyAgent orchestrates the energy analysis demo.
    /// Demo flow:
    /// 1. Ensure agent exists (create if needed) and verify attached tools
    /// 2. Send a prompt to the agent to compute baseline and measures
    /// 3. Retrieve messages, detect the Energy GlobalEnvelope JSON and persist it
    /// 4. Trigger the plotting script to produce a timestamped PNG for demo
    ///
    /// For presentation we keep verbose, structured logs at each step so the audience
    /// can follow along easily.
    /// </summary>
    public class EnergyAgent
    {
        private readonly IPersistentAgentsClientAdapter _adapter;
        private readonly IConfiguration _configuration;
        private readonly ILogger<EnergyAgent> _logger;

        public EnergyAgent(IPersistentAgentsClientAdapter adapter, IConfiguration configuration, ILogger<EnergyAgent> logger)
        {
            _adapter = adapter ?? throw new ArgumentNullException(nameof(adapter));
            _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public async Task InitializeAsync()
        {
            var projectEndpoint = _configuration["Project:Endpoint"] ?? string.Empty;
            var modelDeploymentName = _configuration["Project:ModelDeploymentName"] ?? _configuration["Model:DeploymentName"] ?? throw new InvalidOperationException("Configuration 'Project:ModelDeploymentName' is required");

            // Read persisted agent id (Key Vault or local file) using shared helpers
            var agentId = await AgentFileHelpers.ReadPersistedAgentIdAsync(_configuration, "Energy", _logger);

            if (!string.IsNullOrEmpty(agentId))
            {
                try
                {
                    var exists = await _adapter.AgentExistsAsync(agentId);
                    if (exists)
                    {
                        _logger.LogInformation("EnergyAgent already exists: {AgentId}", agentId);

                        // Verify attached tools on the agent via Administration.GetAgentAsync
                        try
                        {
                            var client = new PersistentAgentsClient(projectEndpoint, new DefaultAzureCredential());
                            var adminResp = await client.Administration.GetAgentAsync(agentId);
                            var agentDef = adminResp?.Value;
                            if (agentDef != null && agentDef.Tools != null)
                            {
                                _logger.LogInformation("Agent {AgentId} has {Count} tools attached:", agentId, agentDef.Tools.Count);
                                foreach (var t in agentDef.Tools)
                                {
                                    try
                                    {
                                        var tType = t.GetType().Name;
                                        string tName = tType;
                                        // common OpenApiToolDefinition has a 'Name' property
                                        var prop = t.GetType().GetProperty("Name");
                                        if (prop != null)
                                        {
                                            var val = prop.GetValue(t) as string;
                                            if (!string.IsNullOrEmpty(val)) tName = val + " (" + tType + ")";
                                        }

                                        _logger.LogInformation(" - Tool: {Tool}", tName);
                                    }
                                    catch (Exception ex)
                                    {
                                        _logger.LogDebug(ex, "Failed to reflect tool details");
                                        _logger.LogInformation(" - Tool type: {ToolType}", t.GetType().FullName);
                                    }
                                }
                            }
                            else
                            {
                                _logger.LogWarning("Could not read tools for agent {AgentId}; agentDef or Tools was null.", agentId);
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "Failed to call Administration.GetAgentAsync to inspect attached tools for agent {AgentId}", agentId);
                        }
                        // Persisted agent exists and we've attempted verification; nothing further to do.
                        return;
                    }
                    else
                    {
                        _logger.LogInformation("Agent id present but agent not found on server. Will create a new Energy agent.");
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Error while checking existing agent existence; will attempt to create a new agent");
                }
            }

            // Prefer instructions from a per-agent markdown (Agents/Energy/EnergyInstructions.md) or the shared RemoteData file.
            var instructions = Foundry.Agents.Agents.InstructionReader.ReadSection("Energy");
            if (string.IsNullOrWhiteSpace(instructions))
            {
                // Small, safe fallback to avoid creating an empty agent. The full instructions should be placed in
                // Agents/Energy/EnergyInstructions.md so they are loaded at initialization.
                instructions = "You are Energy. Return exactly one JSON object with fields: \"agent\", \"thread_id\", \"task_id\", \"status\", \"summary\", \"data\", \"next_actions\", \"citations\". Required inputs: zone, city, date (yyyy-MM-dd). Use RemoteDataAgent for price/weather. If required tools did not run, return status 'needs_input' or 'error'.";
            }

                _logger.LogInformation("Creating EnergyAgent using model deployment {ModelDeployment}", modelDeploymentName);
            try
            {
                // Request only the Code Interpreter tool for the Energy agent. External data should
                // come from the RemoteData agent, not via an OpenAPI tool attached to Energy.
                _logger.LogInformation("Creating agent with tools: {Tools}", new[] { "code_interpreter" });
                // Postfix agent name with AF for Ai Foundry demo instances. Persist ID under folder 'Energy'.
                var createName = "EnergyAgentAF";
                var createdAgentId = await _adapter.CreateAgentAsync(modelDeploymentName, createName, instructions, new[] { "code_interpreter" });
                if (!string.IsNullOrEmpty(createdAgentId))
                {
                    await AgentFileHelpers.PersistAgentIdAsync(createdAgentId, _configuration, _logger, "Energy");
                    _logger.LogInformation("EnergyAgent created and persisted with id {AgentId}", createdAgentId);

                    // Verify the created agent's tools
                    try
                    {
                        var client = new PersistentAgentsClient(projectEndpoint, new DefaultAzureCredential());
                        var adminResp = await client.Administration.GetAgentAsync(createdAgentId);
                        var agentDef = adminResp?.Value;
                        if (agentDef != null && agentDef.Tools != null)
                        {
                            _logger.LogInformation("Created agent {AgentId} has {Count} tools attached:", createdAgentId, agentDef.Tools.Count);
                            foreach (var t in agentDef.Tools)
                            {
                                try
                                {
                                    var tType = t.GetType().Name;
                                    string tName = tType;
                                    var prop = t.GetType().GetProperty("Name");
                                    if (prop != null)
                                    {
                                        var val = prop.GetValue(t) as string;
                                        if (!string.IsNullOrEmpty(val)) tName = val + " (" + tType + ")";
                                    }
                                    _logger.LogInformation(" - Tool: {Tool}", tName);
                                }
                                catch (Exception ex)
                                {
                                    _logger.LogDebug(ex, "Failed to reflect tool details");
                                    _logger.LogInformation(" - Tool type: {ToolType}", t.GetType().FullName);
                                }
                            }
                        }
                        else
                        {
                            _logger.LogWarning("Could not read tools for created agent {AgentId}; agentDef or Tools was null.", createdAgentId);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to call Administration.GetAgentAsync to inspect attached tools for agent {AgentId}", createdAgentId);
                    }
                }
                else
                {
                    _logger.LogWarning("CreateAgentAsync returned null agent id");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to create EnergyAgent");
                throw;
            }
        }

        // Full CreateRun -> poll -> retrieve messages flow for Energy agent
    public async Task<object?> RunEnergyAsync(string endpoint, string agentId, CancellationToken cancellationToken = default, object? initialPayload = null, string? threadId = null, string? threadLockDirectory = null)
        {
            _logger.LogInformation("Invoking Energy agent {AgentId}", agentId);
            try
            {
                // Normalize incoming initialPayload so the assistant sees the expected `data` object.
                object? extractedData = null;
                try
                {
                    if (initialPayload != null)
                    {
                        // If initialPayload is a string that contains a fenced JSON block, sanitize it first
                        if (initialPayload is string s)
                        {
                            var cleaned = SanitizeJsonText(s);
                            // Try to parse the cleaned string into an object
                            try
                            {
                                var parsed = Newtonsoft.Json.JsonConvert.DeserializeObject<object?>(cleaned);
                                initialPayload = parsed ?? cleaned;
                            }
                            catch
                            {
                                // keep original cleaned string
                                initialPayload = cleaned;
                            }
                        }

                        // Serialize/deserialize to a dictionary to inspect keys reliably
                        var json = Newtonsoft.Json.JsonConvert.SerializeObject(initialPayload);
                        var dict = Newtonsoft.Json.JsonConvert.DeserializeObject<System.Collections.Generic.Dictionary<string, object?>>(json);
                        if (dict != null)
                        {
                            // Common locations: input.data, data, Data
                            if (dict.TryGetValue("input", out var inputObj) && inputObj != null)
                            {
                                try
                                {
                                    var inputJson = Newtonsoft.Json.JsonConvert.SerializeObject(inputObj);
                                    var inputDict = Newtonsoft.Json.JsonConvert.DeserializeObject<System.Collections.Generic.Dictionary<string, object?>>(inputJson);
                                    if (inputDict != null && inputDict.TryGetValue("data", out var d) && d != null)
                                    {
                                        extractedData = d;
                                    }
                                }
                                catch { }
                            }

                            if (extractedData == null)
                            {
                                if (dict.TryGetValue("data", out var d2) && d2 != null) extractedData = d2;
                                else if (dict.TryGetValue("Data", out var d3) && d3 != null) extractedData = d3;
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Energy: failed to normalize initial payload");
                }

                // Build payload: include normalized prompt, original initialPayload under 'input', and extractedData under 'data' if found
                var payloadDict = new System.Collections.Generic.Dictionary<string, object?>();
                //payloadDict["prompt"] = normalizedPrompt;
                payloadDict["input"] = initialPayload;
                if (extractedData != null) payloadDict["data"] = extractedData;
                var payload = payloadDict as object;
                _logger.LogDebug("Energy payload normalized. HasData={HasData}", extractedData != null);

                // Ask the adapter to run the persisted agent with the payload (single call, no pre-posted messages)
                var assistantText = await _adapter.RunAgentAsync(agentId, payload, cancellationToken);

                object? parsedResult = null;
                if (!string.IsNullOrWhiteSpace(assistantText))
                {
                    try
                    {
                        SaveEnergyOutputAndPlot(assistantText);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to save or plot latest Energy GlobalEnvelope");
                    }

                    try
                    {
                        var cleaned = SanitizeJsonText(assistantText);
                        parsedResult = Newtonsoft.Json.JsonConvert.DeserializeObject<object?>(cleaned) ?? cleaned;
                    }
                    catch
                    {
                        parsedResult = assistantText;
                    }
                }
                return parsedResult;
            }
            catch (RequestFailedException rf)
            {
                _logger.LogError(rf, "Persistent agents service returned an error when running agent {AgentId}: {Message}", agentId, rf.Message);
                try
                {
                    if (rf.Status >= 500)
                    {
                        _logger.LogWarning("Request to Persistent Agents service failed with status {Status}. This may indicate a downstream service or tool (e.g. your OpenAPI Function) is returning 5xx or is unreachable. Check the Function's logs and the configured OpenAPI base URL.", rf.Status);
                    }
                }
                catch
                {
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                _logger.LogWarning("Run cancelled for agent {AgentId}", agentId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unexpected error while invoking Energy agent {AgentId}", agentId);
            }
            return null;
        }

        // Placeholder for the orchestration run logic that will call tools and the code interpreter.
        // Implement RunEnergyAsync next: fetch missing inputs via external_signals, prepare a deterministic
        // Python snippet and send it to the Code Interpreter tool, validate output JSON envelope, persist thread mapping.
        public Task RunEnergyAsync()
        {
            _logger.LogInformation("RunEnergyAsync not yet implemented");
            return Task.CompletedTask;
        }

        /// <summary>
        /// Save the Energy GlobalEnvelope JSON to docs/last_agent_output.json (pretty printed)
        /// and invoke the plotting script to create a timestamped PNG. This helper isolates
        /// filesystem and process interactions so the message loop remains easy to read.
        /// </summary>
        private void SaveEnergyOutputAndPlot(string jsonText)
        {
            try
            {
                var cleaned = SanitizeJsonText(jsonText);
                _logger.LogDebug("Sanitized Energy output (first 200 chars): {Preview}", cleaned?.Length > 200 ? cleaned.Substring(0,200) : cleaned);
                var doc = JsonDocument.Parse(cleaned ?? string.Empty);
                var formatted = JsonSerializer.Serialize(doc.RootElement, new JsonSerializerOptions { WriteIndented = true });
                var outDir = System.IO.Path.Combine("docs");
                System.IO.Directory.CreateDirectory(outDir);
                var outPath = System.IO.Path.Combine(outDir, "last_agent_output.json");
                System.IO.File.WriteAllText(outPath, formatted);
                _logger.LogInformation("Saved Energy GlobalEnvelope (pretty JSON) to {Path}", outPath);

                // Run the plotting script and capture output for demo exposition
                // Resolve script path relative to the current working directory (robust in dev & CI)
                var scriptPath = System.IO.Path.GetFullPath(System.IO.Path.Combine(System.IO.Directory.GetCurrentDirectory(), "docs", "energy_measures_plot.py"));
                var jsonPath = System.IO.Path.GetFullPath(outPath);

                if (!System.IO.File.Exists(scriptPath))
                {
                    _logger.LogWarning("Plot script not found at {ScriptPath}. Skipping plot. Current directory: {Cwd}", scriptPath, System.IO.Directory.GetCurrentDirectory());
                    return;
                }

                var psi = new ProcessStartInfo
                {
                    FileName = "python",
                    Arguments = $"\"{scriptPath}\" \"{jsonPath}\"",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                using var proc = Process.Start(psi);
                if (proc != null)
                {
                    var stdout = proc.StandardOutput.ReadToEnd();
                    var stderr = proc.StandardError.ReadToEnd();
                    proc.WaitForExit(60000);

                    if (!string.IsNullOrWhiteSpace(stdout)) _logger.LogInformation("Plot script output:\n{Stdout}", stdout);
                    if (!string.IsNullOrWhiteSpace(stderr)) _logger.LogWarning("Plot script errors:\n{Stderr}", stderr);
                }
                else
                {
                    _logger.LogWarning("Failed to start plot script process (Process.Start returned null)");
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to parse, save, or plot Energy GlobalEnvelope JSON");
            }
        }

    // Remove common Markdown code fences and surrounding backticks so JSON can be parsed robustly.
    public static string SanitizeJsonText(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return raw ?? string.Empty;
            var txt = raw.Trim();

            try
            {
                // If the assistant returned a fenced block like ```json\n{...}\n```
                var firstFence = txt.IndexOf("```");
                if (firstFence >= 0)
                {
                    var startLineEnd = txt.IndexOf('\n', firstFence);
                    if (startLineEnd >= 0)
                    {
                        var lastFence = txt.LastIndexOf("```");
                        if (lastFence > startLineEnd)
                        {
                            var inner = txt.Substring(startLineEnd + 1, lastFence - (startLineEnd + 1));
                            return inner.Trim();
                        }
                    }
                }

                // If it's wrapped with single backticks around whole value: `...`
                if (txt.Length >= 2 && txt[0] == '`' && txt[txt.Length - 1] == '`')
                {
                    return txt.Trim('`').Trim();
                }

                // Otherwise return trimmed text
                return txt;
            }
            catch
            {
                return txt;
            }
        }
    }
}
