using Serilog.Events;
using Serilog.Formatting;
using System.Text.Json;

namespace SolarDistribution.Worker.Logging;

/// <summary>
/// Loki formatter — serializes each LogEvent as a valid one-line JSON record.
///
/// Why not use {Message:j} in an outputTemplate?
///   → Serilog does not escape quotes/special characters in {Message:j}
///     when message is already a rendered string (not an object).
///     Result: broken JSON as soon as a message contains ":", ",", "/"...
///
/// Solution: serialize each field with System.Text.Json, which guarantees
/// valid JSON regardless of values.
///
/// Produced format (one line per log):
/// {
///   "timestamp": "2025-06-15T08:32:11.453+02:00",
///   "level":     "INF",
///   "message":   "Cycle #42 — surplus 712 W",
///   "source":    "SolarDistribution.Worker.Services.SolarWorker",
///   "exception": "System.Exception: ...",   // empty if no exception
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
        // Keep non-ASCII characters readable in Grafana (emoji, accents...)
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public void Format(LogEvent logEvent, TextWriter output)
    {
        // Extra properties (excluding SourceContext and Exception — already mapped to dedicated fields)
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

    /// <summary>Converts ScalarValue/SequenceValue/StructureValue to primitive .NET types.</summary>
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