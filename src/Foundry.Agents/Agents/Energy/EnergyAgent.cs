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

                        // Invoke the agent once to demonstrate functionality and log its response.
                        var demoPromptExisting = AgentPrompts.HighLevelAsk;
                        await RunEnergyAsync(projectEndpoint, agentId, demoPromptExisting, CancellationToken.None);
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

            // Prefer instructions from the shared markdown section, fall back to an inline default.
            var instructions = Foundry.Agents.Agents.InstructionReader.ReadSection("Energy");
            if (string.IsNullOrWhiteSpace(instructions))
            {
                // Inline fallback instructions
                instructions =
                    "You must return only JSON (no extra text) with:\n{\n  \"agent\":\"Energy\", \"thread_id\":\"<string>\", \"task_id\":\"<string>\", \"status\":\"<ok|needs_input|error>\", \"summary\":\"<1-3 sentences; no chain-of-thought>\", \"data\":{}, \"next_actions\":[], \"citations\":[]\n}\n" +
                    "If baseline unknown, call OpenAPI operations DayAheadPrice(zone='SE3',date=today) and WeatherHourly(city='Stockholm',date=today). Use Code Interpreter to estimate baseline.kwh (24h) and three measures with delta_kwh and impact_profile[24]. Enforce 24-item arrays where specified. Currency: SEK; Zone: SE3; Location: Stockholm; TZ: Europe/Stockholm.";
            }

            _logger.LogInformation("Creating EnergyAgent using model deployment {ModelDeployment}", modelDeploymentName);
            try
            {
                // Request both the OpenAPI and Code Interpreter tools for the Energy agent
                _logger.LogInformation("Creating agent with tools: {Tools}", new[] { "openapi", "code_interpreter", "connected:RemoteData" });
                var createdAgentId = await _adapter.CreateAgentAsync(modelDeploymentName, "EnergyAgent", instructions, new[] { "openapi", "code_interpreter", "connected:RemoteData" });
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

                    // Run a demo prompt after creation
                    var demoPrompt = AgentPrompts.HighLevelAsk;
                    await RunEnergyAsync(projectEndpoint, createdAgentId, demoPrompt, CancellationToken.None);
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
        public async Task RunEnergyAsync(string endpoint, string agentId, string userPrompt, CancellationToken cancellationToken = default)
        {
            try
            {
                _logger.LogInformation("Invoking Energy agent {AgentId} with prompt: {Prompt}", agentId, userPrompt);

                var client = new PersistentAgentsClient(endpoint, new DefaultAzureCredential());

                // Thread mapping path for Energy agent
                var threadMappingPath = System.IO.Path.Combine("Agents", "Energy", "threads.json");

                // Get or create thread
                var threadId = await AgentFileHelpers.GetOrCreateThreadIdForAgentAsync(client, agentId, threadMappingPath, _logger, cancellationToken);
                _logger.LogInformation("Using thread {ThreadId} for Energy agent {AgentId}", threadId, agentId);

                // Acquire a simple file lock for the thread
                var dir = System.IO.Path.Combine("Agents", "Energy");
                var lockAcquired = await AgentFileHelpers.AcquireThreadLockAsync(dir, threadId, TimeSpan.FromSeconds(5));
                if (!lockAcquired)
                {
                    _logger.LogWarning("Could not acquire lock for thread {ThreadId}; another process may be running. Aborting run.", threadId);
                    return;
                }

                try
                {
                    // Normalize prompt (replace 'today' tokens)
                    var normalizedPrompt = userPrompt;
                    var todayIso = DateTime.UtcNow.ToString("yyyy-MM-dd");
                    try
                    {
                        normalizedPrompt = normalizedPrompt.Replace("today's", todayIso, StringComparison.OrdinalIgnoreCase);
                        normalizedPrompt = normalizedPrompt.Replace("today", todayIso, StringComparison.OrdinalIgnoreCase);
                    }
                    catch
                    {
                        normalizedPrompt = userPrompt;
                    }

                    _logger.LogDebug("User prompt after normalization: {Prompt}", normalizedPrompt);

                    var textBlock = PersistentAgentsModelFactory.MessageInputTextBlock(normalizedPrompt);
                    var content = BinaryData.FromObjectAsJson(textBlock);
                    var messageResp = await client.Messages.CreateMessageAsync(threadId, MessageRole.User, content.ToString());
                    var message = messageResp?.Value;
                    if (message == null)
                    {
                        _logger.LogError("Failed to create message in thread {ThreadId}", threadId);
                        return;
                    }
                    _logger.LogInformation("Created message {MessageId} in thread {ThreadId}", message.Id, threadId);

                    // Start run
                    var runResp = await client.Runs.CreateRunAsync(threadId, agentId, overrideInstructions: null, cancellationToken: cancellationToken);
                    var run = runResp?.Value;
                    if (run == null)
                    {
                        _logger.LogError("Failed to create run for thread {ThreadId} and agent {AgentId}", threadId, agentId);
                        return;
                    }
                    _logger.LogInformation("Started run {RunId} (status={Status}) for thread {ThreadId}", run.Id, run.Status, threadId);

                    // Poll until finished
                    while (run.Status == RunStatus.Queued || run.Status == RunStatus.InProgress)
                    {
                        _logger.LogDebug("Run {RunId} status {Status} - waiting...", run.Id, run.Status);
                        await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken);
                        var getRunResp = await client.Runs.GetRunAsync(threadId, run.Id, cancellationToken: cancellationToken);
                        run = getRunResp?.Value ?? run;
                    }

                    if (run.Status != RunStatus.Completed)
                    {
                        var runId = run?.Id ?? "<unknown>";
                        var runStatusStr = run?.Status.ToString() ?? "<unknown>";
                        var lastErr = run?.LastError;
                        _logger.LogWarning("Run {RunId} completed with unexpected status {Status}. LastError: {LastError}", runId, runStatusStr, lastErr is null ? "<none>" : (lastErr.Message ?? "<none>"));

                        try
                        {
                            if (lastErr != null)
                            {
                                _logger.LogDebug("Run.LastError details: Code={Code} Message={Message}", lastErr?.Code, lastErr?.Message);
                            }
                        }
                        catch
                        {
                            // ignore
                        }

                        var lastMsg = lastErr?.Message?.ToLowerInvariant() ?? string.Empty;
                        if (lastMsg.Contains("http") || lastMsg.Contains("502") || lastMsg.Contains("504") || lastMsg.Contains("timeout") || lastMsg.Contains("connection") || lastMsg.Contains("5"))
                        {
                            _logger.LogWarning("The failure appears to be an HTTP/transport error. This often means a downstream tool endpoint (for example your OpenAPI Function) was unreachable or returned a 5xx. Verify the Function is running and accessible at the configured OpenAPI endpoint.");
                        }
                    }
                    else
                    {
                        _logger.LogInformation("Run {RunId} completed successfully.", run.Id);
                    }

                    // Retrieve messages for the thread and print outputs
                    _logger.LogDebug("Retrieving messages for thread {ThreadId}", threadId);
                    var messages = client.Messages.GetMessages(threadId);
                    var msgList = messages.ToList();

                    // Detect whether RemoteData (connected agent) was invoked in this thread
                    bool remoteDataObserved = false;
                    foreach (var threadMessage in msgList)
                    {
                        _logger.LogInformation("{CreatedAt:yyyy-MM-dd HH:mm:ss} - {Role}: ", threadMessage.CreatedAt, threadMessage.Role);
                        foreach (var contentItem in threadMessage.ContentItems)
                        {
                            if (contentItem is MessageTextContent textItem)
                            {
                                _logger.LogInformation(textItem.Text);

                                // Heuristics: look for JSON envelope emitted by RemoteData agent
                                var txt = textItem.Text ?? string.Empty;
                                if (txt.Contains("\"agent\": \"RemoteData\"") || txt.Contains("\"agent\":\"RemoteData\"") || txt.Contains("DayAheadPrice") || txt.Contains("WeatherHourly") || txt.Contains("external_signals"))
                                {
                                    remoteDataObserved = true;
                                    _logger.LogInformation("Detected RemoteData activity in thread {ThreadId}: message contains RemoteData envelope or OpenAPI calls.", threadId);
                                }

                                // Detect Energy GlobalEnvelope JSON and handle it once
                                if (txt.Contains("\"agent\": \"Energy\"") || txt.Contains("\"agent\":\"Energy\""))
                                {
                                    // Delegate saving and plotting to a single helper for clarity
                                    try
                                    {
                                        SaveEnergyOutputAndPlot(txt);
                                    }
                                    catch (Exception ex)
                                    {
                                        _logger.LogWarning(ex, "Failed to save or plot Energy GlobalEnvelope");
                                    }
                                }
                            }
                            else if (contentItem is MessageImageFileContent imageFileItem)
                            {
                                _logger.LogInformation("<image from ID: {FileId}>", imageFileItem.FileId);
                            }
                            else
                            {
                                _logger.LogInformation("(unhandled content item type: {Type})", contentItem.GetType().Name);
                            }
                        }
                    }

                    if (!remoteDataObserved)
                    {
                        _logger.LogWarning("No RemoteData assistant message or OpenAPI call was observed in the Energy thread {ThreadId}. If you expected RemoteData to be invoked, increase logging or verify the connected agent was attached to Energy.", threadId);
                    }
                    else
                    {
                        _logger.LogInformation("RemoteData was observed in the Energy thread {ThreadId}.", threadId);
                    }

                    // Rotate thread if it grows too large
                    const int rotateThreshold = 100;
                    if (msgList.Count > rotateThreshold)
                    {
                        _logger.LogInformation("Thread {ThreadId} exceeded {Threshold} messages; creating a new thread and updating mapping.", threadId, rotateThreshold);
                        var newThreadResp = await client.Threads.CreateThreadAsync(new System.Collections.Generic.List<ThreadMessageOptions>());
                        var newThread = newThreadResp?.Value;
                        if (newThread != null)
                        {
                            var mapping = await AgentFileHelpers.ReadThreadMappingAsync(threadMappingPath, _logger);
                            mapping[agentId] = newThread.Id;
                            await AgentFileHelpers.SaveThreadMappingAsync(threadMappingPath, mapping, _logger);
                            _logger.LogInformation("Rotated thread for agent {AgentId}: {OldThread} -> {NewThread}", agentId, threadId, newThread.Id);
                        }
                    }
                }
                finally
                {
                    AgentFileHelpers.ReleaseThreadLock(dir, threadId, _logger);
                }
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
                var doc = JsonDocument.Parse(jsonText);
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
    }
}
