using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;
using Azure.AI.Agents.Persistent;
using Azure.Identity;
using System.Collections.Generic;
using System.Text.Json;
using Azure;
using Foundry.Agents.Agents;

namespace Foundry.Agents.Agents.RemoteData
{
    public class RemoteDataAgent
    {
        private readonly ILogger<RemoteDataAgent> _logger;
        private readonly IConfiguration _configuration;
        private readonly IPersistentAgentsClientAdapter _agentsAdapter;

        public RemoteDataAgent(ILogger<RemoteDataAgent> logger, IConfiguration configuration)
            : this(logger, configuration, null)
        {
        }

        public RemoteDataAgent(ILogger<RemoteDataAgent> logger, IConfiguration configuration, IPersistentAgentsClientAdapter? agentsAdapter)
        {
            _logger = logger;
            _configuration = configuration;
            _agentsAdapter = agentsAdapter!; // may be null initially; we will handle in InitializeAsync
        }

        // Instructions are kept as a separate markdown file for easy editing by non-developers.
        public string Instructions => Foundry.Agents.Agents.InstructionReader.ReadSection("RemoteData");

        public async Task InitializeAsync(CancellationToken cancellationToken = default)
        {
            _logger.LogInformation("RemoteDataAgent initializing using persistent agents adapter.");

            var endpoint = Environment.GetEnvironmentVariable("PROJECT_ENDPOINT") ?? _configuration["Project:Endpoint"];
            var modelDeploymentName = Environment.GetEnvironmentVariable("MODEL_DEPLOYMENT_NAME") ?? _configuration["Project:ModelDeploymentName"];

            if (string.IsNullOrWhiteSpace(endpoint) || string.IsNullOrWhiteSpace(modelDeploymentName))
            {
                _logger.LogWarning("PROJECT_ENDPOINT or MODEL_DEPLOYMENT_NAME not configured. Skipping agent initialization.");
                return;
            }

            _logger.LogDebug("Using endpoint {Endpoint} and model {Model}", endpoint, modelDeploymentName);

            try
            {
                // If adapter wasn't provided via DI (tests), construct the real adapter using config/DefaultAzureCredential.
                IPersistentAgentsClientAdapter adapter = _agentsAdapter ?? new Foundry.Agents.Agents.Shared.RealPersistentAgentsClientAdapter(endpoint, _configuration, null);

                // If an agent id is provided, check existence.
                var providedAgentId = Environment.GetEnvironmentVariable("PROJECT_AGENT_ID") ?? _configuration["Project:AgentId"];
                if (!string.IsNullOrEmpty(providedAgentId))
                {
                    _logger.LogInformation("Verifying provided agent id {AgentId}", providedAgentId);
                    if (await adapter.AgentExistsAsync(providedAgentId))
                    {
                        _logger.LogInformation("Agent {AgentId} exists.", providedAgentId);
                        await AgentFileHelpers.PersistAgentIdAsync(providedAgentId, _configuration, _logger, "RemoteData");
                        // After ensuring the agent exists, run a single prompt to demonstrate functionality and log the response.
                        var demoPrompt = "Fetch today's SE3 price and Stockholm hourly temperature. Return the JSON envelope only";
                        await RunAgentAsync(endpoint, providedAgentId, demoPrompt, cancellationToken);
                        return;
                    }
                    _logger.LogWarning("Provided agent id {AgentId} not found; will create a new agent.", providedAgentId);
                }

                // Create agent
                var instructions =
                    "You must return only JSON (no extra text) with:\n{\n  \"agent\":\"RemoteData\", \"thread_id\":\"<string>\", \"task_id\":\"<string>\", \"status\":\"<ok|needs_input|error>\", \"summary\":\"<1-3 sentences; no chain-of-thought>\", \"data\":{}, \"next_actions\":[], \"citations\":[]\n}\n" +
                    "ROLE: Acquire real 24h SE3 price + Stockholm weather by invoking the OpenAPI tool 'external_signals' (DayAheadPrice, WeatherHourly).\n" +
                    "WORKFLOW: Call DayAheadPrice(zone=SE3,date=today) then WeatherHourly(city=Stockholm,date=today). Validate each response has exactly 24 numeric values; retry a failing operation once. Produce only the final JSON envelope (no prose or code fences).\n" +
                    "RULES: Never fabricate values; if both calls fail return status=error and empty data; if one succeeds include only that dataset; thread_id must match context; no chain-of-thought.\n" +
                    "DIAGNOSTICS: On error include diagnostics {refusal_reason_code, refusal_reason_raw, attempted_operations, must_call_tools_before_refusal}.\n" +
                    "OUTPUT: Emit only JSON with fields: agent, thread_id, task_id(\"remote-phase-1\"), status, summary, data (day_ahead_price_sek_per_kwh, temperature_c, source:'function'), next_actions, citations.";

                var newAgentId = await adapter.CreateAgentAsync(modelDeploymentName, "RemoteDataAgent", instructions, new[] { "openapi" });
                if (!string.IsNullOrEmpty(newAgentId))
                {
                    _logger.LogInformation("Created agent with id {AgentId}", newAgentId);
                    await AgentFileHelpers.PersistAgentIdAsync(newAgentId, _configuration, _logger, "RemoteData");
                    // Run demo prompt against the newly created agent and log its response
                    var demoPrompt = "Fetch today's SE3 price and Stockholm hourly temperature. Return the JSON envelope only";
                    await RunAgentAsync(endpoint, newAgentId, demoPrompt, cancellationToken);
                }
                else
                {
                    _logger.LogError("Agent creation via adapter returned null or empty id.");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error while running persistent agent flow");
            }
        }

        // Path to store agent->thread mapping
        private string ThreadMappingPath => System.IO.Path.Combine("Agents", "RemoteData", "threads.json");

        private async Task RunAgentAsync(string endpoint, string agentId, string userPrompt, CancellationToken cancellationToken)
        {
            try
            {
                _logger.LogInformation("Invoking agent {AgentId} with prompt: {Prompt}", agentId, userPrompt);

                var client = new PersistentAgentsClient(endpoint, new DefaultAzureCredential());

                // Step 2: Get or create a thread for this agent (persisted mapping)
                var threadId = await Foundry.Agents.Agents.AgentFileHelpers.GetOrCreateThreadIdForAgentAsync(client, agentId, ThreadMappingPath, _logger, cancellationToken);
                _logger.LogInformation("Using thread {ThreadId} for agent {AgentId}", threadId, agentId);

                // Acquire a simple file lock for the thread to avoid concurrent runs
                var dir = System.IO.Path.Combine("Agents", "RemoteData");
                var lockAcquired = await Foundry.Agents.Agents.AgentFileHelpers.AcquireThreadLockAsync(dir, threadId, TimeSpan.FromSeconds(5));
                if (!lockAcquired)
                {
                    _logger.LogWarning("Could not acquire lock for thread {ThreadId}; another process may be running. Aborting run.", threadId);
                    return;
                }

                try
                {
                    // Step 3: Add a message to the thread
                    _logger.LogDebug("Preparing user message for thread {ThreadId}", threadId);
                    // Replace human-friendly 'today' tokens with an explicit ISO date to avoid downstream parsing errors in the Function.
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

                    // Step 4: Run the agent
                    _logger.LogDebug("Starting run for agent {AgentId} on thread {ThreadId}", agentId, threadId);
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

                        // If the run exposed structured last-error details, log them at debug level for diagnostics
                        try
                        {
                            if (lastErr != null)
                            {
                                _logger.LogDebug("Run.LastError details: Code={Code} Message={Message}", lastErr?.Code, lastErr?.Message);
                            }
                        }
                        catch
                        {
                            // Ignore any reflection/shape differences when reading LastError
                        }

                        // Heuristics: if the error message or code looks like an HTTP/transport error, suggest the OpenAPI tool (function) as a possible root cause
                        var lastMsg = lastErr?.Message?.ToLowerInvariant() ?? string.Empty;
                        if (lastMsg.Contains("http") || lastMsg.Contains("502") || lastMsg.Contains("504") || lastMsg.Contains("bad gateway") || lastMsg.Contains("timeout") || lastMsg.Contains("connection") || lastMsg.Contains("proxy") || lastMsg.Contains("5"))
                        {
                            _logger.LogWarning("The failure appears to be an HTTP/transport error. This often means a downstream tool endpoint (for example your OpenAPI Function) was unreachable or returned a 5xx. Verify the Function is running and accessible at the configured OpenAPI endpoint (OpenApi:BaseUrl) or check the inline OpenAPI tool URLs and logs.");
                        }
                    }
                    else
                    {
                        _logger.LogInformation("Run {RunId} completed successfully.", run.Id);
                    }

                    // Retrieve messages for the thread and print outputs
                    _logger.LogDebug("Retrieving messages for thread {ThreadId}", threadId);
                    var messages = client.Messages.GetMessages(threadId);
                    // Simple rotation: if message count grows beyond threshold, create a new thread for future runs
                    var msgList = messages.ToList();
                    foreach (var threadMessage in msgList)
                    {
                        _logger.LogInformation("{CreatedAt:yyyy-MM-dd HH:mm:ss} - {Role}: ", threadMessage.CreatedAt, threadMessage.Role);
                        foreach (var contentItem in threadMessage.ContentItems)
                        {
                            if (contentItem is MessageTextContent textItem)
                            {
                                _logger.LogInformation(textItem.Text);
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

                    // If history length exceeds threshold, rotate to a new thread to avoid unbounded history
                    const int rotateThreshold = 100;
                    if (msgList.Count > rotateThreshold)
                    {
                        _logger.LogInformation("Thread {ThreadId} exceeded {Threshold} messages; creating a new thread and updating mapping.", threadId, rotateThreshold);
                        var newThreadResp = await client.Threads.CreateThreadAsync(new System.Collections.Generic.List<ThreadMessageOptions>());
                        var newThread = newThreadResp?.Value;
                        if (newThread != null)
                        {
                            var mapping = await Foundry.Agents.Agents.AgentFileHelpers.ReadThreadMappingAsync(ThreadMappingPath, _logger);
                            mapping[agentId] = newThread.Id;
                            await Foundry.Agents.Agents.AgentFileHelpers.SaveThreadMappingAsync(ThreadMappingPath, mapping, _logger);
                            _logger.LogInformation("Rotated thread for agent {AgentId}: {OldThread} -> {NewThread}", agentId, threadId, newThread.Id);
                        }
                    }
                }
                finally
                {
                    Foundry.Agents.Agents.AgentFileHelpers.ReleaseThreadLock(System.IO.Path.Combine("Agents", "RemoteData"), threadId, _logger);
                }
            }
            catch (RequestFailedException rf)
            {
                _logger.LogError(rf, "Persistent agents service returned an error when running agent {AgentId}: {Message}", agentId, rf.Message);
                // If this is an HTTP 5xx error from the service layer, hint that the downstream tool (Function) may be implicated
                try
                {
                    if (rf.Status >= 500)
                    {
                        _logger.LogWarning("Request to Persistent Agents service failed with status {Status}. This may indicate a downstream service or tool (e.g. your OpenAPI Function) is returning 5xx or is unreachable. Check the Function's logs and the configured OpenAPI base URL.", rf.Status);
                    }
                }
                catch
                {
                    // Ignore if Status is not available
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                _logger.LogWarning("Run cancelled for agent {AgentId}", agentId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unexpected error while invoking agent {AgentId}", agentId);
            }
        }
    }
}
