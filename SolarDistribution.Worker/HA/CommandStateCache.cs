using System.Text.Json;
using Microsoft.Extensions.Logging;
using SolarDistribution.Worker.Configuration;

namespace SolarDistribution.Worker.HA;

/// <summary>
/// Persistent cache of the last sent values and zone states (0W ↔ >0W)
/// per battery, saved to disk as JSON.
///
/// Survives Docker restarts / host reboots:
///   - On startup: loads state from /data/state/command-state.json
///   - After each successful write: atomic save (write temp + rename)
///
/// If the file is missing or corrupted → empty state (same behavior as before:
/// conditional actions trigger once on the first cycle).
/// </summary>
public class CommandStateCache
{
    // ── State file path ──────────────────────────────────────────────────────

    private readonly string _statePath;
    private readonly ILogger<CommandStateCache> _logger;

    // ── In-memory state ──────────────────────────────────────────────────────

    private CacheData _data = new();

    public CommandStateCache(SolarConfig config, ILogger<CommandStateCache> logger)
    {
        _logger = logger;

        // Derive directory from log path (/data/logs → /data/state)
        string logDir = Path.GetDirectoryName(config.Logging.FilePath ?? "/data/logs/solar-worker.log")
                          ?? "/data/logs";
        string dataDir = Path.Combine(Path.GetDirectoryName(logDir) ?? "/data", "state");
        _statePath = Path.Combine(dataDir, "command-state.json");

        Load();
    }

    // ── Public API ───────────────────────────────────────────────────────────

    /// <summary>Last raw value sent to HA for this battery. null = never sent.</summary>
    public double? GetLastSentValue(int batteryId)
        => _data.LastSentValues.TryGetValue(batteryId, out double v) ? v : null;

    /// <summary>Last zone state (true = was 0W). null = never sent.</summary>
    public bool? GetLastWasZero(int batteryId)
        => _data.LastWasZero.TryGetValue(batteryId, out bool v) ? v : null;

    /// <summary>
    /// Updates a battery state and persists immediately to disk.
    /// Called only after a successful HA command.
    /// </summary>
    public void Update(int batteryId, double sentValue, bool wasZero)
    {
        _data.LastSentValues[batteryId] = sentValue;
        _data.LastWasZero[batteryId] = wasZero;
        _data.LastUpdatedUtc = DateTime.UtcNow;
        Save();
    }

    public void UpdateZoneOnly(int batteryId, bool wasZero)
    {
        _data.LastWasZero[batteryId] = wasZero;
        _data.LastUpdatedUtc = DateTime.UtcNow;
        Save();
    }

    // ── Persistence ──────────────────────────────────────────────────────────

    private void Load()
    {
        if (!File.Exists(_statePath))
        {
            _logger.LogInformation("CommandStateCache: no state file found at {Path} — starting fresh", _statePath);
            return;
        }

        try
        {
            string json = File.ReadAllText(_statePath);
            var loaded = JsonSerializer.Deserialize<CacheData>(json);
            if (loaded is not null)
            {
                _data = loaded;
                _logger.LogInformation(
                    "CommandStateCache: loaded {Count} battery state(s) from {Path} (last update: {Ts:u})",
                    _data.LastSentValues.Count, _statePath, _data.LastUpdatedUtc);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "CommandStateCache: failed to load state from {Path} — starting fresh", _statePath);
            _data = new();
        }
    }

    private void Save()
    {
        try
        {
            string dir = Path.GetDirectoryName(_statePath)!;
            Directory.CreateDirectory(dir);

            // Atomic write: temp file + rename to avoid corruption
            string tmp = _statePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(_data, new JsonSerializerOptions
            {
                WriteIndented = true
            }));
            File.Move(tmp, _statePath, overwrite: true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "CommandStateCache: failed to persist state to {Path}", _statePath);
        }
    }

    // ── JSON model ───────────────────────────────────────────────────────────

    private class CacheData
    {
        public Dictionary<int, double> LastSentValues { get; set; } = new();
        public Dictionary<int, bool> LastWasZero { get; set; } = new();
        public DateTime LastUpdatedUtc { get; set; } = DateTime.UtcNow;
    }
}