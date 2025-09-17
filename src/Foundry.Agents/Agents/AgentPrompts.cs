namespace Foundry.Agents.Agents
{
    internal static class AgentPrompts
    {
        public const string RemoteDataDemo = "Fetch today's SE3 price and Stockholm hourly temperature. Return the JSON envelope only";
        public const string EnergyDemo = "Compute baseline and three measures for energy usage for today. Return the JSON envelope only";
        // High level ask for Energy: ask Energy to orchestrate and use connected tools as needed
        public const string HighLevelAsk = "Plan and compute a deterministic baseline and three measures for energy usage for yyyy-MM-dd. First call the connected agent RemoteData to fetch DayAheadPrice(zone='SE3',date=YYYY-MM-DD) and WeatherHourly(city='Stockholm',date=YYYY-MM-DD). Use ISO date format yyyy-MM-dd (do not send 'today' or placeholder tokens). Use connected tools as needed. Return only the GlobalEnvelope JSON with arrays of length 24 where applicable.";
    }
}
