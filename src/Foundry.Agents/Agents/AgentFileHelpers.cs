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
        public static async Task<Dictionary<string, string>> ReadThreadMappingAsync(string threadMappingPath, ILogger? logger)
        {
            try
            {
                if (!File.Exists(threadMappingPath)) return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                var txt = await File.ReadAllTextAsync(threadMappingPath);
                return JsonSerializer.Deserialize<Dictionary<string, string>>(txt) ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            }
            catch (Exception ex)
            {
                logger?.LogWarning(ex, "Failed to read thread mapping file at {Path}; continuing with empty mapping.", threadMappingPath);
                return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            }
        }

        public static async Task SaveThreadMappingAsync(string threadMappingPath, Dictionary<string, string> mapping, ILogger? logger)
        {
            try
            {
                var dir = Path.GetDirectoryName(threadMappingPath) ?? Path.Combine("Agents", "RemoteData");
                Directory.CreateDirectory(dir);
                var txt = JsonSerializer.Serialize(mapping, new JsonSerializerOptions { WriteIndented = true });
                await File.WriteAllTextAsync(threadMappingPath, txt);
            }
            catch (Exception ex)
            {
                logger?.LogWarning(ex, "Failed to persist thread mapping file at {Path}", threadMappingPath);
            }
        }

        public static async Task<string> GetOrCreateThreadIdForAgentAsync(PersistentAgentsClient client, string agentId, string threadMappingPath, ILogger? logger, CancellationToken cancellationToken)
        {
            var mapping = await ReadThreadMappingAsync(threadMappingPath, logger);
            if (mapping.TryGetValue(agentId, out var existingThreadId))
            {
                logger?.LogDebug("Found existing thread mapping for agent {AgentId} -> {ThreadId}", agentId, existingThreadId);
                return existingThreadId;
            }

            logger?.LogDebug("No existing thread for agent {AgentId}; creating new thread.", agentId);
            var threadResp = await client.Threads.CreateThreadAsync(new System.Collections.Generic.List<ThreadMessageOptions>(), cancellationToken: cancellationToken);
            var thread = threadResp?.Value;
            var threadId = thread?.Id ?? throw new InvalidOperationException("Failed to create thread");
            mapping[agentId] = threadId;
            await SaveThreadMappingAsync(threadMappingPath, mapping, logger);
            logger?.LogInformation("Persisted thread mapping for agent {AgentId} -> {ThreadId}", agentId, threadId);
            return threadId;
        }

        public static async Task<bool> AcquireThreadLockAsync(string directory, string threadId, TimeSpan timeout)
        {
            Directory.CreateDirectory(directory);
            var lockPath = Path.Combine(directory, $"thread-{threadId}.lock");
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (sw.Elapsed < timeout)
            {
                try
                {
                    using (var fs = File.Open(lockPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    {
                        var info = Encoding.UTF8.GetBytes(DateTime.UtcNow.ToString("o"));
                        await fs.WriteAsync(info.AsMemory(0, info.Length));
                    }
                    return true;
                }
                catch (IOException)
                {
                    await Task.Delay(200);
                }
            }
            return false;
        }

        public static void ReleaseThreadLock(string directory, string threadId, ILogger? logger = null)
        {
            try
            {
                var lockPath = Path.Combine(directory, $"thread-{threadId}.lock");
                if (File.Exists(lockPath)) File.Delete(lockPath);
            }
            catch (Exception ex)
            {
                logger?.LogWarning(ex, "Failed to release lock for thread {ThreadId}", threadId);
            }
        }

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
