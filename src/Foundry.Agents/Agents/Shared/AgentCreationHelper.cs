using System;
using System.Threading;
using System.Threading.Tasks;
using Azure;
using Azure.AI.Agents.Persistent;
using Azure.Identity;
using Microsoft.Agents.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Foundry.Agents.Agents.Shared
{
    public static class AgentCreationHelper
    {
        /// <summary>
        /// Generic helper to get or create a persisted AIAgent. Returns the AIAgent or null on failure.
        /// </summary>
        public static async Task<AIAgent?> GetOrCreateAsync(string endpoint, IConfiguration configuration, ILogger logger, string agentName, string createName, Func<string>? readInstructions = null, IPersistentAgentsClientAdapter? adapter = null, CancellationToken cancellationToken = default)
        {
            try
            {
                var client = new PersistentAgentsClient(endpoint, new DefaultAzureCredential());

                var persistedAgentId = await AgentFileHelpers.ReadPersistedAgentIdAsync(configuration, agentName, logger);
                if (!string.IsNullOrWhiteSpace(persistedAgentId))
                {
                    try
                    {
                        var agent = await client.GetAIAgentAsync(persistedAgentId, cancellationToken: cancellationToken);
                        if (agent != null)
                        {
                            logger.LogInformation("Found persisted {AgentName} agent {AgentId} on server; reusing.", agentName, persistedAgentId);
                            return agent;
                        }
                    }
                    catch (RequestFailedException rf) when (rf.Status == 404)
                    {
                        logger.LogWarning("Persisted {AgentName} agent id {AgentId} not found (404); will attempt creation.", agentName, persistedAgentId);
                    }
                    catch (Exception ex)
                    {
                        logger.LogWarning(ex, "Error while verifying persisted agent id {AgentId} for {AgentName}; will attempt creation.", persistedAgentId, agentName);
                    }
                }

                var envKey = agentName.ToUpperInvariant() + "_AGENT_ID";
                var providedAgentId = Environment.GetEnvironmentVariable(envKey) ?? configuration[$"Project:{agentName}AgentId"];
                if (!string.IsNullOrWhiteSpace(providedAgentId))
                {
                    try
                    {
                        var agent = await client.GetAIAgentAsync(providedAgentId, cancellationToken: cancellationToken);
                        if (agent != null)
                        {
                            logger.LogInformation("Using provided {AgentName} agent id {AgentId} and persisting locally.", agentName, providedAgentId);
                            await AgentFileHelpers.PersistAgentIdAsync(providedAgentId, configuration, logger, agentName);
                            return agent;
                        }
                    }
                    catch (Exception ex)
                    {
                        logger.LogWarning(ex, "Error while verifying provided {AgentName} agent id {AgentId}; will attempt creation.", agentName, providedAgentId);
                    }
                }

                var modelDeploymentName = Environment.GetEnvironmentVariable("MODEL_DEPLOYMENT_NAME") ?? configuration["Project:ModelDeploymentName"];
                if (string.IsNullOrWhiteSpace(modelDeploymentName))
                {
                    logger.LogWarning("MODEL_DEPLOYMENT_NAME/Project:ModelDeploymentName not configured; cannot create {AgentName} agent.", agentName);
                    return null;
                }

                var instructions = readInstructions != null ? readInstructions() ?? string.Empty : string.Empty;

                var realAdapter = adapter ?? new RealPersistentAgentsClientAdapter(endpoint, configuration, null);
                var newAgentId = await realAdapter.CreateAgentAsync(modelDeploymentName, createName, instructions, new[] { "openapi" });
                if (string.IsNullOrWhiteSpace(newAgentId))
                {
                    logger.LogError("{AgentName} agent creation returned null or empty id.", agentName);
                    return null;
                }

                logger.LogInformation("Created {AgentName} agent with id {AgentId}; persisting locally.", agentName, newAgentId);
                await AgentFileHelpers.PersistAgentIdAsync(newAgentId, configuration, logger, agentName);

                try
                {
                    var createdAgent = await client.GetAIAgentAsync(newAgentId, cancellationToken: cancellationToken);
                    return createdAgent;
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "{AgentName} agent created ({AgentId}) but failed to fetch AIAgent object.", agentName, newAgentId);
                    return null;
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed while getting or creating {AgentName} agent.", agentName);
                return null;
            }
        }
    }
}
