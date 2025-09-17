using System;
using System.IO;

namespace Foundry.Agents.Agents
{
    public static class InstructionReader
    {
        // Read the section for agentName from Agents/RemoteData/RemoteDataInstructions.md
        // Looks for a header like: "# <AgentName> Agent Instructions" and returns the content until the next '---' or next top-level header.
        public static string ReadSection(string agentName)
        {
            try
            {
                var path = Path.Combine("Agents", "RemoteData", "RemoteDataInstructions.md");
                if (!File.Exists(path)) return string.Empty;
                var txt = File.ReadAllText(path);

                var header = $"# {agentName} Agent Instructions";
                var idx = txt.IndexOf(header, StringComparison.OrdinalIgnoreCase);
                if (idx < 0)
                {
                    // Fallback: return the whole file
                    return txt;
                }

                var start = idx + header.Length;
                var sepIdx = txt.IndexOf("\n---", start, StringComparison.Ordinal);
                if (sepIdx < 0)
                {
                    sepIdx = txt.IndexOf("\n# ", start, StringComparison.Ordinal);
                }

                var section = sepIdx > 0 ? txt.Substring(start, sepIdx - start) : txt.Substring(start);
                return section.Trim();
            }
            catch
            {
                return string.Empty;
            }
        }
    }
}
