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

            _logger.LogInformation("HostedAgentRunner starting. Initializing Energy agent (Energy is main, RemoteData attached as connected agent).");
            using var scope = _services.CreateScope();

            // Initialize RemoteData first so its persisted id is available for Energy to attach as a connected agent.
            var remote = scope.ServiceProvider.GetService<Foundry.Agents.Agents.RemoteData.RemoteDataAgent>();
            if (remote != null)
            {
                try
                {
                    await remote.InitializeAsync(cancellationToken);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "RemoteData initialization failed; proceeding to Energy initialization anyway.");
                }
            }

            // Initialize EnergyAgent only; it will reference RemoteData as a connected tool if available
            var energy = scope.ServiceProvider.GetService<Foundry.Agents.Agents.Energy.EnergyAgent>();
            if (energy != null)
            {
                try
                {
                    await energy.InitializeAsync();
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error while running Energy agent");
                }
            }
            else
            {
                _logger.LogWarning("EnergyAgent not registered.");
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
