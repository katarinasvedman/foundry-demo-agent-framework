using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
namespace Foundry.Agents.Agents
{
    // A small hosted service that can run registered agents on startup.
    public class HostedAgentRunner : IHostedService
    {
        private readonly IServiceProvider _services;
        private readonly ILogger<HostedAgentRunner> _logger;
        private readonly IHostApplicationLifetime _lifetime;
        private int _shutdownRequested = 0;
        private int _started = 0;
        public HostedAgentRunner(IServiceProvider services, ILogger<HostedAgentRunner> logger, IHostApplicationLifetime lifetime)
        {
            _services = services;
            _logger = logger;
            _lifetime = lifetime;
        }

        public async Task StartAsync(CancellationToken cancellationToken)
        {
            if (System.Threading.Interlocked.Exchange(ref _started, 1) == 1)
            {
                _logger.LogInformation("HostedAgentRunner.StartAsync already executed; skipping re-entry.");
                return;
            }

            _logger.LogInformation("HostedAgentRunner starting. Initializing only Orchestrator agent.");
            using var scope = _services.CreateScope();

            // Only the OrchestratorAgent runs in-process. All other agents are persisted assistants
            // managed by the Foundry Persistent Agents service and will be created/queried by the orchestrator.
            var orchestrator = scope.ServiceProvider.GetService<Foundry.Agents.Agents.Orchestrator.OrchestratorAgent>();
            if (orchestrator != null)
            {
                try
                {
                    // Use demo inputs for zone/city/date
                        var zone = "SE3";
                        var city = "Stockholm";
                        var date = System.DateTime.UtcNow.ToString("yyyy-MM-dd");
                        var json = await orchestrator.RunAsync(zone, city, date);
                        _logger.LogInformation("Orchestrator run completed. Result: {Result}", json);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error while running orchestrator");
                }
            }
            else
            {
                _logger.LogWarning("OrchestratorAgent not registered.");
            }

            // After the agent run completes (or fails), stop the application so the process exits cleanly
            try
            {
                // Ensure we request shutdown only once to avoid duplicate logs/requests when StartAsync
                // is executed more than once in edge cases (or the host was started multiple times).
                if (System.Threading.Interlocked.Exchange(ref _shutdownRequested, 1) == 0)
                {
                    _logger.LogInformation("Energy initialization finished; requesting host shutdown.");
                    _lifetime.StopApplication();
                }
                else
                {
                    _logger.LogDebug("Shutdown already requested previously; skipping duplicate request.");
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to request host shutdown");
            }
        }

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
