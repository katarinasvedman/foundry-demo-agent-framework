using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Foundry.Agents.Agents.Report
{
    // Minimal ReportAgent: produces a concise human-readable summary and subject from numeric results
    public class ReportAgent
    {
        private readonly ILogger<ReportAgent> _logger;

        public ReportAgent(ILogger<ReportAgent> logger)
        {
            _logger = logger;
        }

        // Input: JSON string from ComputeAgent (deterministic numeric results)
        // Output: JSON object with { subject: string, summary: string }
        public Task<string> RunAsync(string computeJson)
        {
            // Minimal deterministic summarization: do not change numbers, only produce a short subject and human summary.
            // For now, implement a very small heuristic: when computeJson is empty or null, return needs_input envelope.
            if (string.IsNullOrWhiteSpace(computeJson))
            {
                _logger.LogWarning("ReportAgent received empty input");
                return Task.FromResult("{ \"subject\": \"Needs input\", \"summary\": \"No compute results provided.\" }");
            }

            // Simple summary builder: include length of payload and a neutral summary line. This is intentionally minimal.
            var subject = "Energy report";
            var summary = $"Generated report from compute results (payload length: {computeJson.Length} chars). See attached data for numeric details.";

            var resultJson = System.Text.Json.JsonSerializer.Serialize(new { subject, summary });
            return Task.FromResult(resultJson);
        }
    }
}
