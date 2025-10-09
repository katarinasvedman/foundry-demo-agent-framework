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
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Foundry.Agents.Agents;
using Foundry.Agents.Agents.Shared;
using System.Text.Json;
using System.Diagnostics;

namespace Foundry.Agents.Agents.Energy
{
    /// <summary>
    /// EnergyAgent provides helpers for the energy demo's persisted agent.
    /// The Orchestrator is responsible for running workflows, capturing outputs,
    /// and triggering any plotting or filesystem interactions. This class exposes
    /// the GetOrCreate helper to ensure the persisted agent exists.
    /// </summary>
    public class EnergyAgent
    {
        private readonly IConfiguration _configuration;
        private readonly ILogger<EnergyAgent> _logger;

        public EnergyAgent(IConfiguration configuration, ILogger<EnergyAgent> logger)
        {
            _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }
        /// <summary>
        /// Ensure a persisted AIAgent exists for Energy on the provided endpoint. Delegates
        /// to <see cref="AgentCreationHelper"/> which implements the get-or-create flow.
        /// Returns the created or found <see cref="AIAgent"/> instance or null on failure.
        /// </summary>
        public static Task<AIAgent?> GetOrCreateAIAgentAsync(string endpoint, IConfiguration configuration, ILogger logger, IPersistentAgentsClientAdapter? adapter = null, CancellationToken cancellationToken = default)
        {
            return AgentCreationHelper.GetOrCreateAsync(endpoint, configuration, logger, "Energy", "EnergyAgentAFX", () => InstructionReader.ReadSection("Energy"), adapter, cancellationToken);
        }
    }
}
