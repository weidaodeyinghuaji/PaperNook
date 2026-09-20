using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PaperTodo;

internal enum PluginStartupStage
{
    Starting,
    LoadingState,
    DiscoveringPlugins,
    ActivatingPlugin,
    Running,
    CleanExit
}

internal sealed record PluginStartupAssessment(
    bool ShouldEnterSafeMode,
    string PluginId,
    string Fingerprint,
    int ConsecutivePluginFailures,
    DateTimeOffset? LastFailureUtc);

internal sealed class PluginStartupHealthStore
{
    private const int CurrentVersion = 1;
    private readonly string _path;
    private readonly IDurableAtomicFileWriter _writer;
    private HealthDocument _document = new();
    private bool _healthy;

    private sealed class HealthDocument
    {
        public int Version { get; set; } = CurrentVersion;
        public string RunId { get; set; } = "";
        public PluginStartupStage Stage { get; set; } = PluginStartupStage.CleanExit;
        public string PluginId { get; set; } = "";
        public string Fingerprint { get; set; } = "";
        public int ConsecutivePluginFailures { get; set; }
        public DateTimeOffset StartedUtc { get; set; }
        public DateTimeOffset UpdatedUtc { get; set; }
    }

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerOptions.Strict)
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    internal PluginStartupHealthStore(
        string dataDirectory,
        IDurableAtomicFileWriter? writer = null)
    {
        _path = Path.Combine(Path.GetFullPath(dataDirectory), "startup-health.json");
        _writer = writer ?? DurableAtomicFileWriter.Shared;
    }

    internal PluginStartupAssessment BeginRun()
    {
        _healthy = false;
        var previous = TryLoad();
        var pluginFailure = previous != null &&
            previous.Stage is PluginStartupStage.DiscoveringPlugins or PluginStartupStage.ActivatingPlugin;
        var streak = pluginFailure ? previous!.ConsecutivePluginFailures + 1 : 0;
        var assessment = new PluginStartupAssessment(
            streak >= 2,
            pluginFailure ? previous!.PluginId : "",
            pluginFailure ? previous!.Fingerprint : "",
            streak,
            pluginFailure ? previous!.UpdatedUtc : null);
        var now = DateTimeOffset.UtcNow;
        _document = new HealthDocument
        {
            RunId = Guid.NewGuid().ToString("N"),
            Stage = PluginStartupStage.Starting,
            ConsecutivePluginFailures = streak,
            StartedUtc = now,
            UpdatedUtc = now
        };
        Save();
        return assessment;
    }

    internal void EnterStage(
        PluginStartupStage stage,
        string pluginId = "",
        string fingerprint = "")
    {
        if (_healthy && stage is PluginStartupStage.DiscoveringPlugins or PluginStartupStage.ActivatingPlugin)
        {
            return;
        }
        _document.Stage = stage;
        _document.PluginId = pluginId ?? "";
        _document.Fingerprint = fingerprint ?? "";
        _document.UpdatedUtc = DateTimeOffset.UtcNow;
        Save();
    }

    internal void MarkHealthy()
    {
        EnterStage(PluginStartupStage.Running);
        _healthy = true;
    }
    internal void MarkCleanExit() => EnterStage(PluginStartupStage.CleanExit);

    private HealthDocument? TryLoad()
    {
        try
        {
            if (!File.Exists(_path)) return null;
            var value = JsonSerializer.Deserialize<HealthDocument>(File.ReadAllText(_path), JsonOptions);
            return value is { Version: CurrentVersion } ? value : null;
        }
        catch
        {
            return null;
        }
    }

    private void Save() =>
        _writer.Write(_path, JsonSerializer.SerializeToUtf8Bytes(_document, JsonOptions));
}
