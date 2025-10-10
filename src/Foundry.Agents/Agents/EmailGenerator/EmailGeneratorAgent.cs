using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Azure.AI.Agents.Persistent;
using Microsoft.Agents.AI;
using Foundry.Agents.Agents.Shared;

namespace Foundry.Agents.Agents.EmailGenerator
{
    public class EmailGeneratorAgent
    {
        public string Instructions => InstructionReader.ReadSection("EmailGenerator");

        public static Task<AIAgent?> GetOrCreateAIAgentAsync(string endpoint, IConfiguration configuration, ILogger logger, IPersistentAgentsClientAdapter? adapter = null, CancellationToken cancellationToken = default)
        {
            // Email generator should have code interpreter capabilities for plot generation
            return AgentCreationHelper.GetOrCreateAsync(endpoint, configuration, logger, "EmailGenerator", "EmailGeneratorAgentAFX", () => InstructionReader.ReadSection("EmailGenerator"), adapter, new[] { "code_interpreter" }, cancellationToken);
        }
    }
}
