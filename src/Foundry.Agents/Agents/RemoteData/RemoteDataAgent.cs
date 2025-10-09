using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Azure;
using Azure.AI.Agents.Persistent;
using Azure.Identity;
using Microsoft.Agents.AI;
using Foundry.Agents.Agents;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Foundry.Agents.Agents.Shared;

namespace Foundry.Agents.Agents.RemoteData
{
    public class RemoteDataAgent
    {
    private readonly ILogger<RemoteDataAgent> _logger;
    private readonly IConfiguration _configuration;

        public RemoteDataAgent(ILogger<RemoteDataAgent> logger, IConfiguration configuration)
        {
            _logger = logger;
            _configuration = configuration;
        }

        // Agent instructions are stored in the repo and loaded at runtime by `InstructionReader`.
        public string Instructions => InstructionReader.ReadSection("RemoteData");

        // RemoteDataAgent no longer exposes local initialization or run helpers. Agent creation
        // and lifecycle are handled by the Orchestrator which calls the GetOrCreate helper below.

        /// <summary>
        /// Ensure a persisted AIAgent exists for RemoteData on the given endpoint. Uses
        /// <see cref="AgentCreationHelper"/> to locate an existing agent or create and persist
        /// a new one. Returns the found or created <see cref="AIAgent"/> instance, or null on failure.
        /// </summary>
        public static Task<AIAgent?> GetOrCreateAIAgentAsync(string endpoint, IConfiguration configuration, ILogger logger, IPersistentAgentsClientAdapter? adapter = null, CancellationToken cancellationToken = default)
        {
            return AgentCreationHelper.GetOrCreateAsync(endpoint, configuration, logger, "RemoteData", "RemoteDataAgentAFX", () => InstructionReader.ReadSection("RemoteData"), adapter, cancellationToken);
        }
    }
}
