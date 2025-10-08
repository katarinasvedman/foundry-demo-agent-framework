using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Azure.AI.Agents.Persistent;
using Azure.Identity;
using Foundry.Agents.Agents.Shared;

namespace Foundry.Agents.Agents.EmailAssistant
{
    public class EmailAssistantAgent
    {
        private readonly ILogger<EmailAssistantAgent> _logger;
        private readonly IConfiguration _configuration;
    private readonly IPersistentAgentsClientAdapter _adapter;
    private readonly Foundry.Agents.Tools.LogicApp.LogicAppTool? _logicAppTool;

        public EmailAssistantAgent(ILogger<EmailAssistantAgent> logger, IConfiguration configuration)
            : this(logger, configuration, null)
        {
        }

        public EmailAssistantAgent(ILogger<EmailAssistantAgent> logger, IConfiguration configuration, IPersistentAgentsClientAdapter? adapter, Foundry.Agents.Tools.LogicApp.LogicAppTool? logicAppTool = null)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
            _adapter = adapter!; // may be null for InitializeAsync and created lazily
            _logicAppTool = logicAppTool;
        }

        // Read per-agent instructions via InstructionReader
        public string Instructions => InstructionReader.ReadSection("EmailAssistant");

    public async Task InitializeAsync()
        {
            _logger.LogInformation("EmailAssistantAgent initializing using persistent agents adapter.");

            var endpoint = Environment.GetEnvironmentVariable("PROJECT_ENDPOINT") ?? _configuration["Project:Endpoint"];
            var modelDeploymentName = Environment.GetEnvironmentVariable("MODEL_DEPLOYMENT_NAME") ?? _configuration["Project:ModelDeploymentName"];

            if (string.IsNullOrWhiteSpace(endpoint) || string.IsNullOrWhiteSpace(modelDeploymentName))
            {
                _logger.LogWarning("PROJECT_ENDPOINT or MODEL_DEPLOYMENT_NAME not configured. Skipping EmailAssistant initialization.");
                return;
            }

            try
            {
                IPersistentAgentsClientAdapter adapter = _adapter ?? new Shared.RealPersistentAgentsClientAdapter(endpoint, _configuration, null);

                var persistedAgentId = await AgentFileHelpers.ReadPersistedAgentIdAsync(_configuration, "EmailAssistant", _logger);
                if (!string.IsNullOrEmpty(persistedAgentId))
                {
                    _logger.LogInformation("Found persisted agent id {AgentId} for EmailAssistant; verifying", persistedAgentId);
                    try
                    {
                        if (await adapter.AgentExistsAsync(persistedAgentId))
                        {
                            _logger.LogInformation("Persisted agent {AgentId} exists. Reusing.", persistedAgentId);
                            return;
                        }

                        _logger.LogWarning("Persisted agent id {AgentId} not found on server; will create a new agent.", persistedAgentId);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Error while verifying persisted agent id {AgentId}; will attempt creation.", persistedAgentId);
                    }
                }

                var providedAgentId = Environment.GetEnvironmentVariable("PROJECT_AGENT_ID") ?? _configuration["Project:AgentId"];
                // If no env/config provided id, fallback to the persisted file for EmailAssistant
                if (string.IsNullOrEmpty(providedAgentId))
                {
                    var persisted = await AgentFileHelpers.ReadPersistedAgentIdAsync(_configuration, "EmailAssistant", _logger);
                    if (!string.IsNullOrEmpty(persisted)) providedAgentId = persisted;
                }
                if (!string.IsNullOrEmpty(providedAgentId))
                {
                    _logger.LogInformation("Verifying provided agent id {AgentId}", providedAgentId);
                    if (await adapter.AgentExistsAsync(providedAgentId))
                    {
                        _logger.LogInformation("Agent {AgentId} exists.", providedAgentId);
                        await AgentFileHelpers.PersistAgentIdAsync(providedAgentId, _configuration, _logger, "EmailAssistant");
                        return;
                    }

                    _logger.LogWarning("Provided agent id {AgentId} not found; will create a new agent.", providedAgentId);
                }

                var instructions = Instructions;
                if (string.IsNullOrWhiteSpace(instructions))
                {
                    // Minimal inline fallback if the file is missing
                    instructions = "You are EmailAssistantAgent. Draft and send concise emails via an OpenAPI/HTTP Logic App connector. Accept email_to, email_subject, email_body. Return a JSON envelope with status ok|needs_input|error.";
                }

                // EmailAssistant is implemented to invoke Azure Logic Apps via a host-provided LogicAppTool
                // and does not require attaching an OpenAPI tool to the persisted agent.
                var newAgentId = await adapter.CreateAgentAsync(modelDeploymentName, "EmailAssistantAgent", instructions, Array.Empty<string>());
                if (!string.IsNullOrEmpty(newAgentId))
                {
                    _logger.LogInformation("Created EmailAssistantAgent with id {AgentId}", newAgentId);
                    await AgentFileHelpers.PersistAgentIdAsync(newAgentId, _configuration, _logger, "EmailAssistant");
                }
                else
                {
                    _logger.LogError("CreateAgentAsync returned null or empty id for EmailAssistantAgent.");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error while initializing EmailAssistantAgent");
            }
        }

        // Run the EmailAssistant flow: accepts a flexible input shape, normalizes recipients,
        // calls the configured OpenAPI/LogicApp endpoint and returns the strict JSON envelope.
        // This method is intentionally self-contained and returns a string containing JSON.
        public async Task<string> RunAsync(object input)
        {
            try
            {
                // Normalize input into a canonical shape
                var canonical = NormalizeInput(input);
                if (canonical == null)
                {
                    return BuildEnvelope(status: "needs_input", summary: "Missing required fields. Expect email_to, email_subject, email_body.", data: new { });
                }

                // Critical rule: do not rewrite subject or body
                var requestBody = new
                {
                    email_to = canonical.EmailTo,
                    email_subject = canonical.EmailSubject,
                    email_body = canonical.EmailBody,
                    cc = canonical.Cc ?? new string[0],
                    isHtml = canonical.IsHtml
                };

                // Prefer injected LogicAppTool or construct one using DefaultAzureCredential
                var logicTool = _logicAppTool;
                if (logicTool == null)
                {
                    var httpClient = new System.Net.Http.HttpClient();
                    var loggerFactory = LoggerFactory.Create(b => b.AddConsole());
                    var credential = new DefaultAzureCredential();
                    logicTool = new Foundry.Agents.Tools.LogicApp.LogicAppTool(httpClient, credential, loggerFactory.CreateLogger<Foundry.Agents.Tools.LogicApp.LogicAppTool>(), _configuration);
                }

                // Logic App path configuration - prefer LogicApp:InvokePath or LogicApp:SendEmailPath
                string path = _configuration["LogicApp:InvokePath"] ?? _configuration["LogicApp:SendEmailPath"] ?? "/invoke";

                // The Foundry/OpenAPI spec exported from portal uses required query parameters: api-version, sv, sp
                var query = new System.Collections.Generic.Dictionary<string,string>
                {
                    { "api-version", _configuration["LogicApp:ApiVersion"] ?? "2016-10-01" },
                    { "sv", _configuration["LogicApp:Sv"] ?? "1.0" },
                    { "sp", _configuration["LogicApp:Sp"] ?? "%2Ftriggers%2FWhen_a_HTTP_request_is_received%2Frun" }
                };

                try
                {
                    var respText = await logicTool.InvokeAsync(path, requestBody, query);

                    var data = new
                    {
                        to = canonical.EmailTo,
                        subject = canonical.EmailSubject,
                        sentAt = DateTime.UtcNow.ToString("o")
                    };

                    return BuildEnvelope(status: "ok", summary: "Email sent successfully.", data: data);
                }
                catch (Exception ex)
                {
                    var diagnostics = new
                    {
                        http_status = ex is HttpRequestException h ? h.Message : ex.Message,
                        connector = path,
                        request_shape = "canonical",
                        request_sample = requestBody,
                        response_sample = ex.ToString()
                    };

                    return BuildEnvelope(status: "error", summary: "Failed to send email.", data: new { diagnostics });
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unexpected error in EmailAssistant.RunAsync");
                return BuildEnvelope(status: "error", summary: "Unexpected internal error.", data: new { error = ex.Message });
            }
        }

        private record CanonicalInput(string[] EmailTo, string EmailSubject, string EmailBody, string[]? Cc, bool IsHtml);

        private CanonicalInput? NormalizeInput(object input)
        {
            try
            {
                // If input is a JSON string (query) or an object, try to convert to a dictionary
                if (input == null) return null;

                // If input is a string that contains JSON, try parse
                if (input is string s)
                {
                    // Try parse as JSON object
                    try
                    {
                        var obj = Newtonsoft.Json.JsonConvert.DeserializeObject<System.Collections.Generic.Dictionary<string, object>>(s);
                        return NormalizeDict(obj);
                    }
                    catch
                    {
                        // not JSON, fallthrough
                    }
                }

                // If input is a dictionary-like object
                if (input is System.Collections.IDictionary dict)
                {
                    var d = new System.Collections.Generic.Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
                    foreach (System.Collections.DictionaryEntry e in dict)
                    {
                        if (e.Key is string key)
                        {
                            d[key] = e.Value!;
                        }
                    }

                    // If wrapped query: { "query": "{...}" } or { "query": {...} }
                    if (d.TryGetValue("query", out var q))
                    {
                        if (q is string qs)
                        {
                            try
                            {
                                var obj = Newtonsoft.Json.JsonConvert.DeserializeObject<System.Collections.Generic.Dictionary<string, object>>(qs);
                                return NormalizeDict(obj);
                            }
                            catch
                            {
                                // not JSON
                            }
                        }
                        else if (q is System.Collections.IDictionary qd)
                        {
                            var nested = new System.Collections.Generic.Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
                            foreach (System.Collections.DictionaryEntry e in qd)
                            {
                                if (e.Key is string key) nested[key] = e.Value!;
                            }
                            return NormalizeDict(nested);
                        }
                    }

                    return NormalizeDict(d);
                }

                // Try to serialize-and-deserialize to dictionary as a last resort
                var serialized = Newtonsoft.Json.JsonConvert.SerializeObject(input);
                var fallback = Newtonsoft.Json.JsonConvert.DeserializeObject<System.Collections.Generic.Dictionary<string, object>>(serialized);
                return NormalizeDict(fallback);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to normalize input for EmailAssistant");
                return null;
            }
        }

        private CanonicalInput? NormalizeDict(System.Collections.Generic.Dictionary<string, object>? dict)
        {
            if (dict == null) return null;

            string? emailToRaw = null;
            if (dict.TryGetValue("email_to", out var et) || dict.TryGetValue("emailTo", out et)) emailToRaw = et?.ToString();
            if (emailToRaw == null && dict.TryGetValue("to", out var toV)) emailToRaw = toV?.ToString();

            var subject = dict.TryGetValue("email_subject", out var es) ? es?.ToString() : (dict.TryGetValue("subject", out var s2) ? s2?.ToString() : null);
            var body = dict.TryGetValue("email_body", out var eb) ? eb?.ToString() : (dict.TryGetValue("body", out var b2) ? b2?.ToString() : null);

            if (string.IsNullOrWhiteSpace(emailToRaw) || string.IsNullOrWhiteSpace(subject) || string.IsNullOrWhiteSpace(body))
            {
                return null;
            }

            string[] NormalizeList(string? raw)
            {
                if (string.IsNullOrWhiteSpace(raw)) return System.Array.Empty<string>();
                try
                {
                    // If it's a JSON array
                    if ((raw.StartsWith("[") && raw.EndsWith("]")) || raw.Contains("\"@") || raw.Contains("\"@"))
                    {
                        try
                        {
                            var arr = Newtonsoft.Json.JsonConvert.DeserializeObject<string[]>(raw);
                            if (arr != null) return arr;
                        }
                        catch { }
                    }

                    return raw.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries)
                        .Select(x => x.Trim())
                        .Where(x => !string.IsNullOrWhiteSpace(x))
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToArray();
                }
                catch
                {
                    return new[] { raw };
                }
            }

            var toArr = NormalizeList(emailToRaw);
            var ccRaw = dict.TryGetValue("cc", out var ccv) ? ccv?.ToString() : null;
            var ccArr = NormalizeList(ccRaw);

            var isHtml = false;
            if (dict.TryGetValue("isHtml", out var ih)) Boolean.TryParse(ih?.ToString(), out isHtml);

            return new CanonicalInput(toArr, subject!, body!, ccArr, isHtml);
        }

        private string BuildEnvelope(string status, string summary, object data)
        {
            var envelope = new
            {
                agent = "EmailAssistantAgent",
                thread_id = "<string>",
                task_id = Guid.NewGuid().ToString(),
                status = status,
                summary = summary,
                data = data,
                citations = new string[0]
            };

            return Newtonsoft.Json.JsonConvert.SerializeObject(envelope);
        }
    }
}
