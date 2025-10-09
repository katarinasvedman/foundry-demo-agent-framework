using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Azure.AI.Agents.Persistent;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Azure.Security.KeyVault.Secrets;
using Azure.Identity;

namespace Foundry.Agents.Agents
{
    internal static class AgentFileHelpers
    {
        // Thread mapping and file-lock helpers removed: thread/lock/mapping implementation
        // has been retired in favor of running agents through the agent framework workflows.

        public static async Task PersistAgentIdAsync(string agentId, IConfiguration configuration, ILogger logger, string agentName = "RemoteData")
        {
            try
            {
                var useManagedIdentity = configuration.GetValue<bool?>("Azure:UseManagedIdentity") ?? false;
                var keyVaultName = configuration["Azure:KeyVaultName"];
                if (useManagedIdentity && !string.IsNullOrEmpty(keyVaultName))
                {
                    logger.LogInformation("Persisting agent id to Key Vault {KeyVault}", keyVaultName);
                    var kvUri = new Uri($"https://{keyVaultName}.vault.azure.net/");
                    var client = new SecretClient(kvUri, new DefaultAzureCredential());
                    await client.SetSecretAsync($"{agentName}AgentId", agentId);
                    return;
                }

                var dir = Path.Combine("Agents", agentName);
                Directory.CreateDirectory(dir);
                var path = Path.Combine(dir, "agent-id.txt");
                await File.WriteAllTextAsync(path, agentId);
                logger.LogInformation("Persisted agent id to local file {Path}", path);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to persist agent id");
            }
        }

        public static async Task<string?> ReadPersistedAgentIdAsync(IConfiguration configuration, string agentName, ILogger logger)
        {
            try
            {
                var useManagedIdentity = configuration.GetValue<bool?>("Azure:UseManagedIdentity") ?? false;
                var keyVaultName = configuration["Azure:KeyVaultName"]; 
                if (useManagedIdentity && !string.IsNullOrEmpty(keyVaultName))
                {
                    try
                    {
                        var kvUri = new Uri($"https://{keyVaultName}.vault.azure.net/");
                        var client = new SecretClient(kvUri, new DefaultAzureCredential());
                        var secret = await client.GetSecretAsync($"{agentName}AgentId");
                        return secret?.Value?.Value;
                    }
                    catch (Exception ex)
                    {
                        logger?.LogWarning(ex, "Failed to read agent id from Key Vault {KeyVault} for agent {AgentName}", keyVaultName, agentName);
                    }
                }

                var path = System.IO.Path.Combine("Agents", agentName, "agent-id.txt");
                if (System.IO.File.Exists(path))
                {
                    try
                    {
                        var txt = await System.IO.File.ReadAllTextAsync(path);
                        return txt?.Trim();
                    }
                    catch (Exception ex)
                    {
                        logger?.LogWarning(ex, "Failed to read local agent id file at {Path}", path);
                    }
                }
            }
            catch (Exception ex)
            {
                logger?.LogWarning(ex, "Error while attempting to read persisted agent id for {AgentName}", agentName);
            }

            return null;
        }
    }
}
