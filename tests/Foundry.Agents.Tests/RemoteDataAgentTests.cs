using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;
using FluentAssertions;
using Foundry.Agents.Agents.RemoteData;

namespace Foundry.Agents.Tests
{
    public class RemoteDataAgentTests
    {
        [Fact]
        public async Task InitializeAsync_Skips_WhenConfigurationMissing()
        {
            var logger = Mock.Of<ILogger<RemoteDataAgent>>();
            var inMemory = new ConfigurationBuilder().Build();
            var agent = new RemoteDataAgent(logger, inMemory);

            // No exceptions should be thrown when missing config
            await agent.InitializeAsync(CancellationToken.None);
        }

        [Fact]
        public void Instructions_File_NotFound_ReturnsEmpty()
        {
            var logger = Mock.Of<ILogger<RemoteDataAgent>>();
            var inMemory = new ConfigurationBuilder().Build();
            var agent = new RemoteDataAgent(logger, inMemory);

            agent.Instructions.Should().BeEmpty();
        }
    }
}
