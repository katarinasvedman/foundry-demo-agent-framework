using System;
using System.IO;

namespace Foundry.Agents.Agents
{
    public static class InstructionReader
    {
        // Read the section for agentName from Agents/RemoteData/RemoteDataInstructions.md
        // Prefer a per-agent file at Agents/<Agent>/<Agent>Instructions.md, falling back to the shared RemoteData file.
        // If neither exists, return empty string.
        public static string ReadSection(string agentName)
        {
            try
            {
                // 1) per-agent file
                var perAgentPath = Path.Combine("Agents", agentName, agentName + "Instructions.md");
                if (File.Exists(perAgentPath))
                {
                    return File.ReadAllText(perAgentPath).Trim();
                }

                // 2) legacy composite file (RemoteDataInstructions.md) with headers
                var path = Path.Combine("Agents", "RemoteData", "RemoteDataInstructions.md");
                if (!File.Exists(path)) return string.Empty;
                var txt = File.ReadAllText(path);

                var header = $"# {agentName} Agent Instructions";
                var idx = txt.IndexOf(header, StringComparison.OrdinalIgnoreCase);
                if (idx < 0)
                {
                    // Fallback: return the whole file
                    return txt.Trim();
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
