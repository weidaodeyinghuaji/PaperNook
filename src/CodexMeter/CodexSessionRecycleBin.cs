using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace PaperTodo;

internal enum CodexSessionRestoreResult
{
    Restored,
    Conflict,
    NotFound,
    Invalid
}

internal sealed record CodexSessionRecycleEntry
{
    public int Schema { get; init; } = 1;
    public string Id { get; init; } = "";
    public string SessionId { get; init; } = "";
    public string OriginalPath { get; init; } = "";
    public string StoredFileName { get; init; } = "session.jsonl";
    public long Length { get; init; }
    public string Sha256 { get; init; } = "";
    public DateTimeOffset DeletedAt { get; init; }
    public string ManifestPath { get; init; } = "";
}

internal sealed class CodexSessionRecycleBin
{
    private const int Schema = 1;
    private readonly string _trashRoot;
    private readonly string _sessionsRoot;
    private readonly IDurableAtomicFileWriter _writer;

    internal CodexSessionRecycleBin(
        string durableRoot,
        string? sessionsRoot = null,
        IDurableAtomicFileWriter? writer = null)
    {
        _trashRoot = Path.GetFullPath(Path.Combine(durableRoot, "CodexMeter", "Trash"));
        _sessionsRoot = Path.GetFullPath(sessionsRoot ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex", "sessions"));
        _writer = writer ?? DurableAtomicFileWriter.Shared;
    }

    internal async Task<CodexSessionRecycleEntry> MoveToTrashAsync(
        CodexSessionSummary session,
        CancellationToken cancellationToken)
    {
        var source = Path.GetFullPath(session.FilePath);
        EnsureSessionPath(source);
        if (!File.Exists(source)) throw new FileNotFoundException("Codex session file no longer exists.", source);

        var id = $"{DateTimeOffset.UtcNow:yyyyMMddTHHmmssfff}-{Guid.NewGuid():N}";
        var directory = Path.Combine(_trashRoot, id);
        Directory.CreateDirectory(directory);
        var storedName = Path.GetFileName(source);
        var staged = Path.Combine(directory, storedName + ".tmp");
        var stored = Path.Combine(directory, storedName);
        var manifestPath = Path.Combine(directory, "manifest.json");
        try
        {
            await CopyFileAsync(source, staged, cancellationToken).ConfigureAwait(false);
            var sourceHash = await HashAsync(source, cancellationToken).ConfigureAwait(false);
            var stagedHash = await HashAsync(staged, cancellationToken).ConfigureAwait(false);
            var length = new FileInfo(source).Length;
            if (!string.Equals(sourceHash, stagedHash, StringComparison.Ordinal) ||
                new FileInfo(staged).Length != length)
            {
                throw new InvalidDataException("Codex session recycle copy verification failed.");
            }
            File.Move(staged, stored);
            var entry = new CodexSessionRecycleEntry
            {
                Schema = Schema,
                Id = id,
                SessionId = session.SessionId,
                OriginalPath = source,
                StoredFileName = storedName,
                Length = length,
                Sha256 = sourceHash,
                DeletedAt = DateTimeOffset.UtcNow,
                ManifestPath = manifestPath
            };
            _writer.Write(manifestPath, JsonSerializer.SerializeToUtf8Bytes(entry, CodexMeterJson.Options));
            File.Delete(source);
            return entry;
        }
        catch
        {
            TryDelete(staged);
            if (!File.Exists(stored) && !File.Exists(manifestPath)) TryDeleteDirectory(directory);
            throw;
        }
    }

    internal async Task<CodexSessionRestoreResult> RestoreAsync(
        string id,
        CancellationToken cancellationToken)
    {
        if (!SafeId(id)) return CodexSessionRestoreResult.Invalid;
        var directory = Path.GetFullPath(Path.Combine(_trashRoot, id));
        if (!IsChild(_trashRoot, directory)) return CodexSessionRestoreResult.Invalid;
        var entry = ReadEntry(Path.Combine(directory, "manifest.json"));
        if (entry == null || entry.Schema != Schema || !string.Equals(entry.Id, id, StringComparison.Ordinal))
            return CodexSessionRestoreResult.NotFound;

        var target = Path.GetFullPath(entry.OriginalPath);
        try { EnsureSessionPath(target); }
        catch { return CodexSessionRestoreResult.Invalid; }
        if (File.Exists(target)) return CodexSessionRestoreResult.Conflict;

        var stored = Path.Combine(directory, entry.StoredFileName);
        if (!File.Exists(stored) || new FileInfo(stored).Length != entry.Length ||
            !string.Equals(await HashAsync(stored, cancellationToken).ConfigureAwait(false), entry.Sha256, StringComparison.Ordinal))
        {
            return CodexSessionRestoreResult.Invalid;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        var staged = target + ".papernook-restore.tmp";
        try
        {
            await CopyFileAsync(stored, staged, cancellationToken).ConfigureAwait(false);
            if (!string.Equals(await HashAsync(staged, cancellationToken).ConfigureAwait(false), entry.Sha256, StringComparison.Ordinal))
                return CodexSessionRestoreResult.Invalid;
            File.Move(staged, target);
            TryDeleteDirectory(directory);
            return CodexSessionRestoreResult.Restored;
        }
        finally
        {
            TryDelete(staged);
        }
    }

    internal IReadOnlyList<CodexSessionRecycleEntry> List() =>
        !Directory.Exists(_trashRoot)
            ? []
            : Directory.EnumerateDirectories(_trashRoot)
                .Select(directory => ReadEntry(Path.Combine(directory, "manifest.json")))
                .Where(entry => entry != null)
                .Cast<CodexSessionRecycleEntry>()
                .OrderByDescending(entry => entry.DeletedAt)
                .ToArray();

    private void EnsureSessionPath(string path)
    {
        if (!IsChild(_sessionsRoot, path))
            throw new InvalidDataException("Codex session path is outside the configured sessions directory.");
    }

    private static bool IsChild(string parent, string child)
    {
        var prefix = Path.GetFullPath(parent).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return Path.GetFullPath(child).StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    private static bool SafeId(string value) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= 80 &&
        value.All(character => char.IsAsciiLetterOrDigit(character) || character == '-');

    private static CodexSessionRecycleEntry? ReadEntry(string path)
    {
        try
        {
            var entry = JsonSerializer.Deserialize<CodexSessionRecycleEntry>(File.ReadAllText(path), CodexMeterJson.Options);
            return entry == null ? null : entry with { ManifestPath = path };
        }
        catch { return null; }
    }

    private static async Task CopyFileAsync(string source, string target, CancellationToken cancellationToken)
    {
        await using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        await input.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
        await output.FlushAsync(cancellationToken).ConfigureAwait(false);
        output.Flush(flushToDisk: true);
    }

    private static async Task<string> HashAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false)).ToLowerInvariant();
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    private static void TryDeleteDirectory(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); } catch { }
    }
}
