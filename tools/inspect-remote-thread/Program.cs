using System;
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
            // Try to locate the Agents/RemoteData/agent-id.txt by walking up parent directories
            string? agentIdPath = null;
            try
            {
                var cur = new System.IO.DirectoryInfo(AppContext.BaseDirectory).FullName;
                while (!string.IsNullOrEmpty(cur))
                {
                    var candidate = System.IO.Path.Combine(cur, "Agents", "RemoteData", "agent-id.txt");
                    if (System.IO.File.Exists(candidate))
                    {
                        agentIdPath = candidate;
                        break;
                    }
                    var parent = System.IO.Directory.GetParent(cur);
                    if (parent == null) break;
                    cur = parent.FullName;
                }
            }
            catch
            {
                // ignore and fallback
            }

            // fallback: current working directory
            if (agentIdPath == null)
            {
                var candidate = System.IO.Path.Combine(System.IO.Directory.GetCurrentDirectory(), "Agents", "RemoteData", "agent-id.txt");
                if (System.IO.File.Exists(candidate)) agentIdPath = candidate;
            }

            if (agentIdPath == null)
            {
                Console.Error.WriteLine("Agent id file not found: searched for Agents/RemoteData/agent-id.txt upwards from the binary and current directory.");
                return 2;
            }

            var agentId = (await System.IO.File.ReadAllTextAsync(agentIdPath)).Trim();
            var endpoint = Environment.GetEnvironmentVariable("PROJECT_ENDPOINT");
            if (string.IsNullOrEmpty(endpoint))
            {
                Console.Error.WriteLine("PROJECT_ENDPOINT env var not set. Please set it to the Persistent Agents endpoint.");
                return 2;
            }
            var client = new PersistentAgentsClient(endpoint, new DefaultAzureCredential());
            Console.WriteLine($"Inspecting agent {agentId} at {endpoint}");

            // If user passed --thread and --run, fetch run details and print tool outputs
            string? threadArg = null;
            string? runArg = null;
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--thread" && i + 1 < args.Length) threadArg = args[++i];
                if (args[i] == "--run" && i + 1 < args.Length) runArg = args[++i];
            }

            if (!string.IsNullOrEmpty(threadArg) && !string.IsNullOrEmpty(runArg))
            {
                Console.WriteLine($"Fetching run {runArg} in thread {threadArg}...");
                try
                {
                    var getRun = client.Runs.GetRun(threadArg, runArg);
                    var run = getRun.Value;
                    Console.WriteLine($"Run {run.Id} Status={run.Status}");
                    if (run.Outputs != null)
                    {
                        Console.WriteLine("Run outputs:");
                        foreach (var o in run.Outputs)
                        {
                            Console.WriteLine($" - {o.Name} (Type={o.ContentType})");
                            try
                            {
                                var bd = o.Content;
                                var s = bd.ToString();
                                Console.WriteLine(s);
                            }
                            catch (Exception ex)
                            {
                                Console.WriteLine($"   (failed to read output content: {ex.Message})");
                            }
                        }
                    }
                    else
                    {
                        Console.WriteLine("No run outputs found on the run object.");
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Failed to fetch run: {ex}");
                }

                // also print the messages in the thread for context
                Console.WriteLine($"\nMessages in thread {threadArg}:");
                var msgs2 = client.Messages.GetMessages(threadArg).ToList();
                Console.WriteLine($"  Messages: {msgs2.Count}");
                foreach (var m in msgs2)
                {
                    Console.WriteLine($"    {m.CreatedAt} {m.Role}:");
                    foreach (var ci in m.ContentItems)
                    {
                        if (ci is MessageTextContent txt) Console.WriteLine(txt.Text);
                        else Console.WriteLine($"    (content type {ci.GetType().Name})");
                    }
                }

                return 0;
            }

            // List threads - this API may require paging; we just try to list some
            var threads = client.Threads.GetThreads();
            var threadList = threads.ToList();
            Console.WriteLine($"Found {threadList.Count} threads. Listing up to 10 with recent messages...");
            int count = 0;
            foreach (var t in threadList.OrderByDescending(t => t.CreatedAt))
            {
                Console.WriteLine($"Thread {t.Id} CreatedAt={t.CreatedAt}");
                var msgs = client.Messages.GetMessages(t.Id).ToList();
                Console.WriteLine($"  Messages: {msgs.Count}");
                foreach (var m in msgs)
                {
                    Console.WriteLine($"    {m.CreatedAt} {m.Role}: ");
                    foreach (var ci in m.ContentItems)
                    {
                        if (ci is MessageTextContent txt) Console.WriteLine(txt.Text);
                        else Console.WriteLine($"    (content type {ci.GetType().Name})");
                    }
                }
                count++;
                if (count >= 10) break;
            }

            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.ToString());
            return 1;
        }
    }
}
