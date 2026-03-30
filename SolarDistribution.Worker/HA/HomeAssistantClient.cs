using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using SolarDistribution.Worker.Configuration;

namespace SolarDistribution.Worker.HA;

// ── HA response DTO ──────────────────────────────────────────────────────────

public record HaState(
    [property: JsonPropertyName("entity_id")]  string EntityId,
    [property: JsonPropertyName("state")]      string State,
    [property: JsonPropertyName("attributes")] JsonElement Attributes,
    [property: JsonPropertyName("last_updated")] DateTime LastUpdated
);

// ── Interface ─────────────────────────────────────────────────────────────────

public interface IHomeAssistantClient
{
    Task<HaState?> GetStateAsync(string entityId, CancellationToken ct = default);
    Task<double?>  GetNumericStateAsync(string entityId, CancellationToken ct = default);
    Task<bool>     SetNumberValueAsync(string entityId, double value, CancellationToken ct = default);
    Task<bool>     TurnOnSwitchAsync(string entityId, CancellationToken ct = default);
    Task<bool>     TurnOffSwitchAsync(string entityId, CancellationToken ct = default);
    Task<bool>     CallServiceGenericAsync(string domain, string service, Dictionary<string, object>? data, CancellationToken ct = default);
    Task<bool>     PingAsync(CancellationToken ct = default);
}

// ── Implementation ───────────────────────────────────────────────────────────

/// <summary>
/// HTTP client for the Home Assistant REST API.
///
/// Read   : GET  /api/states/{entity_id}
/// Write  : POST /api/services/number/set_value   → controls power in W
///          POST /api/services/homeassistant/turn_on|turn_off → enable/disable switch
///
/// Resilience: retry + circuit breaker configured via Microsoft.Extensions.Http.Resilience
/// dans Program.cs (AddResilienceHandler).
/// </summary>
public class HomeAssistantClient : IHomeAssistantClient
{
    private readonly HttpClient _http;
    private readonly ILogger<HomeAssistantClient> _logger;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public HomeAssistantClient(HttpClient http, ILogger<HomeAssistantClient> logger)
    {
        _http   = http;
        _logger = logger;
    }

    // ── Read ─────────────────────────────────────────────────────────────────

    public async Task<HaState?> GetStateAsync(string entityId, CancellationToken ct = default)
    {
        try
        {
            var state = await _http.GetFromJsonAsync<HaState>(
                $"/api/states/{entityId}", JsonOpts, ct);

            _logger.LogDebug("HA state [{Entity}] = {State}", entityId, state?.State);
            return state;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to read HA state for {Entity}", entityId);
            return null;
        }
    }

    public async Task<double?> GetNumericStateAsync(string entityId, CancellationToken ct = default)
    {
        var state = await GetStateAsync(entityId, ct);

        if (state is null) return null;

        if (double.TryParse(state.State,
            System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture,
            out double value))
        {
            return value;
        }

        _logger.LogWarning("HA entity {Entity} state '{State}' is not numeric", entityId, state.State);
        return null;
    }

    // ── Write ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Sets the value of a 'number' entity in HA.
    /// Utilise POST /api/services/number/set_value
    /// Compatible with all inverters/batteries exposed as number.* in HA.
    /// </summary>
    public async Task<bool> SetNumberValueAsync(string entityId, double value, CancellationToken ct = default)
    {
        return await CallServiceAsync("number", "set_value",
            new { entity_id = entityId, value }, ct);
    }

    public async Task<bool> TurnOnSwitchAsync(string entityId, CancellationToken ct = default)
        => await CallServiceAsync("homeassistant", "turn_on", new { entity_id = entityId }, ct);

    public async Task<bool> TurnOffSwitchAsync(string entityId, CancellationToken ct = default)
        => await CallServiceAsync("homeassistant", "turn_off", new { entity_id = entityId }, ct);

    /// <summary>
    /// Generic call to any HA service.
    /// Allows executing custom actions configured in ZeroWActions / NonZeroWActions.
    /// Ex : domain="input_boolean", service="turn_on", data={ "entity_id": "..." }
    /// </summary>
    public async Task<bool> CallServiceGenericAsync(
        string domain,
        string service,
        Dictionary<string, object>? data,
        CancellationToken ct = default)
        => await CallServiceAsync(domain, service, (object?)data ?? new { }, ct);

    public async Task<bool> PingAsync(CancellationToken ct = default)
    {
        try
        {
            var response = await _http.GetAsync("/api/", ct);
            return response.IsSuccessStatusCode;
        }
        catch { return false; }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private async Task<bool> CallServiceAsync(
        string domain, string service, object payload, CancellationToken ct)
    {
        string url = $"/api/services/{domain}/{service}";
        try
        {
            var response = await _http.PostAsJsonAsync(url, payload, ct);

            if (response.IsSuccessStatusCode)
            {
                _logger.LogDebug("HA service {Domain}.{Service} called successfully", domain, service);
                return true;
            }

            string body = await response.Content.ReadAsStringAsync(ct);
            _logger.LogWarning(
                "HA service {Domain}.{Service} returned {Status}: {Body}",
                domain, service, response.StatusCode, body);
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to call HA service {Domain}.{Service}", domain, service);
            return false;
        }
    }
}
