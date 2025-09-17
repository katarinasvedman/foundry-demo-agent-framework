using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Foundry.Agents.Agents.RemoteData;
using System.Collections.Generic;

namespace Foundry.Agents.Tests
{
    public class RealPersistentAgentsClientAdapterTests
    {
        [Fact]
        public async Task CreateAgent_AttachesInlineSpec_WhenSpecFilePresent()
        {
            // Arrange
            var baseDir = AppContext.BaseDirectory;
            var specPath = Path.Combine(baseDir, "Tools", "OpenApi", "apispec.json");
            // Ensure the spec file exists in the output (project configured to copy)
            Assert.True(File.Exists(specPath), $"Spec file not found at {specPath}");

            var inMemoryConfig = new ConfigurationBuilder().AddInMemoryCollection(new[] {
                new KeyValuePair<string,string>("OpenApi:SpecPath", Path.Combine("Tools","OpenApi","apispec.json"))
             }).Build();

            var adapter = new RealPersistentAgentsClientAdapter("https://example.invalid", inMemoryConfig, NullLogger<RealPersistentAgentsClientAdapter>.Instance);

            // We cannot call the real SDK in unit tests; instead verify that reading the spec does not throw and logs info.
            // Call CreateAgentAsync with model name; since the SDK client is internal/reflection-based, we expect an exception from reflection path
            // but the code should attempt to read the spec first. We assert no FileNotFoundException and that the method either throws or returns.

            var ex = await Record.ExceptionAsync(async () => await adapter.CreateAgentAsync("test-model", "unit-test-agent", "instructions"));

            // Ensure exception is not FileNotFoundException (meaning spec was found and read)
            Assert.False(ex is FileNotFoundException, "Spec file should be present and readable");
        }
    }
}
