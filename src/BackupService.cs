using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;

namespace PaperTodo;

internal sealed record BackupRequest(
    BackupKind Kind,
    bool IncludeExportedNotes,
    int AutomaticRetentionCount = 10);

internal sealed record BackupProgress(string Stage, int CompletedFiles, int TotalFiles);

internal sealed record BackupResult(
    string BackupPath,
    BackupManifest Manifest,
    long Length);

internal sealed record BackupVerification(
    bool IsValid,
    BackupManifest? Manifest,
    string Error);

internal sealed class BackupService
{
    internal const string BackupExtension = ".papernook-backup";
    internal const string LegacyBackupExtension = ".papertodo-backup";
    private const long MaxManifestBytes = 2 * 1024 * 1024;
    private const long MaxFileBytes = 256L * 1024 * 1024;
    private const long MaxTotalBytes = 1024L * 1024 * 1024;
    private readonly AppStoragePaths _storage;
    private readonly StateStore _stateStore;
    private readonly NoteImageStore _imageStore;
    private readonly Action _saveCoreState;
    private readonly Action _flushPluginData;

    internal BackupService(
        AppStoragePaths storage,
        StateStore stateStore,
        NoteImageStore imageStore,
        Action saveCoreState,
        Action flushPluginData)
    {
        _storage = storage ?? throw new ArgumentNullException(nameof(storage));
        _stateStore = stateStore ?? throw new ArgumentNullException(nameof(stateStore));
        _imageStore = imageStore ?? throw new ArgumentNullException(nameof(imageStore));
        _saveCoreState = saveCoreState ?? throw new ArgumentNullException(nameof(saveCoreState));
        _flushPluginData = flushPluginData ?? throw new ArgumentNullException(nameof(flushPluginData));
    }

    internal async Task<BackupResult> CreateAsync(
        BackupRequest request,
        IProgress<BackupProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        Directory.CreateDirectory(_storage.BackupDirectory);

        var operationId = Guid.NewGuid().ToString("N");
        var staging = Path.Combine(_storage.BackupDirectory, $".papernook-backup-stage-{operationId}");
        var finalPath = NextBackupPath(request.Kind);
        var temporaryPackage = finalPath + $".tmp-{operationId}";
        try
        {
            Directory.CreateDirectory(staging);
            progress?.Report(new BackupProgress("saving", 0, 0));
            _saveCoreState();
            cancellationToken.ThrowIfCancellationRequested();

            var payloads = await Task.Run(() =>
            {
                var prepared = new List<StagedPayload>();
                var statePath = Path.Combine(staging, "data.json");
                _stateStore.CreateValidatedSnapshot(statePath);
                prepared.Add(new StagedPayload("data/data.json", statePath, "state"));

                cancellationToken.ThrowIfCancellationRequested();
                var imagePath = Path.Combine(staging, "note-assets.lmdb");
                _imageStore.CreateSnapshot(imagePath);
                prepared.Add(new StagedPayload("data/note-assets.lmdb", imagePath, "images"));

                _flushPluginData();
                AddDirectoryPayloads(
                    prepared,
                    _storage.PluginDataDirectory,
                    "plugins/data",
                    "plugin-data");
                if (request.IncludeExportedNotes)
                    AddDirectoryPayloads(prepared, _storage.NotesDirectory, "notes", "exported-note");
                return prepared;
            }, cancellationToken).ConfigureAwait(false);

            payloads.Sort(static (left, right) => StringComparer.Ordinal.Compare(left.EntryPath, right.EntryPath));
            var manifestFiles = new List<BackupManifestFile>(payloads.Count);
            long totalBytes = 0;
            for (var index = 0; index < payloads.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var payload = payloads[index];
                var info = new FileInfo(payload.SourcePath);
                if (info.Length > MaxFileBytes || checked(totalBytes + info.Length) > MaxTotalBytes)
                {
                    throw new InvalidDataException("Backup input exceeds the supported size limit.");
                }
                totalBytes += info.Length;
                manifestFiles.Add(new BackupManifestFile(
                    payload.EntryPath,
                    info.Length,
                    HashFile(payload.SourcePath),
                    payload.Role));
                progress?.Report(new BackupProgress("hashing", index + 1, payloads.Count));
            }

            var manifest = new BackupManifest(
                BackupManifest.CurrentFormatVersion,
                CurrentVersion(),
                DateTimeOffset.UtcNow,
                _storage.IsPortable,
                request.Kind,
                manifestFiles);
            var manifestBytes = manifest.Serialize();
            if (manifestBytes.Length > MaxManifestBytes)
            {
                throw new InvalidDataException("Backup manifest is too large.");
            }

            await Task.Run(
                () => WritePackage(temporaryPackage, payloads, manifestBytes, progress, cancellationToken),
                cancellationToken).ConfigureAwait(false);
            var verification = VerifyPackage(temporaryPackage);
            if (!verification.IsValid)
            {
                throw new InvalidDataException($"Backup verification failed: {verification.Error}");
            }

            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporaryPackage, finalPath);
            if (request.Kind == BackupKind.Automatic)
            {
                ApplyAutomaticRetention(Math.Clamp(request.AutomaticRetentionCount, 3, 30));
            }
            return new BackupResult(finalPath, manifest, new FileInfo(finalPath).Length);
        }
        finally
        {
            TryDeleteFile(temporaryPackage);
            TryDeleteDirectory(staging);
        }
    }

    internal static BackupVerification VerifyPackage(string path)
    {
        try
        {
            using var archive = ZipFile.OpenRead(path);
            var entries = new Dictionary<string, ZipArchiveEntry>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in archive.Entries)
            {
                var normalized = ValidateEntryPath(entry.FullName);
                if (!entries.TryAdd(normalized, entry))
                {
                    throw new InvalidDataException($"Duplicate backup entry: {normalized}.");
                }
                if (entry.Length > 1024 * 1024 &&
                    (entry.CompressedLength == 0 || entry.Length / (double)entry.CompressedLength > 100))
                {
                    throw new InvalidDataException($"Suspicious compression ratio for backup entry: {normalized}.");
                }
            }

            if (!entries.TryGetValue("manifest.json", out var manifestEntry) ||
                manifestEntry.Length <= 0 || manifestEntry.Length > MaxManifestBytes)
            {
                throw new InvalidDataException("Backup manifest is missing or invalid.");
            }
            BackupManifest manifest;
            using (var stream = manifestEntry.Open())
            {
                manifest = BackupManifest.Deserialize(stream);
            }

            var listed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            long total = 0;
            foreach (var file in manifest.Files)
            {
                var normalized = ValidateEntryPath(file.Path);
                if (normalized == "manifest.json" || !listed.Add(normalized))
                {
                    throw new InvalidDataException($"Invalid manifest entry: {normalized}.");
                }
                if (!entries.TryGetValue(normalized, out var entry))
                {
                    throw new InvalidDataException($"Missing backup entry: {normalized}.");
                }
                if (file.Length < 0 || file.Length > MaxFileBytes || entry.Length != file.Length)
                {
                    throw new InvalidDataException($"Invalid length for backup entry: {normalized}.");
                }
                total = checked(total + entry.Length);
                if (total > MaxTotalBytes)
                {
                    throw new InvalidDataException("Backup expands beyond the supported size limit.");
                }
                using var content = entry.Open();
                var actualHash = Convert.ToHexStringLower(SHA256.HashData(content));
                if (!string.Equals(actualHash, file.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException($"Hash mismatch for backup entry: {normalized}.");
                }
            }

            var extras = entries.Keys.Where(name => name != "manifest.json" && !listed.Contains(name)).ToArray();
            if (extras.Length > 0)
            {
                throw new InvalidDataException($"Backup contains unlisted entries: {string.Join(", ", extras)}.");
            }
            return new BackupVerification(true, manifest, "");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            return new BackupVerification(false, null, ex.Message);
        }
    }

    internal static BackupManifest? TryReadManifest(string path)
    {
        try
        {
            using var archive = ZipFile.OpenRead(path);
            var entries = archive.Entries
                .Where(entry => string.Equals(entry.FullName, "manifest.json", StringComparison.OrdinalIgnoreCase))
                .ToArray();
            if (entries.Length != 1 || entries[0].Length is <= 0 or > MaxManifestBytes) return null;
            using var stream = entries[0].Open();
            return BackupManifest.Deserialize(stream);
        }
        catch
        {
            return null;
        }
    }

    private void ApplyAutomaticRetention(int keep)
    {
        var automatic = EnumerateBackupFiles(_storage.BackupDirectory)
            .Select(path => (Path: path, Verification: VerifyPackage(path)))
            .Where(item => item.Verification is { IsValid: true, Manifest.Kind: BackupKind.Automatic })
            .OrderByDescending(item => item.Verification.Manifest!.CreatedUtc)
            .Skip(keep)
            .ToArray();
        foreach (var item in automatic)
        {
            TryDeleteFile(item.Path);
        }
    }

    private string NextBackupPath(BackupKind kind)
    {
        var label = kind == BackupKind.Automatic ? "auto" : "manual";
        var stem = $"PaperNook-{label}-{DateTime.UtcNow:yyyyMMdd-HHmmss}";
        var path = Path.Combine(_storage.BackupDirectory, stem + BackupExtension);
        for (var suffix = 2; File.Exists(path); suffix++)
        {
            path = Path.Combine(_storage.BackupDirectory, $"{stem}-{suffix}{BackupExtension}");
        }
        return path;
    }

    internal static IEnumerable<string> EnumerateBackupFiles(string directory) =>
        Directory.EnumerateFiles(directory, $"*{BackupExtension}")
            .Concat(Directory.EnumerateFiles(directory, $"*{LegacyBackupExtension}"))
            .Distinct(StringComparer.OrdinalIgnoreCase);

    private static void WritePackage(
        string path,
        IReadOnlyList<StagedPayload> payloads,
        byte[] manifestBytes,
        IProgress<BackupProgress>? progress,
        CancellationToken cancellationToken)
    {
        using var output = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
        using var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true);
        var items = payloads
            .Select(payload => new PackageItem(payload.EntryPath, payload.SourcePath, null))
            .Append(new PackageItem("manifest.json", null, manifestBytes))
            .OrderBy(item => item.EntryPath, StringComparer.Ordinal)
            .ToArray();
        for (var index = 0; index < items.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var item = items[index];
            var entry = archive.CreateEntry(item.EntryPath, CompressionLevel.Optimal);
            entry.LastWriteTime = new DateTimeOffset(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);
            using var destination = entry.Open();
            if (item.Bytes != null)
            {
                destination.Write(item.Bytes);
            }
            else
            {
                using var source = File.OpenRead(item.SourcePath!);
                CopyWithCancellation(source, destination, cancellationToken);
            }
            progress?.Report(new BackupProgress("packing", index + 1, items.Length));
        }
        archive.Dispose();
        output.Flush(flushToDisk: true);
    }

    private static void AddDirectoryPayloads(
        ICollection<StagedPayload> payloads,
        string sourceRoot,
        string entryRoot,
        string role)
    {
        if (!Directory.Exists(sourceRoot))
        {
            return;
        }
        foreach (var source in Directory.EnumerateFiles(sourceRoot, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(sourceRoot, source).Replace('\\', '/');
            payloads.Add(new StagedPayload($"{entryRoot}/{relative}", source, role));
        }
    }

    private static string ValidateEntryPath(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Contains('\\') || value.StartsWith('/') ||
            Path.IsPathFullyQualified(value) || value.Split('/').Any(segment => segment is "" or "." or ".."))
        {
            throw new InvalidDataException($"Unsafe backup entry path: {value}.");
        }
        return value;
    }

    private static string HashFile(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    private static void CopyWithCancellation(
        Stream source,
        Stream destination,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[128 * 1024];
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var read = source.Read(buffer, 0, buffer.Length);
            if (read == 0) return;
            destination.Write(buffer, 0, read);
        }
    }

    private static string CurrentVersion() =>
        typeof(BackupService).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? typeof(BackupService).Assembly.GetName().Version?.ToString()
        ?? "0.0.0";

    private static void TryDeleteFile(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    private static void TryDeleteDirectory(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); } catch { }
    }

    private sealed record StagedPayload(string EntryPath, string SourcePath, string Role);
    private sealed record PackageItem(string EntryPath, string? SourcePath, byte[]? Bytes);
}
