using System.IO;
using System.Text.Json;

namespace PaperTodo;

internal sealed record PluginSafeModeStatus(
    bool Active,
    string Reason,
    string SuspectedPluginId,
    string SuspectedFingerprint,
    DateTimeOffset? EnteredUtc);

internal sealed class PluginPolicyStore
{
    private const int CurrentVersion = 1;
    private readonly string _path;
    private readonly IDurableAtomicFileWriter _writer;
    private PolicyDocument _document;
    private readonly bool _forceSafeMode;
    private readonly bool _tryPluginsOnce;
    private bool _configurationDamaged;

    private sealed class PolicyDocument
    {
        public int Version { get; set; } = CurrentVersion;
        public bool SafeModeActive { get; set; }
        public string SafeModeReason { get; set; } = "";
        public string SuspectedPluginId { get; set; } = "";
        public string SuspectedFingerprint { get; set; } = "";
        public DateTimeOffset? SafeModeEnteredUtc { get; set; }
        public string TryOncePluginId { get; set; } = "";
        public string TryOnceFingerprint { get; set; } = "";
        public Dictionary<string, PluginPolicyEntry> Plugins { get; set; } = new(StringComparer.Ordinal);
    }

    private sealed class PluginPolicyEntry
    {
        public bool UserEnabled { get; set; } = true;
        public string QuarantinedFingerprint { get; set; } = "";
        public int FailureCount { get; set; }
    }

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerOptions.Strict)
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    internal PluginPolicyStore(
        string dataDirectory,
        bool forceSafeMode = false,
        bool tryPluginsOnce = false,
        IDurableAtomicFileWriter? writer = null)
    {
        var root = Path.GetFullPath(dataDirectory);
        _path = Path.Combine(root, "plugins.policy.json");
        _writer = writer ?? DurableAtomicFileWriter.Shared;
        _forceSafeMode = forceSafeMode;
        _tryPluginsOnce = tryPluginsOnce;
        _document = Load(root);
    }

    internal PluginSafeModeStatus SafeMode => new(
        _forceSafeMode || _configurationDamaged || _document.SafeModeActive,
        _forceSafeMode ? "command-line" : _configurationDamaged ? "policy-damaged" : _document.SafeModeReason,
        _document.SuspectedPluginId,
        _document.SuspectedFingerprint,
        _document.SafeModeEnteredUtc);

    internal bool IsEnabled(string id, string fingerprint)
    {
        if (string.Equals(id, PaperBodyProviderIds.Markdown, StringComparison.Ordinal)) return true;
        if (_configurationDamaged) return false;
        var policy = GetOrCreate(id);
        if (!policy.UserEnabled) return false;
        var explicitlyTrying = _tryPluginsOnce &&
            string.Equals(_document.TryOncePluginId, id, StringComparison.Ordinal) &&
            string.Equals(_document.TryOnceFingerprint, fingerprint, StringComparison.Ordinal);
        if (SafeMode.Active && !explicitlyTrying) return false;
        return !string.Equals(policy.QuarantinedFingerprint, fingerprint, StringComparison.Ordinal) || explicitlyTrying;
    }

    internal bool IsUserEnabled(string id) =>
        string.Equals(id, PaperBodyProviderIds.Markdown, StringComparison.Ordinal) ||
        GetOrCreate(id).UserEnabled;

    internal bool IsQuarantined(string id, string fingerprint) =>
        !string.Equals(id, PaperBodyProviderIds.Markdown, StringComparison.Ordinal) &&
        string.Equals(GetOrCreate(id).QuarantinedFingerprint, fingerprint, StringComparison.Ordinal);

    internal void SetEnabled(string id, bool enabled)
    {
        if (string.Equals(id, PaperBodyProviderIds.Markdown, StringComparison.Ordinal)) return;
        GetOrCreate(id).UserEnabled = enabled;
        Save();
    }

    internal void EnterAutomaticSafeMode(PluginStartupAssessment assessment)
    {
        if (!assessment.ShouldEnterSafeMode) return;
        _document.SafeModeActive = true;
        _document.SafeModeReason = "repeated-plugin-startup-failure";
        _document.SuspectedPluginId = assessment.PluginId;
        _document.SuspectedFingerprint = assessment.Fingerprint;
        _document.SafeModeEnteredUtc = DateTimeOffset.UtcNow;
        if (!string.IsNullOrWhiteSpace(assessment.PluginId))
        {
            var entry = GetOrCreate(assessment.PluginId);
            entry.QuarantinedFingerprint = assessment.Fingerprint;
            entry.FailureCount = assessment.ConsecutivePluginFailures;
        }
        Save();
    }

    internal void ScheduleTryOnce(string id, string fingerprint)
    {
        _document.TryOncePluginId = id;
        _document.TryOnceFingerprint = fingerprint;
        Save();
    }

    internal void ObserveFingerprint(string id, string fingerprint)
    {
        if (!string.Equals(_document.SuspectedPluginId, id, StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(_document.SuspectedFingerprint) ||
            string.Equals(_document.SuspectedFingerprint, fingerprint, StringComparison.Ordinal))
        {
            return;
        }
        _document.SafeModeActive = false;
        _document.SafeModeReason = "";
        _document.SuspectedPluginId = "";
        _document.SuspectedFingerprint = "";
        _document.TryOncePluginId = "";
        _document.TryOnceFingerprint = "";
        Save();
    }

    internal void CompleteHealthyRun()
    {
        if (string.IsNullOrWhiteSpace(_document.TryOncePluginId)) return;
        var entry = GetOrCreate(_document.TryOncePluginId);
        if (string.Equals(entry.QuarantinedFingerprint, _document.TryOnceFingerprint, StringComparison.Ordinal))
        {
            entry.QuarantinedFingerprint = "";
            entry.FailureCount = 0;
        }
        _document.TryOncePluginId = "";
        _document.TryOnceFingerprint = "";
        _document.SafeModeActive = false;
        _document.SafeModeReason = "";
        Save();
    }

    private PolicyDocument Load(string root)
    {
        try
        {
            if (!File.Exists(_path)) return new PolicyDocument();
            var value = JsonSerializer.Deserialize<PolicyDocument>(File.ReadAllText(_path), JsonOptions);
            if (value is not { Version: CurrentVersion }) throw new InvalidDataException("Unsupported plugin policy version.");
            value.Plugins = new Dictionary<string, PluginPolicyEntry>(value.Plugins, StringComparer.Ordinal);
            return value;
        }
        catch
        {
            _configurationDamaged = true;
            try
            {
                var failed = Path.Combine(root, $"plugins.policy.failed_load.{DateTime.UtcNow:yyyyMMddHHmmssfff}.json");
                File.Copy(_path, failed, overwrite: false);
            }
            catch { }
            return new PolicyDocument
            {
                SafeModeActive = true,
                SafeModeReason = "policy-damaged",
                SafeModeEnteredUtc = DateTimeOffset.UtcNow
            };
        }
    }

    private PluginPolicyEntry GetOrCreate(string id)
    {
        if (!_document.Plugins.TryGetValue(id, out var entry))
        {
            entry = new PluginPolicyEntry();
            _document.Plugins[id] = entry;
        }
        return entry;
    }

    private void Save()
    {
        _configurationDamaged = false;
        _writer.Write(_path, JsonSerializer.SerializeToUtf8Bytes(_document, JsonOptions));
    }
}
