using System.Threading.Tasks;
using System.Collections.Generic;

namespace Foundry.Agents.Agents
{
    public interface IPersistentAgentsClientAdapter
    {
        Task<bool> AgentExistsAsync(string agentId);
        // toolTypes: optional hint strings such as "openapi" or "code_interpreter" to request attaching specific tools
        Task<string?> CreateAgentAsync(string modelDeploymentName, string name, string? instructions, IEnumerable<string>? toolTypes = null);
    }
}
