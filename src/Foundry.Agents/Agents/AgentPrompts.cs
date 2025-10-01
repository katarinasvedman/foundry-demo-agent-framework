namespace Foundry.Agents.Agents
{
    internal static class AgentPrompts
    {
        public const string RemoteDataDemo = "Fetch SE3 price and Stockholm hourly temperature for yyyy-MM-dd. Return the JSON envelope only";
        public const string EnergyDemo = "Compute baseline and three measures for energy usage for yyyy-MM-dd. Return the JSON envelope only";
        // High level ask for Energy: ask Energy to orchestrate and use connected tools as needed
        public const string HighLevelAsk = "Compute a deterministic baseline and three energy-saving measures for zone SE3 in Stockholm on 2025-10-01. Send the summary by email to kapeltol@microsoft.com. Return only the GlobalEnvelope JSON.";
    }
}
