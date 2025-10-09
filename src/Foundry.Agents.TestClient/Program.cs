using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Azure.Identity;
using Azure.AI.Agents.Persistent;

class Program
{
    static async Task<int> Main(string[] args)
    {
        try
        {
            var endpoint = Environment.GetEnvironmentVariable("PROJECT_ENDPOINT") ?? "http://localhost:3000";
            Console.WriteLine($"Using endpoint: {endpoint}");

            var ids = args?.ToList() ?? new System.Collections.Generic.List<string>();

            if (ids.Count == 0)
            {
                // Try to read agent ids from Agents/*/agent-id.txt
                var agentsDir = Path.Combine(Directory.GetCurrentDirectory(), "Agents");
                if (Directory.Exists(agentsDir))
                {
                    foreach (var dir in Directory.GetDirectories(agentsDir))
                    {
                        var p = Path.Combine(dir, "agent-id.txt");
                        if (File.Exists(p))
                        {
                            try { var t = File.ReadAllText(p).Trim(); if (!string.IsNullOrWhiteSpace(t)) ids.Add(t); } catch { }
                        }
                    }
                }
            }

            if (ids.Count == 0)
            {
                Console.WriteLine("No agent ids provided via args or Agents/*/agent-id.txt files. Provide agent ids as args or create agent-id.txt files.");
                return 2;
            }

            var client = new PersistentAgentsClient(endpoint, new DefaultAzureCredential());
            var admin = client.Administration;

            foreach (var id in ids)
            {
                Console.WriteLine($"Querying agent id: {id}");
                try
                {
                    var resp = await admin.GetAgentAsync(id);
                    var agent = resp?.Value;
                    if (agent == null)
                    {
                        Console.WriteLine($"Agent {id} not found (null response)");
                        continue;
                    }
                    try
                    {
                        var json = Newtonsoft.Json.JsonConvert.SerializeObject(agent, Newtonsoft.Json.Formatting.Indented);
                        Console.WriteLine("Agent JSON:\n" + json);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"Agent returned but failed to serialize: {ex}");
                    }
                }
                catch (Azure.RequestFailedException rf)
                {
                    Console.WriteLine($"RequestFailedException querying {id}: {rf.Status} {rf.Message}");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Failed to query agent {id}: {ex}");
                }
            }

            return 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine("Error: " + ex);
            return 1;
        }
    }
}
