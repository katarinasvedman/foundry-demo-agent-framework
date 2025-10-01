using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Foundry.Agents.Agents.Shared;
using Foundry.Agents.Agents;

namespace Foundry.Agents.Agents.RemoteData
{
    // Lightweight delegating adapter kept for backward-compatibility with tests
    // and existing code that referenced the type under the RemoteData namespace.
    public class RealPersistentAgentsClientAdapter : IPersistentAgentsClientAdapter
    {
        private readonly Shared.RealPersistentAgentsClientAdapter _inner;

        public RealPersistentAgentsClientAdapter(string endpoint, IConfiguration configuration, ILogger<RealPersistentAgentsClientAdapter>? logger = null)
        {
            // The shared implementation will create a fallback logger if none provided.
            _inner = new Shared.RealPersistentAgentsClientAdapter(endpoint, configuration, null);
        }

        public Task<bool> AgentExistsAsync(string agentId) => _inner.AgentExistsAsync(agentId);

        public Task<string?> CreateAgentAsync(string modelDeploymentName, string name, string? instructions, IEnumerable<string>? toolTypes = null)
            => _inner.CreateAgentAsync(modelDeploymentName, name, instructions, toolTypes);

        public Task<bool> UpdateAgentOpenApiToolAsync(string agentId, string openApiSpecJson)
            => _inner.UpdateAgentOpenApiToolAsync(agentId, openApiSpecJson);
    }
}
