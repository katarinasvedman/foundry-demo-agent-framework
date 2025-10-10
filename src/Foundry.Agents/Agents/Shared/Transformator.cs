using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Foundry.Agents.Agents.Shared
{
    public static class Transformator
    {
        // Default inline threshold in bytes (300 KB)
        private const int DefaultInlineThreshold = 300 * 1024;

        public static int GetInlineThreshold(IConfiguration? configuration)
        {
            if (configuration == null) return DefaultInlineThreshold;
            return configuration.GetValue<int?>("Transformator:InlineThresholdBytes") ?? DefaultInlineThreshold;
        }

        // Accepts a raw envelope object (possibly nested or with 'arguments' string) and returns
        // a canonical normalized envelope suitable for the EmailAgent and Logic App.
        public static JsonElement NormalizeEnvelope(object rawEnvelope, IConfiguration? configuration, ILogger? logger)
        {
            // Convert input to JsonElement for simpler traversal
            JsonElement root;
            if (rawEnvelope is JsonElement je)
            {
                root = je;
            }
            else
            {
                var json = JsonSerializer.Serialize(rawEnvelope);
                root = JsonSerializer.Deserialize<JsonElement>(json);
            }

            // If the root is an array of items (common when collecting stream events),
            // attempt to pick the most relevant object element. Prefer an element that
            // contains email fields, attachments or an agent == "EmailGenerator". Fall
            // back to the first object element or the root itself.
            JsonElement parsed = root;
            if (root.ValueKind == JsonValueKind.Array)
            {
                JsonElement? chosen = null;
                JsonElement energyElement = default;
                bool energyFound = false;
                JsonElement emailElement = default;
                bool emailFound = false;
                foreach (var el in root.EnumerateArray())
                {
                    JsonElement candidate = default;
                    bool hasCandidate = false;

                    // If element is already an object, consider it directly
                    if (el.ValueKind == JsonValueKind.Object)
                    {
                        candidate = el;
                        hasCandidate = true;
                    }
                    else if (el.ValueKind == JsonValueKind.String)
                    {
                        // Try to parse the string as JSON (strip fences if present)
                        var s = el.GetString() ?? "";
                        s = s.Trim();
                        if (s.StartsWith("```"))
                        {
                            // remove triple-backtick fences and optional language tag
                            var idx = s.IndexOf("\n");
                            if (idx >= 0)
                            {
                                // remove first line if it's a fence marker like ```json
                                var rest = s.Substring(idx + 1);
                                // drop trailing fence if present
                                if (rest.TrimEnd().EndsWith("```"))
                                {
                                    rest = rest.TrimEnd();
                                    rest = rest.Substring(0, rest.Length - 3).TrimEnd();
                                }
                                s = rest;
                            }
                        }
                        else if (s.StartsWith("`") && s.EndsWith("`"))
                        {
                            s = s.Trim('`').Trim();
                        }

                        if (!string.IsNullOrEmpty(s))
                        {
                            // First try parsing the whole string
                            try
                            {
                                var parsedStr = JsonSerializer.Deserialize<JsonElement>(s);
                                if (parsedStr.ValueKind == JsonValueKind.Object)
                                {
                                    candidate = parsedStr;
                                    hasCandidate = true;
                                }
                            }
                            catch
                            {
                                // If full parse fails, try to extract the first JSON object substring from the string
                                try
                                {
                                    int start = s.IndexOf('{');
                                    if (start >= 0)
                                    {
                                        int depth = 0;
                                        for (int i = start; i < s.Length; i++)
                                        {
                                            if (s[i] == '{') depth++;
                                            else if (s[i] == '}')
                                            {
                                                depth--;
                                                if (depth == 0)
                                                {
                                                    var sub = s.Substring(start, i - start + 1);
                                                    try
                                                    {
                                                        var parsedSub = JsonSerializer.Deserialize<JsonElement>(sub);
                                                        if (parsedSub.ValueKind == JsonValueKind.Object)
                                                        {
                                                            candidate = parsedSub;
                                                            hasCandidate = true;
                                                        }
                                                    }
                                                    catch
                                                    {
                                                        // ignore
                                                    }
                                                    break;
                                                }
                                            }
                                        }
                                    }
                                }
                                catch
                                {
                                    // ignore
                                }
                            }
                        }
                    }

                    if (!hasCandidate) continue;

                    bool looksLikeEmail = false;
                    if (candidate.TryGetProperty("email_to", out _)) looksLikeEmail = true;
                    else if (candidate.TryGetProperty("attachments", out _)) looksLikeEmail = true;
                    else if (candidate.TryGetProperty("agent", out var a) && a.ValueKind == JsonValueKind.String && a.GetString() == "EmailGenerator") looksLikeEmail = true;
                    else if (candidate.TryGetProperty("data", out var d) && d.ValueKind == JsonValueKind.Object && d.TryGetProperty("email_to", out _)) looksLikeEmail = true;

                    if (looksLikeEmail)
                    {
                        // prefer the latest matching element as email element
                        emailElement = candidate;
                        emailFound = true;
                    }

                    // also detect Energy envelope explicitly
                    if (candidate.TryGetProperty("agent", out var a2) && a2.ValueKind == JsonValueKind.String && a2.GetString() == "Energy")
                    {
                        energyElement = candidate;
                        energyFound = true;
                    }

                    if (chosen == null) chosen = candidate; // first object fallback
                }
                if (emailFound || energyFound)
                {
                    // If both present, create a merged parsed object that contains both
                    if (emailFound && energyFound)
                    {
                        // create a synthetic parsed object that contains both under clear keys
                        var merged = new Dictionary<string, object?>();
                        merged["agent"] = "Merged/Orchestrator";
                        merged["email"] = JsonSerializer.Deserialize<object>(emailElement.GetRawText());
                        merged["energy"] = JsonSerializer.Deserialize<object>(energyElement.GetRawText());
                        var mergedJson = JsonSerializer.Serialize(merged);
                        parsed = JsonSerializer.Deserialize<JsonElement>(mergedJson);
                        logger?.LogInformation("Transformator: merged Energy and EmailGenerator elements for normalization");

                        // If merged, prefer to continue parsing using the email sub-object but keep the energy object for output
                        if (parsed.TryGetProperty("email", out var emailSub) && parsed.TryGetProperty("energy", out var energySub))
                        {
                            // keep energy for later
                            var energyForOutput = energySub;
                            parsed = emailSub; // continue parsing using the email object
                            // attach energyForOutput to a local variable via closure by reserializing into a field the outer scope can access
                            // We'll set it into a temporary property by using the output dictionary later.
                            // To pass it down, we will store its raw text in a local variable below.
                            // (store in a temp variable defined outside of this if-block)
                            // We'll set energyRaw later
                            // Use a trick: write energyRaw back to the logger context via debug - instead we'll capture energyRaw by re-parsing later below.
                        }
                    }
                    else if (emailFound)
                    {
                        parsed = emailElement;
                        logger?.LogInformation("Transformator: selected email element from array for normalization");
                    }
                    else
                    {
                        parsed = energyElement;
                        logger?.LogInformation("Transformator: selected energy element from array for normalization");
                    }

                    // If we selected energy but didn't find an email element, try to recover an email object
                    // embedded inside string elements of the original root array.
                    if (!emailFound && energyFound)
                    {
                        var recovered = RecoverEmailFromRootArray(root);
                        if (recovered.HasValue && recovered.Value.ValueKind == JsonValueKind.Object)
                        {
                            parsed = recovered.Value;
                            logger?.LogInformation("Transformator: recovered email element from root array after selecting energy");
                        }
                    }
                    // If we selected energy but didn't find an email element, try one more pass
                    // across string elements in the original array to locate an embedded email JSON
                    // (some agents print prose and then a JSON block as a string). This heuristic
                    // looks for markers like "email_to" or the EmailGenerator agent tag and then
                    // attempts to extract and parse a JSON object substring.
                    if (!emailFound && energyFound)
                    {
                        try
                        {
                            foreach (var el2 in root.EnumerateArray())
                            {
                                if (el2.ValueKind != JsonValueKind.String) continue;
                                var s2 = el2.GetString() ?? string.Empty;
                                if (string.IsNullOrWhiteSpace(s2)) continue;
                                // quick marker check to avoid unnecessary parsing
                                if (!(s2.Contains("email_to") || s2.Contains("\"agent\"") || s2.Contains("EmailGenerator"))) continue;

                                var candidateText = s2.Trim();
                                if (candidateText.StartsWith("```"))
                                {
                                    var idx2 = candidateText.IndexOf('\n');
                                    if (idx2 >= 0)
                                    {
                                        var rest2 = candidateText.Substring(idx2 + 1);
                                        if (rest2.TrimEnd().EndsWith("```")) rest2 = rest2.Substring(0, rest2.Length - 3).TrimEnd();
                                        candidateText = rest2;
                                    }
                                }
                                else if (candidateText.StartsWith("`") && candidateText.EndsWith("`"))
                                {
                                    candidateText = candidateText.Trim('`').Trim();
                                }

                                // Try parsing the whole cleaned string first
                                try
                                {
                                    var parsedCandidate = JsonSerializer.Deserialize<JsonElement>(candidateText);
                                    if (parsedCandidate.ValueKind == JsonValueKind.Object)
                                    {
                                        // found an email object embedded as a string
                                        parsed = parsedCandidate;
                                        logger?.LogInformation("Transformator: recovered email element from string after initial selection of energy");
                                        break;
                                    }
                                }
                                catch
                                {
                                    // If full parse fails, try to extract the first balanced { ... } block
                                    try
                                    {
                                        int start2 = candidateText.IndexOf('{');
                                        if (start2 >= 0)
                                        {
                                            int depth2 = 0;
                                            for (int i2 = start2; i2 < candidateText.Length; i2++)
                                            {
                                                if (candidateText[i2] == '{') depth2++;
                                                else if (candidateText[i2] == '}')
                                                {
                                                    depth2--;
                                                    if (depth2 == 0)
                                                    {
                                                        var sub2 = candidateText.Substring(start2, i2 - start2 + 1);
                                                        try
                                                        {
                                                            var parsedSub2 = JsonSerializer.Deserialize<JsonElement>(sub2);
                                                            if (parsedSub2.ValueKind == JsonValueKind.Object)
                                                            {
                                                                parsed = parsedSub2;
                                                                logger?.LogInformation("Transformator: extracted and parsed embedded JSON email object from string");
                                                                break;
                                                            }
                                                        }
                                                        catch { }
                                                        break;
                                                    }
                                                }
                                            }
                                            // if we already set parsed to an object, break out of outer loop
                                            if (parsed.ValueKind == JsonValueKind.Object) break;
                                        }
                                    }
                                    catch { }
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            logger?.LogWarning(ex, "Transformator: secondary pass to recover email element failed");
                        }
                    }
                }
                else
                {
                    // no object elements - fall back to root
                    parsed = root;
                    logger?.LogWarning("Transformator: JSON array root contained no object elements; using array as-is");
                }
            }
            else if (root.TryGetProperty("arguments", out var argsProp) && argsProp.ValueKind == JsonValueKind.String)
            {
                try
                {
                    parsed = JsonSerializer.Deserialize<JsonElement>(argsProp.GetString() ?? "{}");
                }
                catch (Exception ex)
                {
                    logger?.LogWarning(ex, "Transformator: failed to parse 'arguments' as json");
                    parsed = root; // fallback
                }
            }

            // Helpers to find a property from multiple locations
            JsonElement GetFirst(params string[] paths)
            {
                foreach (var p in paths)
                {
                    // single-level path like "data.attachments" supported
                    var parts = p.Split('.', StringSplitOptions.RemoveEmptyEntries);
                    JsonElement cur = parsed;
                    bool ok = true;
                    foreach (var part in parts)
                    {
                        if (cur.ValueKind != JsonValueKind.Object || !cur.TryGetProperty(part, out cur)) { ok = false; break; }
                    }
                    if (ok) return cur;
                }
                return default;
            }

            // Secondary-pass helper: scan string elements in the original root array to recover
            // an embedded email JSON object (some agents print prose and then a JSON block as a string).
            JsonElement? RecoverEmailFromRootArray(JsonElement rootArray)
            {
                try
                {
                    foreach (var el2 in rootArray.EnumerateArray())
                    {
                        if (el2.ValueKind != JsonValueKind.String) continue;
                        var s2 = el2.GetString() ?? string.Empty;
                        if (string.IsNullOrWhiteSpace(s2)) continue;
                        if (!(s2.Contains("email_to") || s2.Contains("\"agent\"") || s2.Contains("EmailGenerator"))) continue;

                        var candidateText = s2.Trim();
                        if (candidateText.StartsWith("```"))
                        {
                            var idx2 = candidateText.IndexOf('\n');
                            if (idx2 >= 0)
                            {
                                var rest2 = candidateText.Substring(idx2 + 1);
                                if (rest2.TrimEnd().EndsWith("```")) rest2 = rest2.Substring(0, rest2.Length - 3).TrimEnd();
                                candidateText = rest2;
                            }
                        }
                        else if (candidateText.StartsWith("`") && candidateText.EndsWith("`"))
                        {
                            candidateText = candidateText.Trim('`').Trim();
                        }

                        try
                        {
                            var parsedCandidate = JsonSerializer.Deserialize<JsonElement>(candidateText);
                            if (parsedCandidate.ValueKind == JsonValueKind.Object) return parsedCandidate;
                        }
                        catch
                        {
                            int start2 = candidateText.IndexOf('{');
                            if (start2 >= 0)
                            {
                                int depth2 = 0;
                                for (int i2 = start2; i2 < candidateText.Length; i2++)
                                {
                                    if (candidateText[i2] == '{') depth2++;
                                    else if (candidateText[i2] == '}')
                                    {
                                        depth2--;
                                        if (depth2 == 0)
                                        {
                                            var sub2 = candidateText.Substring(start2, i2 - start2 + 1);
                                            try
                                            {
                                                var parsedSub2 = JsonSerializer.Deserialize<JsonElement>(sub2);
                                                if (parsedSub2.ValueKind == JsonValueKind.Object) return parsedSub2;
                                            }
                                            catch { }
                                            break;
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
                catch { }
                return null;
            }

            var attachmentsElement = GetFirst("attachments", "data.attachments");

            // Attempt to locate an Energy envelope from the original root (useful when merged)
            JsonElement? energyEnvelopeFromRoot = null;
            if (root.ValueKind == JsonValueKind.Array)
            {
                foreach (var el in root.EnumerateArray())
                {
                    try
                    {
                        JsonElement candidate = el;
                        if (candidate.ValueKind == JsonValueKind.String)
                        {
                            var s = candidate.GetString() ?? "";
                            s = s.Trim();
                            if (s.StartsWith("```"))
                            {
                                var idx = s.IndexOf('\n');
                                if (idx >= 0)
                                {
                                    var rest = s.Substring(idx + 1);
                                    if (rest.TrimEnd().EndsWith("```")) rest = rest.TrimEnd();
                                    if (rest.TrimEnd().EndsWith("```")) rest = rest.Substring(0, rest.Length - 3).TrimEnd();
                                    s = rest;
                                }
                            }
                            try { candidate = JsonSerializer.Deserialize<JsonElement>(s); } catch { }
                        }

                        if (candidate.ValueKind == JsonValueKind.Object && candidate.TryGetProperty("agent", out var a) && a.ValueKind == JsonValueKind.String && a.GetString() == "Energy")
                        {
                            energyEnvelopeFromRoot = candidate;
                            break;
                        }
                    }
                    catch { }
                }
            }

            // Build canonical object
            var output = new Dictionary<string, object?>();

            // recipients as array
            var emailToList = new List<string>();
            var toElement = GetFirst("email_to", "data.email_to");
            if (toElement.ValueKind == JsonValueKind.String)
            {
                var s = toElement.GetString();
                if (!string.IsNullOrEmpty(s)) emailToList.Add(s);
            }
            else if (toElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in toElement.EnumerateArray()) if (item.ValueKind == JsonValueKind.String) emailToList.Add(item.GetString()!);
            }
            output["email_to"] = emailToList;

            // subject/body
            var subject = GetFirst("email_subject", "data.email_subject");
            output["email_subject"] = subject.ValueKind == JsonValueKind.String ? subject.GetString() : "";
            var bodyMd = GetFirst("email_body_markdown", "data.email_body_markdown", "email_body", "data.email_body");
            output["email_body_html"] = bodyMd.ValueKind == JsonValueKind.String ? bodyMd.GetString() : "";

            // attachments mapping
            var inlineThreshold = GetInlineThreshold(configuration);
            var attachmentsOut = new List<Dictionary<string, object?>>();
            var diagnosticsList = new List<Dictionary<string, object?>>();

            if (attachmentsElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in attachmentsElement.EnumerateArray())
                {
                    try
                    {
                        var filename = item.TryGetProperty("filename", out var f) && f.ValueKind == JsonValueKind.String ? f.GetString() : null;
                        var contentBase64 = item.TryGetProperty("content_base64", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() : null;
                        var contentType = item.TryGetProperty("content_type", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : "application/octet-stream";
                        int size = 0;
                        if (item.TryGetProperty("size_bytes", out var s) && s.ValueKind == JsonValueKind.Number)
                        {
                            size = s.GetInt32();
                        }
                        else if (!string.IsNullOrEmpty(contentBase64))
                        {
                            try
                            {
                                size = Convert.FromBase64String(contentBase64).Length;
                            }
                            catch
                            {
                                // leave size as 0 if base64 invalid
                                size = 0;
                            }
                        }

                        var entry = new Dictionary<string, object?>();
                        entry["filename"] = filename;
                        entry["content_type"] = contentType;
                        entry["size_bytes"] = size;

                        var diag = new Dictionary<string, object?>();
                        diag["filename"] = filename;
                        diag["original_size_bytes"] = size;

                        if (!string.IsNullOrEmpty(contentBase64) && size > 0 && size <= inlineThreshold)
                        {
                            entry["content_base64"] = contentBase64;
                            entry["action"] = "inlined";
                            diag["action"] = "inlined";
                            diag["final_size_bytes"] = size;
                        }
                        else if (!string.IsNullOrEmpty(contentBase64) && size > inlineThreshold)
                        {
                            // Enforce inline-only policy: omit oversized attachments and record diagnostics
                            entry["action"] = "omitted";
                            diag["action"] = "omitted";
                            diag["note"] = $"Attachment omitted due to size > {inlineThreshold} bytes";
                        }
                        else
                        {
                            entry["action"] = "no_content";
                            diag["action"] = "no_content";
                            diag["note"] = "No inline content available";
                        }

                        attachmentsOut.Add(entry);
                        diagnosticsList.Add(diag);
                    }
                    catch (Exception ex)
                    {
                        logger?.LogWarning(ex, "Transformator: failed to process an attachment item");
                    }
                }
            }

            output["attachments"] = attachmentsOut;
            output["diagnostics"] = new { attachments = diagnosticsList, transform_time_ms = 0 };

            // Include Energy envelope if we found it in the original root
            if (energyEnvelopeFromRoot.HasValue)
            {
                try
                {
                    output["energy"] = JsonSerializer.Deserialize<object>(energyEnvelopeFromRoot.Value.GetRawText());
                }
                catch { }
            }

            // Defensive: also include a `data` object with common email fields and a string recipient
            try
            {
                // emailToList exists above; create comma-joined string as well
                var emailToStr = string.Join(',', emailToList ?? new System.Collections.Generic.List<string>());
                var dataObj = new System.Collections.Generic.Dictionary<string, object?>();
                dataObj["email_to"] = emailToList;
                dataObj["email_to_str"] = emailToStr;
                dataObj["email_subject"] = output.ContainsKey("email_subject") ? output["email_subject"] : "";
                dataObj["email_body_html"] = output.ContainsKey("email_body_html") ? output["email_body_html"] : "";
                dataObj["attachments"] = attachmentsOut;
                output["data"] = dataObj;
                // Also expose a top-level string-form recipient for connectors that expect a single string
                output["email_to_str"] = emailToStr;
            }
            catch { }

            // serialize to JsonElement
            var outJson = JsonSerializer.Serialize(output);
            return JsonSerializer.Deserialize<JsonElement>(outJson);
        }
    }
}
