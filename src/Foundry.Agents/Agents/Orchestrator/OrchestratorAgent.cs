using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Agents.AI.Workflows;
using Azure.AI.Agents.Persistent;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Newtonsoft.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
//using Microsoft.Agents.AI.Workflows.Events; // bring WorkflowEvent, AgentRunUpdateEvent

namespace Foundry.Agents.Agents.Orchestrator
{
    public class OrchestratorAgent
    {
        private readonly ILogger<OrchestratorAgent> _logger;
        private readonly Microsoft.Extensions.Configuration.IConfiguration _configuration;

        public OrchestratorAgent(ILogger<OrchestratorAgent> logger, Microsoft.Extensions.Configuration.IConfiguration configuration)
        {
            _logger = logger;
            _configuration = configuration;
        }

        // Run the orchestration for a given zone/city/date. Returns the final GlobalEnvelope-like JSON string.
        public async Task<string> RunAsync(string zone, string city, string date)
        {
            // Build workflow executors that call into existing agents.
            // Executors are created per-run to capture the run-specific inputs (zone/city/date).
            _logger.LogInformation("Running Agents");


            // Create FunctionExecutor instances from Func handlers.
            // FunctionExecutor<TInput> constructor: (string id, Func<TInput, IWorkflowContext, CancellationToken, ValueTask> handler, ExecutorOptions options?)
            // Handler for the RemoteData agent (previously called 'signals')
            // Thread mapping and local lock directories removed — orchestration uses the agent framework only.
            // Generate a run id early so handler closures can persist run-scoped diagnostic files
            var runId = Guid.NewGuid().ToString();

            // Simple sequential orchestration: call RemoteData then Energy using their persisted agent ids.
            // Use the DI-registered adapter when running against a non-HTTPS local endpoint to avoid DefaultAzureCredential bearer-token-on-http errors.
            var endpoint = System.Environment.GetEnvironmentVariable("PROJECT_ENDPOINT") ?? "http://localhost:3000";
            // Create a PersistentAgentsClient for the provided endpoint. When PROJECT_ENDPOINT is https, DefaultAzureCredential will be used.
            var persistentAgentsClient = new Azure.AI.Agents.Persistent.PersistentAgentsClient(endpoint, new Azure.Identity.DefaultAzureCredential());

            // Agent ids (explicitly using known demo ids). Prefer env override.
            var remoteDataAgentId = System.Environment.GetEnvironmentVariable("REMOTE_DATA_AGENT_ID") ?? "asst_dMBVMGkg3nbkoarVWsRJNcxV";
            var energyAgentId = System.Environment.GetEnvironmentVariable("ENERGY_AGENT_ID") ?? "asst_WKrLKVRE5G1WEy6XPzWwG1ls";

            // Ensure the RemoteData agent exists on the target persistent agents service. Create if missing.
            AIAgent? remoteData = await Foundry.Agents.Agents.RemoteData.RemoteDataAgent.GetOrCreateAIAgentAsync(endpoint, _configuration, _logger);
            if (remoteData == null)
            {
                _logger.LogError("Failed to obtain or create RemoteData agent. Aborting orchestration.");
                return JsonConvert.SerializeObject(new { error = "Failed to obtain RemoteData agent" });
            }

            // Ensure the Energy agent exists (create if necessary) using the same helper pattern as RemoteData
            var energyAIAgent = await Foundry.Agents.Agents.Energy.EnergyAgent.GetOrCreateAIAgentAsync(endpoint, _configuration, _logger);
            if (energyAIAgent == null)
            {
                _logger.LogError("Failed to obtain or create Energy agent. Aborting orchestration.");
                return JsonConvert.SerializeObject(new { error = "Failed to obtain Energy agent" });
            }
            var energy = energyAIAgent;

            _logger.LogInformation($"remote data agent: {remoteData.DisplayName}");
            _logger.LogInformation($"energy agent: {energy.DisplayName}");

            // Build: RemoteData -> Energy, and take final output from Energy
            //var workflow = new WorkflowBuilder(remoteData)
            //    .AddEdge(remoteData, energy)
            //    .Build();
            
            // Use the convenience builder which wires a sequential agent pipeline and the TurnToken/Output executor correctly.
            var workflow = AgentWorkflowBuilder.BuildSequential(new[] { remoteData, energy });
            _logger.LogInformation("Workflow initialized");

            // Execute the workflow using the streaming API.
            // Capture the last agent update data into resultJson and return it.
            string? resultJson = null;
            
            // Prepare a simple run input object. Do not inject thread ids; agents should not rely on local thread files.
            var payload = new {
                task_id = "remote-phase-1",      // optional but useful to include
                zone = "SE3",
                city = "Stockholm",
                date = "2025-10-01"
            };
            var runPrompt = JsonConvert.SerializeObject(payload);
            _logger.LogInformation($"Running workflow with prompt: {runPrompt}");

            // Use a single ChatMessage so the underlying client sends one content item (avoids content array splitting)
            var run = await InProcessExecution.StreamAsync(workflow, new Microsoft.Extensions.AI.ChatMessage(Microsoft.Extensions.AI.ChatRole.User, runPrompt));
            //var test = await InProcessExecution.RunAsync(workflow, runPrompt);
            
            // Execute the workflow using the streaming API.
            // Capture the last agent update data into resultJson and return it.
            //var run = await InProcessExecution.StreamAsync(workflow, new Microsoft.Agents.AI.ChatMessage(Microsoft.Agents.AI.ChatRole.User, "Compute a deterministic baseline and three energy-saving measures for zone SE3 in Stockholm on 2025-10-01. Send the summary by email to kapeltol@microsoft.com. Return only the GlobalEnvelope JSON."));

            // Helper: robustly serialize workflow output data to a JSON string even when runtime types differ.
            static string SerializeData(object? dataVal)
            {
                if (dataVal == null) return string.Empty;
                if (dataVal is string ss) return ss;

                // If it's an IEnumerable, extract item fields reflectively (e.g., ChatMessage has 'Role' and 'Content')
                if (dataVal is System.Collections.IEnumerable enumerable)
                {
                    var list = new System.Collections.Generic.List<object?>();
                    foreach (var item in enumerable)
                    {
                        if (item == null) { list.Add(null); continue; }

                        if (item is string sItem) { list.Add(sItem); continue; }

                        var itType = item.GetType();
                        var contentProp = itType.GetProperty("Content");
                        var roleProp = itType.GetProperty("Role");
                        if (contentProp != null)
                        {
                            var contentVal = contentProp.GetValue(item)?.ToString();
                            var roleVal = roleProp?.GetValue(item)?.ToString();
                            list.Add(new { role = roleVal, content = contentVal });
                            continue;
                        }

                        // Fallback: use ToString()
                        list.Add(item?.ToString());
                    }

                    try { return JsonConvert.SerializeObject(list); }
                    catch { return string.Join("\n", System.Linq.Enumerable.Select(list, x => x?.ToString() ?? string.Empty)); }
                }

                // Fallback: try to serialize the object
                try { return JsonConvert.SerializeObject(dataVal); }
                catch { return dataVal.ToString() ?? string.Empty; }
            }

            // Must send the turn token to trigger the agents.
            // The agents are wrapped as executors. When they receive messages,
            // they will cache the messages and only start processing when they receive a TurnToken.
            // Send the TurnToken (emit events) to kick the workflow into executing the agent runs.
            await run.TrySendMessageAsync(new TurnToken(emitEvents: true)).ConfigureAwait(false);

            // Prepare a list to collect compact event lines for a clean run events log
            var compactLines = new System.Collections.Generic.List<string>();
            // Track per-executor best outputs (largest fragment seen) so we can summarize each agent's output
            var executorOutputs = new System.Collections.Generic.Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            string? remoteDataPreview = null;
            string? energyPreview = null;

            // Local helper: build a compact event line for the clean events log
            string BuildEventLine(WorkflowEvent e)
            {
                var t = e?.GetType().Name ?? "<null>";
                string exec = "";
                try
                {
                    var pid = e?.GetType().GetProperty("ExecutorId");
                    if (pid != null)
                    {
                        var v = pid.GetValue(e) as string;
                        exec = v ?? string.Empty;
                    }
                }
                catch { }

                // Try to extract a short preview from Data if present
                string preview = string.Empty;
                try
                {
                    var dp = e?.GetType().GetProperty("Data");
                    if (dp != null)
                    {
                        var dv = dp.GetValue(e);
                        if (dv != null)
                        {
                            var s = SerializeData(dv);
                            if (!string.IsNullOrEmpty(s))
                            {
                                preview = s.Length > 200 ? s.Substring(0, 200).Replace("\n", " ") + "…" : s.Replace("\n", " ");
                            }
                        }
                    }
                }
                catch { }

                return $"[{DateTime.UtcNow:O}] {t} | Executor={exec} | Preview={preview}";
            }

            await foreach (WorkflowEvent evt in run.WatchStreamAsync().ConfigureAwait(false))
            {
                // Add compact representation for this event
                try { compactLines.Add(BuildEventLine(evt)); } catch { }
                try
                {
                    var evtType = evt?.GetType().Name ?? "<null>";
                    // Keep the event-level trace at Debug to avoid noisy Info logs for every small fragment.
                    _logger.LogDebug("Workflow event: {EventType}", evtType);

                    // Early reflective capture: if the event has a 'Data' property, try to serialize and prefer larger candidates.
                    try
                    {
                        var dataPropEarly = evt?.GetType().GetProperty("Data");
                        if (dataPropEarly != null)
                        {
                            var dataValEarly = dataPropEarly.GetValue(evt);
                            if (dataValEarly != null)
                            {
                                string earlyStr = SerializeData(dataValEarly);
                                if (!string.IsNullOrEmpty(earlyStr))
                                {
                                    var previewEarly = earlyStr.Length > 500 ? earlyStr.Substring(0, 500) + "…(truncated)" : earlyStr;
                                    // Demote detailed data previews to Debug and avoid writing full content to the console
                                    _logger.LogDebug("  Early-reflected Data length: {Len} preview: {Preview}", earlyStr.Length, previewEarly);
                                    if (string.IsNullOrEmpty(resultJson) || earlyStr.Length > resultJson.Length)
                                    {
                                        resultJson = earlyStr;
                                        // Intentionally not writing the full data to console to reduce log noise.
                                    }
                                }
                            }
                        }
                    }
                    catch (Exception exRef)
                    {
                        _logger.LogDebug(exRef, "Early reflection failed");
                    }

                    if (evt is AgentRunUpdateEvent runUpdate)
                    {
                        var execId = runUpdate.ExecutorId ?? "<no-executor-id>";
                        // Demote this repetitive per-fragment message to Debug to avoid Info-level noise.
                        _logger.LogDebug("  AgentRunUpdateEvent from executor: {ExecutorId}", execId);

                        if (runUpdate.Data != null)
                        {
                            // Use SerializeData to handle runtime shapes robustly
                            var dataStr = SerializeData(runUpdate.Data);
                            if (!string.IsNullOrEmpty(dataStr))
                            {
                                var preview = dataStr.Length > 200 ? dataStr.Substring(0, 200) + "…(truncated)" : dataStr;
                                // Use Debug for verbose content previews to keep Info logs clean.
                                _logger.LogDebug("  Data length: {Len} preview: {Preview}", dataStr.Length, preview);

                                // Keep the longest fragment as the executor's best output candidate
                                if (!executorOutputs.TryGetValue(execId, out var cur) || (dataStr.Length > (cur?.Length ?? 0)))
                                {
                                    executorOutputs[execId] = dataStr;
                                }

                                // Also track a best-result candidate for overall resultJson
                                if (string.IsNullOrEmpty(resultJson) || dataStr.Length > resultJson.Length)
                                {
                                    resultJson = dataStr;
                                }
                            }
                            else
                            {
                                _logger.LogDebug("  AgentRunUpdateEvent.Data.ToString() returned null");
                            }
                        }
                        else
                        {
                            _logger.LogDebug("  AgentRunUpdateEvent.Data is null");
                        }
                    }
                    else if (evt is WorkflowOutputEvent workflowOutput)
                    {
                        var source = workflowOutput.SourceId ?? "<no-source>";
                        _logger.LogInformation("  WorkflowOutputEvent from executor: {SourceId}", source);

                        if (workflowOutput.Data != null)
                        {
                            string s = SerializeData(workflowOutput.Data);

                            // Try to extract a final JSON object if the runtime returned an array of strings
                            string finalJson = s;
                            try
                            {
                                var trimmed = s.TrimStart();
                                if (trimmed.StartsWith("["))
                                {
                                    var arr = JsonConvert.DeserializeObject<string[]>(s);
                                    if (arr != null && arr.Length > 0)
                                    {
                                        for (int i = arr.Length - 1; i >= 0; --i)
                                        {
                                            var cand = arr[i]?.Trim();
                                            if (!string.IsNullOrEmpty(cand) && (cand.StartsWith("{") || cand.StartsWith("[")))
                                            {
                                                finalJson = cand;
                                                break;
                                            }
                                        }
                                    }
                                }
                            }
                            catch { /* ignore parse errors and fall back to raw */ }

                            resultJson = finalJson;

                            // Capture raw + final outputs in-memory and log their presence instead of persisting to disk
                            try
                            {
                                    // no on-disk persistedRawPath/persistedFinalPath - we capture outputs in-memory only
                                // If the runtime returned an array of strings (common), try to extract stage outputs and keep previews
                                try
                                {
                                    var arr = JsonConvert.DeserializeObject<string[]>(s);
                                    if (arr != null && arr.Length >= 2)
                                    {
                                        // arr[0] is input prompt; arr[1] often the RemoteData output; arr[last] is final
                                        var remoteStr = arr.Length > 1 ? arr[1] : null;
                                        var energyStr = arr.Length > 1 ? arr[arr.Length - 1] : null;
                                        if (!string.IsNullOrEmpty(remoteStr))
                                        {
                                            remoteDataPreview = remoteStr.Length > 400 ? remoteStr.Substring(0, 400).Replace('\n', ' ') + "…(truncated)" : remoteStr.Replace('\n', ' ');
                                            _logger.LogInformation("  RemoteData output captured (in-memory). Preview: {Preview}", remoteDataPreview);
                                        }
                                        if (!string.IsNullOrEmpty(energyStr))
                                        {
                                            energyPreview = energyStr.Length > 400 ? energyStr.Substring(0, 400).Replace('\n', ' ') + "…(truncated)" : energyStr.Replace('\n', ' ');
                                            _logger.LogInformation("  Energy output captured (in-memory). Preview: {Preview}", energyPreview);
                                        }
                                    }
                                }
                                catch { }
                                _logger.LogInformation("        Captured run outputs in memory (no disk writes)");
                            }
                            catch (Exception ex)
                            {
                                _logger.LogWarning(ex, "Failed to capture run outputs in memory");
                            }

                            var preview = s.Length > 200 ? s.Substring(0, 200) + "…(truncated)" : s;
                            // Demote the detailed preview to Debug to reduce console noise
                            _logger.LogDebug("  WorkflowOutputEvent Data length: {Len} preview: {Preview}", s.Length, preview);
                            // Do not write full workflow output to console here; rely on persisted files.
                        }
                        else
                        {
                            _logger.LogDebug("  WorkflowOutputEvent.Data is null");
                        }
                    }
                    else
                    {
                        // Fallback: reflect Data property for runtime types named WorkflowOutputEvent or just log other events
                        if (evt != null && string.Equals(evt.GetType().Name, "WorkflowOutputEvent", StringComparison.Ordinal))
                        {
                            var t = evt.GetType();
                            _logger.LogInformation("  (detected WorkflowOutputEvent runtime type: {Type})", t.AssemblyQualifiedName);
                            var dataProp = t.GetProperty("Data");
                            if (dataProp != null)
                            {
                                var dataVal = dataProp.GetValue(evt);
                                if (dataVal != null)
                                {
                                    var outStr = SerializeData(dataVal);
                                    var preview = outStr.Length > 500 ? outStr.Substring(0, 500) + "…(truncated)" : outStr;
                                    _logger.LogInformation("  Reflected WorkflowOutputEvent Data length: {Len} preview: {Preview}", outStr.Length, preview);
                                    if (string.IsNullOrEmpty(resultJson) || outStr.Length > resultJson.Length)
                                    {
                                        resultJson = outStr;
                                        // Do not write to console; preserve in resultJson and persisted files.
                                    }
                                }
                                else
                                {
                                    _logger.LogInformation("  Reflected WorkflowOutputEvent.Data is null");
                                }
                            }
                            else
                            {
                                _logger.LogInformation("  WorkflowOutputEvent runtime type has no Data property");
                            }
                        }
                        else
                        {
                            // For less common events, keep the full object at Debug level to avoid Info-level noise.
                            _logger.LogDebug("  (non-AgentRunUpdateEvent received): {@Event}", evt);
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Exception while processing workflow event for diagnostics");
                }
            }

            _logger.LogInformation("WatchStreamAsync enumeration completed");

            // After the run completes, emit an Info-level summary showing each executor's best output preview
            try
            {
                if (executorOutputs.Count > 0)
                {
                    _logger.LogInformation("Per-executor outputs (short previews):");
                    foreach (var kv in executorOutputs)
                    {
                        var id = kv.Key;
                        var outStr = kv.Value ?? string.Empty;
                        // Try to produce a human-friendly message preview from the executor output
                        string messagePreview = GetReadableMessage(outStr);

                        // If the extractor returned an unhelpful token like 'Contents' or 'RawRepresentation',
                        // try a targeted fallback: pull first Contents[].RawRepresentation or first Text field.
                        if (string.IsNullOrWhiteSpace(messagePreview) ||
                            string.Equals(messagePreview, "Contents", StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(messagePreview, "RawRepresentation", StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(messagePreview, "Content", StringComparison.OrdinalIgnoreCase))
                        {
                            try
                            {
                                var token = Newtonsoft.Json.Linq.JToken.Parse(outStr);
                                // Look for Contents array
                                var contents = token.SelectToken("..Contents");
                                if (contents is Newtonsoft.Json.Linq.JArray carr && carr.Count > 0)
                                {
                                    var first = carr[0];
                                    // try common fields inside first item
                                    var cand = first.SelectToken("RawRepresentation") ?? first.SelectToken("Text") ?? first.SelectToken("Content") ?? first.SelectToken("Details.Text");
                                    if (cand != null)
                                    {
                                        var candStr = cand.Type == Newtonsoft.Json.Linq.JTokenType.String ? cand.ToString() : cand.ToString(Newtonsoft.Json.Formatting.None);
                                        if (!string.IsNullOrWhiteSpace(candStr)) messagePreview = candStr;
                                    }
                                }
                                else
                                {
                                    // Try top-level Text
                                    var topText = token.SelectToken("Text") ?? token.SelectToken("message") ?? token.SelectToken("Message");
                                    if (topText != null) messagePreview = topText.ToString();
                                }
                            }
                            catch { /* ignore parse errors */ }
                        }

                        var preview = messagePreview.Length > 400 ? messagePreview.Substring(0, 400).Replace('\n', ' ') + "…(truncated)" : messagePreview.Replace('\n', ' ');

                        // Do not persist per-executor artifacts to disk; log the preview and keep raw in-memory only
                        try
                        {
                            _logger.LogInformation("  Executor={Id} | MessagePreview={Preview} | Raw=in-memory (not persisted)", id, preview);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogInformation("  Executor={Id} | MessagePreview={Preview}", id, preview);
                            _logger.LogDebug(ex, "Failed to emit per-executor summary to logs");
                        }
                    }
                }

                // Log final assembled result path and a short preview
                if (!string.IsNullOrEmpty(resultJson))
                {
                    var pshort = resultJson.Length > 600 ? resultJson.Substring(0, 600).Replace('\n', ' ') + "…(truncated)" : resultJson.Replace('\n', ' ');
                    _logger.LogInformation("Final assembled result (in-memory) Preview: {Preview}", pshort);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to emit per-executor summary");
            }

            // Emit a concise single-block Info-level summary for quick inspection
            try
            {
                _logger.LogInformation("=== Run summary ({RunId}) ===", runId);
                if (!string.IsNullOrEmpty(remoteDataPreview))
                {
                    var rShort = remoteDataPreview.Length > 300 ? remoteDataPreview.Substring(0, 300).Replace('\n', ' ') + "…(truncated)" : remoteDataPreview.Replace('\n', ' ');
                    _logger.LogInformation("  RemoteData: {Preview}", rShort);
                }
                if (!string.IsNullOrEmpty(energyPreview))
                {
                    var eShort = energyPreview.Length > 300 ? energyPreview.Substring(0, 300).Replace('\n', ' ') + "…(truncated)" : energyPreview.Replace('\n', ' ');
                    _logger.LogInformation("  Energy: {Preview}", eShort);
                }
                // Final on-disk path no longer produced; final result captured in-memory
                _logger.LogInformation("=== End run summary ===");
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to emit concise run summary");
            }

            // If we captured an energy preview (or final JSON that looks like Energy output), try to persist and plot it
            try
            {
                // Prefer the full assembled resultJson when available; previews may be truncated and break JSON parsing.
                var energyOutput = !string.IsNullOrWhiteSpace(resultJson) ? resultJson : energyPreview;
                if (!string.IsNullOrWhiteSpace(energyOutput))
                {
                    SaveEnergyOutputAndPlot(energyOutput);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to save/plot energy output from orchestrator");
            }

            // Helper: attempt to extract a readable message from a stored executor output string
            static string GetReadableMessage(string outStr)
            {
                if (string.IsNullOrWhiteSpace(outStr)) return string.Empty;

                string trimmed = outStr.TrimStart();

                // Try parsing as JSON token (object or array) and extract likely text fields recursively
                try
                {
                    var token = Newtonsoft.Json.Linq.JToken.Parse(trimmed);

                    string? ExtractFromToken(Newtonsoft.Json.Linq.JToken? t)
                    {
                        if (t == null) return null;
                        if (t.Type == Newtonsoft.Json.Linq.JTokenType.String) return t.ToString();

                        if (t.Type == Newtonsoft.Json.Linq.JTokenType.Object)
                        {
                            var jobj = (Newtonsoft.Json.Linq.JObject)t;
                            // Preferred field names (case-insensitive)
                            string[] preferred = new[] { "Message", "message", "Text", "text", "result", "body", "content", "Content" };
                            foreach (var f in preferred)
                            {
                                if (jobj.TryGetValue(f, StringComparison.OrdinalIgnoreCase, out var v) && v != null)
                                {
                                    if (v.Type == Newtonsoft.Json.Linq.JTokenType.String)
                                        return v.ToString();
                                    // If nested object/array, try to extract recursively
                                    var nested = ExtractFromToken(v);
                                    if (!string.IsNullOrWhiteSpace(nested)) return nested;
                                }
                            }

                            // Contents array is common: inspect items
                            if (jobj.TryGetValue("Contents", StringComparison.OrdinalIgnoreCase, out var contents) && contents is Newtonsoft.Json.Linq.JArray carr)
                            {
                                foreach (var item in carr)
                                {
                                    var cand = ExtractFromToken(item);
                                    if (!string.IsNullOrWhiteSpace(cand)) return cand;
                                }
                            }

                            // RawRepresentation often contains the useful payload
                            if (jobj.TryGetValue("RawRepresentation", StringComparison.OrdinalIgnoreCase, out var rawRep) && rawRep != null)
                            {
                                var cand = ExtractFromToken(rawRep);
                                if (!string.IsNullOrWhiteSpace(cand)) return cand;
                            }
                        }

                        if (t.Type == Newtonsoft.Json.Linq.JTokenType.Array)
                        {
                            var arr = (Newtonsoft.Json.Linq.JArray)t;
                            // prefer last non-empty string/object
                            for (int i = arr.Count - 1; i >= 0; --i)
                            {
                                var candToken = arr[i];
                                var cand = ExtractFromToken(candToken);
                                if (!string.IsNullOrWhiteSpace(cand)) return cand;
                            }
                        }

                        return null;
                    }

                    var fromJson = ExtractFromToken(token);
                    if (!string.IsNullOrWhiteSpace(fromJson)) return fromJson;
                }
                catch { /* not JSON or parse failure; fall through */ }

                // If it's an array of strings encoded as JSON, try that specifically
                try
                {
                    var arr = JsonConvert.DeserializeObject<string[]>(trimmed);
                    if (arr != null && arr.Length > 0)
                    {
                        for (int i = arr.Length - 1; i >= 0; --i)
                        {
                            var cand = arr[i]?.Trim();
                            if (!string.IsNullOrEmpty(cand)) return cand;
                        }
                    }
                }
                catch { }

                // Fall back to searching for obvious text-like fields in the raw string
                try
                {
                    var idx = outStr.IndexOf("\"Text\"", StringComparison.OrdinalIgnoreCase);
                    if (idx >= 0)
                    {
                        var start = outStr.IndexOf('"', idx + 6);
                        if (start >= 0)
                        {
                            var colon = outStr.IndexOf(':', start);
                            if (colon >= 0)
                            {
                                var quote = outStr.IndexOf('"', colon);
                                if (quote >= 0)
                                {
                                    var end = outStr.IndexOf('"', quote + 1);
                                    if (end > quote)
                                    {
                                        var val = outStr.Substring(quote + 1, end - quote - 1);
                                        if (!string.IsNullOrWhiteSpace(val)) return val;
                                    }
                                }
                            }
                        }
                    }
                }
                catch { }

                // Last resort: return a single-line trimmed version of the raw string
                var single = outStr.Replace('\r', ' ').Replace('\n', ' ').Trim();
                return single.Length <= 1000 ? single : single.Substring(0, 1000) + "…(truncated)";
            }

            // Do not persist compact events log to disk; keep it in-memory and log a short summary
            try
            {
                _logger.LogInformation("Captured {Count} compact event lines in-memory (not persisted)", compactLines.Count);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to emit compact events summary");
            }

            // Ensure we always return a JSON string. If the executor emitted JSON already return it; otherwise wrap it.
            if (string.IsNullOrWhiteSpace(resultJson))
            {
                var fallback = new { runId = runId, message = "no executor output captured" };
                return JsonConvert.SerializeObject(fallback);
            }

            var trimmedResult = resultJson.TrimStart();
            if (trimmedResult.StartsWith("{") || trimmedResult.StartsWith("["))
            {
                return resultJson;
            }

            return JsonConvert.SerializeObject(new { runId = runId, result = resultJson });
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
                string cleaned = jsonText ?? string.Empty;
                // If the captured content is an array of strings, try to extract the likely energy payload
                try
                {
                    var arr = JsonConvert.DeserializeObject<string[]>(cleaned);
                    if (arr != null && arr.Length > 0)
                    {
                        // prefer last item as final
                        cleaned = arr[arr.Length - 1] ?? cleaned;
                    }
                }
                catch { }

                try
                {
                    var sanitized = SanitizeJsonText(cleaned) ?? string.Empty;
                    string? formatted = null;
                    bool parsedOk = false;

                    // First attempt: direct parse with System.Text.Json
                    try
                    {
                        var doc = System.Text.Json.JsonDocument.Parse(sanitized);
                        formatted = System.Text.Json.JsonSerializer.Serialize(doc.RootElement, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
                        parsedOk = true;
                    }
                    catch (Exception parseEx)
                    {
                        _logger.LogDebug(parseEx, "Direct JSON parse failed for Energy output; will attempt substring extraction");
                    }

                    // Second attempt: try to find a balanced JSON object/array substring (first '{' or '[' to matching brace)
                    if (!parsedOk)
                    {
                        try
                        {
                            var s = sanitized;
                            int start = s.IndexOf('{');
                            if (start < 0) start = s.IndexOf('[');
                            if (start >= 0)
                            {
                                int depth = 0;
                                char open = s[start];
                                char close = open == '{' ? '}' : ']';
                                int end = -1;
                                for (int i = start; i < s.Length; i++)
                                {
                                    var c = s[i];
                                    if (c == open) depth++;
                                    else if (c == close) depth--;
                                    if (depth == 0)
                                    {
                                        end = i;
                                        break;
                                    }
                                }

                                if (end > start)
                                {
                                    var candidate = s.Substring(start, end - start + 1);
                                    try
                                    {
                                        var doc2 = System.Text.Json.JsonDocument.Parse(candidate);
                                        formatted = System.Text.Json.JsonSerializer.Serialize(doc2.RootElement, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
                                        parsedOk = true;
                                    }
                                    catch (Exception ex2)
                                    {
                                        _logger.LogDebug(ex2, "Substring JSON parse failed for candidate payload");
                                    }
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger.LogDebug(ex, "Failed during JSON substring extraction attempt");
                        }
                    }

                    var outDir = System.IO.Path.Combine("docs");
                    System.IO.Directory.CreateDirectory(outDir);
                    var outPath = System.IO.Path.Combine(outDir, "last_agent_output.json");

                    if (parsedOk && formatted != null)
                    {
                        System.IO.File.WriteAllText(outPath, formatted);
                        _logger.LogInformation("Saved Energy GlobalEnvelope (pretty JSON) to {Path}", outPath);

                        // Run the plotting script and capture output for demo exposition
                        var scriptPath = System.IO.Path.GetFullPath(System.IO.Path.Combine(System.IO.Directory.GetCurrentDirectory(), "docs", "energy_measures_plot.py"));
                        var jsonPath = System.IO.Path.GetFullPath(outPath);

                        if (!System.IO.File.Exists(scriptPath))
                        {
                            _logger.LogWarning("Plot script not found at {ScriptPath}. Skipping plot. Current directory: {Cwd}", scriptPath, System.IO.Directory.GetCurrentDirectory());
                            return;
                        }

                        var psi = new System.Diagnostics.ProcessStartInfo
                        {
                            FileName = "python",
                            Arguments = $"\"{scriptPath}\" \"{jsonPath}\"",
                            RedirectStandardOutput = true,
                            RedirectStandardError = true,
                            UseShellExecute = false,
                            CreateNoWindow = true
                        };

                        using var proc = System.Diagnostics.Process.Start(psi);
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
                    else
                    {
                        // Could not parse JSON robustly. Save a wrapped raw output for debugging and skip plotting.
                        var wrapped = Newtonsoft.Json.JsonConvert.SerializeObject(new { raw = sanitized });
                        System.IO.File.WriteAllText(outPath, wrapped);
                        _logger.LogWarning("Failed to parse Energy JSON payload; saved raw cleaned output to {Path} for inspection and skipped plotting", outPath);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Unexpected error while saving or plotting Energy output");
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Unexpected error in SaveEnergyOutputAndPlot");
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