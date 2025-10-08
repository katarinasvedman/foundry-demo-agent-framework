using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Foundry.Agents.Agents.Shared
{
    // Simple local/mock adapter for development when Project:Endpoint is non-HTTPS (localhost).
    // Returns a synthetic agent id and implements AgentExists/Create without calling the Azure SDK.
    public class LocalPersistentAgentsClientAdapter : IPersistentAgentsClientAdapter
    {
        private readonly string _endpoint;
        private readonly IConfiguration _configuration;
        private readonly ILogger<LocalPersistentAgentsClientAdapter> _logger;

        public LocalPersistentAgentsClientAdapter(string endpoint, IConfiguration configuration, ILogger<LocalPersistentAgentsClientAdapter>? logger = null)
        {
            _endpoint = endpoint ?? throw new ArgumentNullException(nameof(endpoint));
            _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
            _logger = logger ?? Microsoft.Extensions.Logging.LoggerFactory.Create(b => { }).CreateLogger<LocalPersistentAgentsClientAdapter>();
        }

        public Task<bool> AgentExistsAsync(string agentId)
        {
            // For local testing, assume agent does not exist unless an id is provided and looks like a local id
            if (string.IsNullOrEmpty(agentId)) return Task.FromResult(false);
            return Task.FromResult(agentId.StartsWith("local-", StringComparison.OrdinalIgnoreCase));
        }

        public Task<string?> CreateAgentAsync(string modelDeploymentName, string name, string? instructions, IEnumerable<string>? toolTypes = null)
        {
            // Create a synthetic id that will be persisted by the caller
            var id = "local-" + Guid.NewGuid().ToString("N");
            _logger.LogInformation("LocalPersistentAgentsClientAdapter: creating synthetic agent id {AgentId} for agent {Name}", id, name);
            return Task.FromResult<string?>(id);
        }

        public Task<string?> RunAgentAsync(string agentId, object payload, System.Threading.CancellationToken cancellationToken = default)
        {
            // For local adapter, simulate an assistant by echoing the payload as JSON wrapped in a simple envelope
            var json = Newtonsoft.Json.JsonConvert.SerializeObject(new { agent = agentId, status = "ok", data = payload });
            _logger.LogInformation("LocalPersistentAgentsClientAdapter: simulated run for {AgentId}", agentId);
            return Task.FromResult<string?>(json);
        }

        public Task<bool> UpdateAgentOpenApiToolAsync(string agentId, string openApiSpecJson)
        {
            // Local adapter: no-op, pretend success
            _logger.LogInformation("LocalPersistentAgentsClientAdapter: UpdateAgentOpenApiToolAsync called for {AgentId} (no-op)", agentId);
            return Task.FromResult(true);
        }
    }
}
