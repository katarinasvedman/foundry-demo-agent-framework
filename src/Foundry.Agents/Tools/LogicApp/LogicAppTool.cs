using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Azure.Core;
using Azure.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;

namespace Foundry.Agents.Tools.LogicApp
{
    // Lightweight helper to invoke a Logic App HTTP-trigger endpoint using AAD (DefaultAzureCredential)
    public class LogicAppTool
    {
        private readonly HttpClient _http;
        private readonly TokenCredential _credential;
        private readonly ILogger<LogicAppTool> _logger;
        private readonly string _baseUrl;

        public LogicAppTool(HttpClient http, TokenCredential credential, ILogger<LogicAppTool> logger, IConfiguration config)
        {
            _http = http ?? throw new ArgumentNullException(nameof(http));
            _credential = credential ?? throw new ArgumentNullException(nameof(credential));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _baseUrl = config["LogicApp:BaseUrl"] ?? string.Empty;
            if (string.IsNullOrEmpty(_baseUrl)) _logger.LogWarning("LogicApp:BaseUrl not configured; provide a full trigger URI or set LogicApp:BaseUrl in config.");
        }

        // Invoke path (e.g. "/invoke") with optional query params. Uses AAD token for audience https://logic.azure.com/
        public async Task<string> InvokeAsync(string path, object? body, IDictionary<string,string>? queryParams = null)
        {
            var baseUri = string.IsNullOrEmpty(_baseUrl) ? new Uri(path, UriKind.RelativeOrAbsolute) : new Uri(new Uri(_baseUrl), path);

            var uriBuilder = new UriBuilder(baseUri);
            if (queryParams != null)
            {
                var qb = System.Web.HttpUtility.ParseQueryString(uriBuilder.Query ?? string.Empty);
                foreach (var kv in queryParams) qb[kv.Key] = kv.Value;
                uriBuilder.Query = qb.ToString() ?? string.Empty;
            }

            var request = new HttpRequestMessage(HttpMethod.Post, uriBuilder.Uri);
            if (body != null)
            {
                var json = JsonConvert.SerializeObject(body);
                request.Content = new StringContent(json, Encoding.UTF8, "application/json");
            }

            // Acquire AAD token for Logic Apps audience
            try
            {
                var tokenRequest = new TokenRequestContext(new[] { "https://logic.azure.com/.default" });
                var token = await _credential.GetTokenAsync(tokenRequest, default);
                request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token.Token);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to acquire AAD token for Logic App invocation");
                throw;
            }

            var resp = await _http.SendAsync(request);
            var text = await resp.Content.ReadAsStringAsync();
            if (!resp.IsSuccessStatusCode)
            {
                _logger.LogWarning("Logic App invocation returned non-success status {Status}. Response: {Response}", resp.StatusCode, text);
                throw new HttpRequestException($"LogicApp returned {(int)resp.StatusCode}: {text}");
            }

            return text;
        }
    }
}
