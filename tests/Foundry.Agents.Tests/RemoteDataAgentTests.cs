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
        // InitializeAsync removed from RemoteDataAgent — initialization is handled by the orchestrator.
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
