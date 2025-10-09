using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Foundry.Agents.Agents.Compute
{
    // ComputeAgent executes a single deterministic 'code interpreter' cell and returns JSON only.
    public class ComputeAgent
    {
        private readonly ILogger<ComputeAgent> _logger;

        public ComputeAgent(ILogger<ComputeAgent> logger)
        {
            _logger = logger;
        }

        // Run a deterministic computation represented by code text and return JSON string result.
        // For the demo, this method will not execute arbitrary code; instead it expects a structured
        // instruction and returns a minimal JSON placeholder to integrate the workflow.
        public Task<string> RunCodeAsync(string codeCell)
        {
            if (string.IsNullOrWhiteSpace(codeCell))
            {
                return Task.FromResult("{ \"status\": \"error\", \"error\": \"no code provided\" }");
            }

            // Deterministic placeholder: echo length and return a sample numeric payload if the code cell contains the token "SAMPLE_NUMBERS"
            if (codeCell.Contains("SAMPLE_NUMBERS"))
            {
                var sample = new { day_ahead_price_sek_per_kwh = new double[] { 0.1, 0.2 }, temperature_c = new double[] { 10.0, 11.0 } };
                return Task.FromResult(System.Text.Json.JsonSerializer.Serialize(new { status = "ok", data = sample }));
            }

            // Default deterministic response
            return Task.FromResult(System.Text.Json.JsonSerializer.Serialize(new { status = "ok", data = new { note = "no-op compute" } }));
        }
    }
}
