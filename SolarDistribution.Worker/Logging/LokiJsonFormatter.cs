using Serilog.Events;
using Serilog.Formatting;
using System.Text.Json;

namespace SolarDistribution.Worker.Logging;

/// <summary>
/// Formatter Loki — sérialise chaque LogEvent en une ligne JSON valide.
///
/// Pourquoi pas {Message:j} dans un outputTemplate ?
///   → Serilog n'échappe pas les guillemets/caractères spéciaux dans {Message:j}
///     quand le message est déjà une string rendue (pas un objet).
///     Résultat : JSON cassé dès qu'un message contient ":", ",", "/"...
///
/// Solution : on sérialise chaque champ avec System.Text.Json qui garantit
/// un JSON valide quelles que soient les valeurs.
///
/// Format produit (une ligne par log) :
/// {
///   "timestamp": "2025-06-15T08:32:11.453+02:00",
///   "level":     "INF",
///   "message":   "Cycle #42 — surplus 712 W",
///   "source":    "SolarDistribution.Worker.Services.SolarWorker",
///   "exception": "System.Exception: ...",   // vide si pas d'exception
///   "properties": { "Cycle": 42, "SurplusW": 712 }
/// }
///
/// LogQL Grafana :
///   {job="solar-worker"} | json | level="ERR"
///   {job="solar-worker"} | json | source=~"SolarWorker"
/// </summary>
public sealed class LokiJsonFormatter : ITextFormatter
{
    private static readonly JsonSerializerOptions _opts = new()
    {
        // Garde les caractères non-ASCII lisibles dans Grafana (émojis, accents...)
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public void Format(LogEvent logEvent, TextWriter output)
    {
        // Propriétés supplémentaires (hors SourceContext et Exception — déjà en champs dédiés)
        var props = new Dictionary<string, object?>();
        foreach (var (key, value) in logEvent.Properties)
        {
            if (key == "SourceContext") continue;
            props[key] = SimplifyValue(value);
        }

        var entry = new
        {
            timestamp = logEvent.Timestamp.ToString("yyyy-MM-ddTHH:mm:ss.fffzzz"),
            level = logEvent.Level.ToString()[..3].ToUpperInvariant(), // "INF", "WRN", "ERR"…
            message = logEvent.RenderMessage(),
            source = logEvent.Properties.TryGetValue("SourceContext", out var sc)
                            ? sc.ToString().Trim('"')
                            : string.Empty,
            exception = logEvent.Exception?.ToString() ?? string.Empty,
            properties = props.Count > 0 ? props : null,
        };

        output.WriteLine(JsonSerializer.Serialize(entry, _opts));
    }

    /// <summary>Convertit un ScalarValue/SequenceValue/StructureValue en type primitif .NET.</summary>
    private static object? SimplifyValue(LogEventPropertyValue value) => value switch
    {
        ScalarValue sv => sv.Value,
        SequenceValue seq => seq.Elements.Select(SimplifyValue).ToList(),
        StructureValue str => str.Properties.ToDictionary(p => p.Name, p => SimplifyValue(p.Value)),
        DictionaryValue dict => dict.Elements.ToDictionary(
                                              kv => kv.Key.Value?.ToString() ?? "",
                                              kv => SimplifyValue(kv.Value)),
        _ => value.ToString(),
    };
}