using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PaperTodo;

internal sealed class PendingUpdateApply
{
    public string Version { get; set; } = "";
    public string SourcePath { get; set; } = "";
    public string TargetPath { get; set; } = "";
    public long Length { get; set; }
    public string Sha256 { get; set; } = "";
    public string Nonce { get; set; } = "";
    public bool NonceConsumed { get; set; }
    public DateTimeOffset RegisteredUtc { get; set; }
    public DateTimeOffset? AuthorizedUtc { get; set; }
    public DateTimeOffset? LaunchStartedUtc { get; set; }
    public string Stage { get; set; } = "registered";
    public string PreviousPath { get; set; } = "";
    public long PreviousLength { get; set; }
    public string PreviousSha256 { get; set; } = "";
    public string FailedPath { get; set; } = "";
    public DateTimeOffset? HealthyUtc { get; set; }
}

internal sealed class UpdateStateSnapshot
{
    public int Schema { get; set; } = 1;
    public string ETag { get; set; } = "";
    public DateTimeOffset? LastCheckUtc { get; set; }
    public string IgnoredVersion { get; set; } = "";
    public PendingUpdateApply? PendingApply { get; set; }
}

internal sealed class UpdateStateStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerOptions.Strict)
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };
    private readonly IDurableAtomicFileWriter _writer;
    private readonly object _gate = new();

    internal UpdateStateStore(string cacheDirectory, IDurableAtomicFileWriter writer)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cacheDirectory);
        _writer = writer ?? throw new ArgumentNullException(nameof(writer));
        FilePath = Path.Combine(Path.GetFullPath(cacheDirectory), "Updates", "update-state.json");
    }

    internal string FilePath { get; }

    internal UpdateStateSnapshot Load()
    {
        lock (_gate)
        {
            if (!File.Exists(FilePath)) return new UpdateStateSnapshot();
            try
            {
                var state = JsonSerializer.Deserialize<UpdateStateSnapshot>(File.ReadAllBytes(FilePath), JsonOptions);
                if (state is not { Schema: 1 }) throw new InvalidDataException("Update state schema is invalid.");
                return state;
            }
            catch (Exception ex) when (ex is JsonException or IOException or InvalidDataException)
            {
                return new UpdateStateSnapshot();
            }
        }
    }

    internal void RecordCheck(string? etag, DateTimeOffset checkedUtc)
    {
        lock (_gate)
        {
            var state = Load();
            state.ETag = etag?.Trim() ?? "";
            state.LastCheckUtc = checkedUtc.ToUniversalTime();
            Save(state);
        }
    }

    internal void IgnoreVersion(PaperTodoVersion version)
    {
        lock (_gate)
        {
            var state = Load();
            state.IgnoredVersion = version.ToString();
            Save(state);
        }
    }

    internal void RegisterPendingApply(
        ReleaseAsset asset,
        string sourcePath,
        string targetPath,
        string nonce)
    {
        ArgumentNullException.ThrowIfNull(asset);
        ArgumentException.ThrowIfNullOrWhiteSpace(nonce);
        sourcePath = Path.GetFullPath(sourcePath);
        targetPath = Path.GetFullPath(targetPath);
        if (!File.Exists(sourcePath)) throw new FileNotFoundException("Verified update is missing.", sourcePath);
        if (new FileInfo(sourcePath).Length != asset.Length) throw new InvalidDataException("Verified update length changed.");
        var actualHash = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(sourcePath)));
        if (!string.Equals(actualHash, asset.Sha256, StringComparison.Ordinal))
            throw new InvalidDataException("Verified update hash changed.");

        lock (_gate)
        {
            var state = Load();
            state.PendingApply = new PendingUpdateApply
            {
                Version = asset.Version.ToString(),
                SourcePath = sourcePath,
                TargetPath = targetPath,
                Length = asset.Length,
                Sha256 = asset.Sha256,
                Nonce = nonce,
                RegisteredUtc = DateTimeOffset.UtcNow
            };
            Save(state);
        }
    }

    internal bool TryAuthorizeApply(string nonce, string targetPath, out PendingUpdateApply pending)
    {
        lock (_gate)
        {
            var state = Load();
            var candidate = state.PendingApply;
            if (candidate == null || candidate.NonceConsumed ||
                !FixedEquals(candidate.Nonce, nonce) ||
                !SamePath(candidate.TargetPath, targetPath))
            {
                pending = new PendingUpdateApply();
                return false;
            }

            candidate.NonceConsumed = true;
            candidate.AuthorizedUtc = DateTimeOffset.UtcNow;
            Save(state);
            pending = candidate;
            return true;
        }
    }

    internal void UpdatePending(Action<PendingUpdateApply> update)
    {
        lock (_gate)
        {
            var state = Load();
            if (state.PendingApply == null) return;
            update(state.PendingApply);
            Save(state);
        }
    }

    internal bool TryMarkLaunch(
        string nonce,
        string targetPath,
        PaperTodoVersion version,
        bool healthy)
    {
        lock (_gate)
        {
            var state = Load();
            var pending = state.PendingApply;
            if (pending == null || !pending.NonceConsumed ||
                !FixedEquals(pending.Nonce, nonce) ||
                !SamePath(pending.TargetPath, targetPath) ||
                !string.Equals(pending.Version, version.ToString(), StringComparison.Ordinal))
                return false;

            pending.LaunchStartedUtc ??= DateTimeOffset.UtcNow;
            if (healthy) pending.HealthyUtc = DateTimeOffset.UtcNow;
            Save(state);
            return true;
        }
    }

    internal void ClearPending()
    {
        lock (_gate)
        {
            var state = Load();
            state.PendingApply = null;
            Save(state);
        }
    }

    private void Save(UpdateStateSnapshot state)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(state, JsonOptions);
        _writer.Write(FilePath, bytes, path =>
        {
            try { return JsonSerializer.Deserialize<UpdateStateSnapshot>(File.ReadAllBytes(path), JsonOptions)?.Schema == 1; }
            catch { return false; }
        });
    }

    private static bool FixedEquals(string left, string right)
    {
        var leftBytes = Encoding.UTF8.GetBytes(left ?? "");
        var rightBytes = Encoding.UTF8.GetBytes(right ?? "");
        return leftBytes.Length == rightBytes.Length && CryptographicOperations.FixedTimeEquals(leftBytes, rightBytes);
    }

    private static bool SamePath(string left, string right) =>
        string.Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)),
            StringComparison.OrdinalIgnoreCase);
}
