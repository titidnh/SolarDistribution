using System.Text.Json;
using Microsoft.Extensions.Logging;
using SolarDistribution.Worker.Configuration;

namespace SolarDistribution.Worker.HA;

/// <summary>
/// Cache persistant des dernières valeurs envoyées et des états de zone (0W ↔ >0W)
/// par batterie, sauvegardé sur disque en JSON.
///
/// Survie aux redémarrages Docker / reboot host :
///   - Au démarrage : charge l'état depuis /data/state/command-state.json
///   - Après chaque écriture réussie : sauvegarde atomique (write temp + rename)
///
/// Si le fichier est absent ou corrompu → état vide (comportement identique
/// à avant : les actions conditionnelles se déclenchent une fois au premier cycle).
/// </summary>
public class CommandStateCache
{
    // ── Chemin du fichier d'état ──────────────────────────────────────────────

    private readonly string _statePath;
    private readonly ILogger<CommandStateCache> _logger;

    // ── État en mémoire ───────────────────────────────────────────────────────

    private CacheData _data = new();

    public CommandStateCache(SolarConfig config, ILogger<CommandStateCache> logger)
    {
        _logger = logger;

        // Déduit le répertoire depuis le chemin des logs (/data/logs → /data/state)
        string logDir = Path.GetDirectoryName(config.Logging.FilePath ?? "/data/logs/solar-worker.log")
                          ?? "/data/logs";
        string dataDir = Path.Combine(Path.GetDirectoryName(logDir) ?? "/data", "state");
        _statePath = Path.Combine(dataDir, "command-state.json");

        Load();
    }

    // ── API publique ──────────────────────────────────────────────────────────

    /// <summary>Dernière valeur brute envoyée à HA pour cette batterie. null = jamais envoyé.</summary>
    public double? GetLastSentValue(int batteryId)
        => _data.LastSentValues.TryGetValue(batteryId, out double v) ? v : null;

    /// <summary>Dernier état de zone (true = était 0W). null = jamais envoyé.</summary>
    public bool? GetLastWasZero(int batteryId)
        => _data.LastWasZero.TryGetValue(batteryId, out bool v) ? v : null;

    /// <summary>
    /// Met à jour l'état d'une batterie et persiste immédiatement sur disque.
    /// Appelé uniquement après une commande HA réussie.
    /// </summary>
    public void Update(int batteryId, double sentValue, bool wasZero)
    {
        _data.LastSentValues[batteryId] = sentValue;
        _data.LastWasZero[batteryId] = wasZero;
        _data.LastUpdatedUtc = DateTime.UtcNow;
        Save();
    }

    // ── Persistence ───────────────────────────────────────────────────────────

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

            // Écriture atomique : temp file + rename pour éviter la corruption
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

    // ── Modèle JSON ───────────────────────────────────────────────────────────

    private class CacheData
    {
        public Dictionary<int, double> LastSentValues { get; set; } = new();
        public Dictionary<int, bool> LastWasZero { get; set; } = new();
        public DateTime LastUpdatedUtc { get; set; } = DateTime.UtcNow;
    }
}