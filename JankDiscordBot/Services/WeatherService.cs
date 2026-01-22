using System.Linq;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace GloomhavenRotationBot.Services;

public sealed class WeatherService
{
    private readonly HttpClient _http;
    private readonly AppSettingsService _settings;
    private readonly ILogger<WeatherService> _log;

    public WeatherService(HttpClient http, AppSettingsService settings, ILogger<WeatherService> log)
    {
        _http = http;
        _settings = settings;
        _log = log;
    }

    public async Task<string?> GetDailyForecastSummaryAsync(DateOnly date, CancellationToken ct = default)
    {
        var (lat, lon, units) = await _settings.GetWeatherConfigAsync();
        if (lat == null || lon == null)
            return null;

        return await GetDailyForecastSummaryForLocationAsync(date, lat.Value, lon.Value, units, ct);
    }

    /// <summary>
    /// Gets weather forecast for a specific location (per-user weather).
    /// </summary>
    public async Task<string?> GetDailyForecastSummaryForLocationAsync(DateOnly date, double latitude, double longitude, string units = "metric", CancellationToken ct = default)
    {
        var tempUnit = units == "metric" ? "celsius" : "fahrenheit";
        var precipUnit = units == "metric" ? "mm" : "inch";

        var url = $"https://api.open-meteo.com/v1/forecast?latitude={latitude}&longitude={longitude}&timezone=auto&forecast_days=7&daily=temperature_2m_max,temperature_2m_min,precipitation_probability_max,precipitation_sum&temperature_unit={tempUnit}&precipitation_unit={precipUnit}";

        try
        {
            using var res = await _http.GetAsync(url, ct);
            if (!res.IsSuccessStatusCode)
            {
                _log.LogWarning("Weather request failed: {Status}", res.StatusCode);
                return null;
            }

            using var stream = await res.Content.ReadAsStreamAsync(ct);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);

            if (!doc.RootElement.TryGetProperty("daily", out var daily))
                return null;

            var dates = daily.GetProperty("time").EnumerateArray().Select(x => DateOnly.Parse(x.GetString()!)).ToList();
            var maxTemps = daily.GetProperty("temperature_2m_max").EnumerateArray().Select(x => x.GetDouble()).ToList();
            var minTemps = daily.GetProperty("temperature_2m_min").EnumerateArray().Select(x => x.GetDouble()).ToList();
            var precipProb = daily.TryGetProperty("precipitation_probability_max", out var pp) ? pp.EnumerateArray().Select(x => x.GetDouble()).ToList() : new List<double>();
            var precipSum = daily.TryGetProperty("precipitation_sum", out var ps) ? ps.EnumerateArray().Select(x => x.GetDouble()).ToList() : new List<double>();

            var idx = dates.FindIndex(d => d == date);
            if (idx < 0) return null;

            var hi = maxTemps.ElementAtOrDefault(idx);
            var lo = minTemps.ElementAtOrDefault(idx);
            var chance = precipProb.Count > idx ? precipProb[idx] : (double?)null;
            var precip = precipSum.Count > idx ? precipSum[idx] : (double?)null;

            var tempUnitSymbol = units == "metric" ? "C" : "F";
            var precipUnitSymbol = units == "metric" ? "mm" : "in";

            var parts = new List<string>();
            parts.Add($"High {hi:0.#}°{tempUnitSymbol}, Low {lo:0.#}°{tempUnitSymbol}");
            if (chance.HasValue)
                parts.Add($"Precip {chance.Value:0.#}%");
            if (precip.HasValue)
                parts.Add($"Total {precip.Value:0.#}{precipUnitSymbol}");

            return $"Weather: {string.Join(", ", parts)}";
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Weather request failed");
            return null;
        }
    }
}
