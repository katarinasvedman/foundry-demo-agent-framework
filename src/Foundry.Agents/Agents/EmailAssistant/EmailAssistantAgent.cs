using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Azure.AI.Agents.Persistent;
using Microsoft.Agents.AI;
using Foundry.Agents.Agents.Shared;

namespace Foundry.Agents.Agents.EmailAssistant
{
    public class EmailAssistantAgent
    {
        // Read per-agent instructions via InstructionReader
        public string Instructions => InstructionReader.ReadSection("EmailAssistant");

        /// <summary>
        /// Ensure a persisted AIAgent exists for EmailAssistant on the given endpoint. Uses
        /// <see cref="AgentCreationHelper"/> to locate an existing agent or create and persist
        /// a new one. Returns the found or created <see cref="AIAgent"/> instance, or null on failure.
        /// </summary>
        public static Task<AIAgent?> GetOrCreateAIAgentAsync(string endpoint, IConfiguration configuration, ILogger logger, IPersistentAgentsClientAdapter? adapter = null, CancellationToken cancellationToken = default)
        {
            // Email assistant needs a Logic App connector tool to send emails
            return AgentCreationHelper.GetOrCreateAsync(endpoint, configuration, logger, "EmailAssistant", "EmailAssistantAgentAFX", () => InstructionReader.ReadSection("EmailAssistant"), adapter, new[] { "logicapp" }, cancellationToken);
        }
    }
}
