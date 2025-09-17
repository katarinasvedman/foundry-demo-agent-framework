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
        public HostedAgentRunner(IServiceProvider services, ILogger<HostedAgentRunner> logger, IHostApplicationLifetime lifetime)
        {
            _services = services;
            _logger = logger;
            _lifetime = lifetime;
        }

        public async Task StartAsync(CancellationToken cancellationToken)
        {
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
                _logger.LogInformation("Energy initialization finished; requesting host shutdown.");
                _lifetime.StopApplication();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to request host shutdown");
            }
        }

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
