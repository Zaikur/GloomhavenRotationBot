using System.Net.Http;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace GloomhavenRotationBot.Services;

/// <summary>
/// Minimal helper for sending chat-style prompts to an AI model.
/// </summary>
public sealed class AiTextService
{
    private readonly HttpClient _http;
    private readonly ILogger<AiTextService> _log;
    private readonly IConfiguration _config;
    private readonly AppSettingsService _settings;

    public AiTextService(HttpClient http, IConfiguration config, AppSettingsService settings, ILogger<AiTextService> log)
    {
        _http = http;
        _config = config;
        _settings = settings;
        _log = log;
    }

    public async Task<string> GenerateAsync(string systemPrompt, string userPrompt, string fallback, float temperature = 0.5f, int maxTokens = 256, CancellationToken ct = default)
    {
        var (provider, endpointStored, modelStored, keyStored) = await _settings.GetAiConfigAsync();

        var endpoint = !string.IsNullOrWhiteSpace(endpointStored)
            ? endpointStored
            : (_config["AI:Endpoint"] ?? _config["OpenAI:Endpoint"] ?? "https://api.openai.com/v1/chat/completions");

        var model = !string.IsNullOrWhiteSpace(modelStored)
            ? modelStored
            : (_config["AI:Model"] ?? _config["OpenAI:Model"] ?? "gpt-3.5-turbo");

        var apiKey = !string.IsNullOrWhiteSpace(keyStored)
            ? keyStored
            : (_config["AI:ApiKey"] ?? _config["OpenAI:ApiKey"]);

        var providerLabel = string.IsNullOrWhiteSpace(provider) ? "default" : provider;

        // Some providers like may not require an API key
        var requireKey = string.IsNullOrWhiteSpace(providerLabel)
            ? true
            : !providerLabel.Equals("ollama", StringComparison.OrdinalIgnoreCase);

        if (requireKey && string.IsNullOrWhiteSpace(apiKey))
        {
            _log.LogWarning("AI:ApiKey not configured; returning fallback response");
            return fallback;
        }

        try
        {
            var payload = new
            {
                model,
                temperature,
                max_tokens = maxTokens,
                messages = new[]
                {
                    new { role = "system", content = systemPrompt },
                    new { role = "user", content = userPrompt }
                }
            };

            var json = JsonSerializer.Serialize(payload);
            using var req = new HttpRequestMessage(HttpMethod.Post, endpoint)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };
            if (!string.IsNullOrWhiteSpace(apiKey))
                req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);

            using var res = await _http.SendAsync(req, ct);
            if (!res.IsSuccessStatusCode)
            {
                var body = await res.Content.ReadAsStringAsync(ct);
                _log.LogWarning("AI request failed: {Status} {Body}", res.StatusCode, body);
                return fallback;
            }

            using var stream = await res.Content.ReadAsStreamAsync(ct);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);

            var content = doc.RootElement
                .GetProperty("choices")[0]
                .GetProperty("message")
                .GetProperty("content")
                .GetString();

            return string.IsNullOrWhiteSpace(content) ? fallback : content.Trim();
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "AI request failed; returning fallback");
            return fallback;
        }
    }
}
