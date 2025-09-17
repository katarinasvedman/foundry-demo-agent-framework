using System.Net.Http;
using System.Text;
using Newtonsoft.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;

namespace Foundry.Agents.Tools.OpenApi
{
    // A small, generic OpenAPI tool that can be used by agents to call arbitrary HTTP endpoints.
    // This keeps concerns separated and makes it straightforward to replace with a generated client.
    public class OpenApiTool
    {
        private readonly HttpClient _http;
        private readonly ILogger<OpenApiTool> _logger;
        private readonly string _baseUrl;

        public OpenApiTool(HttpClient http, ILogger<OpenApiTool> logger, IConfiguration config)
        {
            _http = http;
            _logger = logger;
            _baseUrl = config["OpenApi:BaseUrl"] ?? string.Empty;
            if (string.IsNullOrEmpty(_baseUrl))
            {
                _logger.LogWarning("OpenApi:BaseUrl is not configured. Calls will likely fail until configured.");
            }
        }

        public async Task<string> CallAsync(string method, string path, object? body)
        {
            var requestUri = string.IsNullOrEmpty(_baseUrl) ? new Uri(path, UriKind.RelativeOrAbsolute) : new Uri(new Uri(_baseUrl), path);
            var request = new HttpRequestMessage(new HttpMethod(method), requestUri);

            if (body != null)
            {
                var json = JsonConvert.SerializeObject(body);
                request.Content = new StringContent(json, Encoding.UTF8, "application/json");
            }

            // Basic retry policy for transient failures (simple, replace with Polly for production)
            for (int attempt = 1; attempt <= 3; attempt++)
            {
                try
                {
                    var resp = await _http.SendAsync(request);
                    resp.EnsureSuccessStatusCode();
                    var text = await resp.Content.ReadAsStringAsync();
                    return text;
                }
                catch (HttpRequestException ex) when (attempt < 3)
                {
                    _logger.LogWarning(ex, "Transient error calling OpenAPI endpoint (attempt {Attempt}).", attempt);
                    await Task.Delay(500 * attempt);
                }
            }

            throw new InvalidOperationException("OpenApi call failed after retries.");
        }
    }
}
