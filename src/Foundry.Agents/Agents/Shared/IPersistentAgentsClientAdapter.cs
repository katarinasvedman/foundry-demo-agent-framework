using System.Threading.Tasks;
using System.Collections.Generic;

namespace Foundry.Agents.Agents
{
    public interface IPersistentAgentsClientAdapter
    {
        Task<bool> AgentExistsAsync(string agentId);
        // toolTypes: optional hint strings such as "openapi" or "code_interpreter" to request attaching specific tools
        Task<string?> CreateAgentAsync(string modelDeploymentName, string name, string? instructions, IEnumerable<string>? toolTypes = null);
        // Run a persisted agent with a structured payload. Adapter may create a transient thread or otherwise
        // supply the payload to the agent in a single run operation. Returns the assistant textual output (or null).
        Task<string?> RunAgentAsync(string agentId, object payload, System.Threading.CancellationToken cancellationToken = default);
        // Update the OpenAPI tool attached to an existing agent. Returns true on success.
        Task<bool> UpdateAgentOpenApiToolAsync(string agentId, string openApiSpecJson);
    }
}
