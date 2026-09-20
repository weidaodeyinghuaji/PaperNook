using System.IO;
using System.IO.Compression;
using System.Text.Json;

namespace PaperTodo;

internal sealed record RestoreInspection(
    bool IsCompatible,
    string Error,
    string BackupPath,
    DateTimeOffset? CreatedUtc,
    string AppVersion,
    int PaperCount,
    int ImageFileCount,
    int PluginFileCount,
    long TotalBytes);

internal sealed class BackupRestoreCoordinator
{
    private readonly AppStoragePaths _storage;
    private readonly IDurableAtomicFileWriter _writer;

    internal BackupRestoreCoordinator(
        AppStoragePaths storage,
        IDurableAtomicFileWriter? writer = null)
    {
        _storage = storage ?? throw new ArgumentNullException(nameof(storage));
        _writer = writer ?? DurableAtomicFileWriter.Shared;
    }

    internal RestoreInspection Inspect(string backupPath)
    {
        var verification = BackupService.VerifyPackage(backupPath);
        if (!verification.IsValid || verification.Manifest == null)
        {
            return new RestoreInspection(false, verification.Error, backupPath, null, "", 0, 0, 0, 0);
        }

        try
        {
            using var archive = ZipFile.OpenRead(backupPath);
            var stateEntry = archive.GetEntry("data/data.json")
                ?? throw new InvalidDataException("Backup does not contain core state.");
            int paperCount;
            using (var state = JsonDocument.Parse(stateEntry.Open()))
            {
                paperCount = state.RootElement.TryGetProperty("papers", out var papers) &&
                             papers.ValueKind == JsonValueKind.Array
                    ? papers.GetArrayLength()
                    : 0;
            }
            var manifest = verification.Manifest;
            return new RestoreInspection(
                true,
                "",
                backupPath,
                manifest.CreatedUtc,
                manifest.AppVersion,
                paperCount,
                manifest.Files.Count(file => file.Role == "images"),
                manifest.Files.Count(file => file.Role == "plugin-data"),
                manifest.Files.Sum(file => file.Length));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            return new RestoreInspection(false, ex.Message, backupPath, null, "", 0, 0, 0, 0);
        }
    }

    internal async Task ScheduleAsync(string backupPath, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var inspection = Inspect(backupPath);
        if (!inspection.IsCompatible)
        {
            throw new InvalidDataException($"Backup cannot be restored: {inspection.Error}");
        }
        if (PendingRestoreJournal.TryLoad(_storage.DataDirectory) != null)
        {
            throw new InvalidOperationException("Another restore is already pending.");
        }

        var dataParent = Path.GetDirectoryName(Path.GetFullPath(_storage.DataDirectory))!;
        var dataName = Path.GetFileName(_storage.DataDirectory);
        var nonce = Guid.NewGuid().ToString("N");
        var staging = Path.Combine(dataParent, $".{dataName}.restore-staging-{nonce}");
        var before = Path.Combine(dataParent, $"{dataName}.before-restore-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{nonce[..8]}");
        try
        {
            Directory.CreateDirectory(staging);
            await Task.Run(
                () => ExtractDataPayload(backupPath, staging, cancellationToken),
                cancellationToken).ConfigureAwait(false);
            ValidateStaging(staging);
            new PendingRestoreJournal(
                PendingRestoreJournal.CurrentVersion,
                nonce,
                Path.GetFullPath(backupPath),
                Path.GetFullPath(_storage.DataDirectory),
                Path.GetFullPath(staging),
                Path.GetFullPath(before),
                RestoreJournalStage.Staged,
                StartupAttempted: false,
                DateTimeOffset.UtcNow).Save(_writer);
        }
        catch
        {
            TryDeleteDirectory(staging);
            throw;
        }
    }

    internal static void ApplyPendingBeforeStoresOpen(
        AppStoragePaths storage,
        IDurableAtomicFileWriter? writer = null)
    {
        ArgumentNullException.ThrowIfNull(storage);
        writer ??= DurableAtomicFileWriter.Shared;
        var journal = PendingRestoreJournal.TryLoad(storage.DataDirectory);
        if (journal == null)
        {
            return;
        }
        ValidateJournalPaths(storage.DataDirectory, journal);

        if (journal.Stage == RestoreJournalStage.Staged)
        {
            var targetExists = Directory.Exists(journal.TargetDataDirectory);
            var stagingExists = Directory.Exists(journal.StagingDirectory);
            var beforeExists = Directory.Exists(journal.BeforeRestoreDirectory);
            if (stagingExists)
            {
                ValidateStaging(journal.StagingDirectory);
            }
            if (!beforeExists)
            {
                if (!targetExists || !stagingExists)
                    throw new InvalidDataException("Pending restore generations are incomplete.");
                Directory.Move(journal.TargetDataDirectory, journal.BeforeRestoreDirectory);
                targetExists = false;
                beforeExists = true;
            }
            if (!targetExists)
            {
                if (!stagingExists)
                    throw new InvalidDataException("Pending restore staging data is missing.");
                Directory.Move(journal.StagingDirectory, journal.TargetDataDirectory);
                targetExists = true;
                stagingExists = false;
            }
            if (!targetExists || !beforeExists || stagingExists)
                throw new InvalidDataException("Pending restore has conflicting generations.");
            journal = journal with
            {
                Stage = RestoreJournalStage.Switched,
                StartupAttempted = true
            };
            journal.Save(writer);
            return;
        }

        if (!journal.StartupAttempted)
        {
            (journal with { StartupAttempted = true }).Save(writer);
            return;
        }

        RollBackFailedRestore(journal, writer);
    }

    internal static void MarkHealthy(AppStoragePaths storage)
    {
        var journal = PendingRestoreJournal.TryLoad(storage.DataDirectory);
        if (journal is not { Stage: RestoreJournalStage.Switched })
        {
            return;
        }
        ValidateJournalPaths(storage.DataDirectory, journal);
        File.Delete(PendingRestoreJournal.PathFor(storage.DataDirectory));
    }

    private static void ExtractDataPayload(
        string backupPath,
        string staging,
        CancellationToken cancellationToken)
    {
        using var archive = ZipFile.OpenRead(backupPath);
        foreach (var entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string? relative = entry.FullName switch
            {
                "data/data.json" => "data.json",
                "data/note-assets.lmdb" => "note-assets.lmdb",
                var name when name.StartsWith("plugins/data/", StringComparison.Ordinal) => name,
                _ => null
            };
            if (relative == null)
            {
                continue;
            }
            var destination = SafeDestination(staging, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            using var input = entry.Open();
            using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            CopyWithCancellation(input, output, cancellationToken);
            output.Flush(flushToDisk: true);
        }
    }

    private static void ValidateStaging(string staging)
    {
        var state = new StateStore(staging, DurableAtomicFileWriter.Shared).Load();
        _ = state.Papers.Count;
        var imagePath = Path.Combine(staging, "note-assets.lmdb");
        if (!File.Exists(imagePath))
        {
            throw new InvalidDataException("Backup image snapshot is missing.");
        }
        using var images = new NoteImageStore(imagePath);
        images.Load();
    }

    private static void RollBackFailedRestore(
        PendingRestoreJournal journal,
        IDurableAtomicFileWriter writer)
    {
        if (!Directory.Exists(journal.BeforeRestoreDirectory))
        {
            throw new InvalidDataException("Restore failed and its rollback directory is missing.");
        }
        var failed = journal.TargetDataDirectory + $".failed-restore-{DateTime.UtcNow:yyyyMMdd-HHmmss}";
        if (Directory.Exists(journal.TargetDataDirectory))
        {
            Directory.Move(journal.TargetDataDirectory, failed);
        }
        Directory.Move(journal.BeforeRestoreDirectory, journal.TargetDataDirectory);
        File.Delete(PendingRestoreJournal.PathFor(journal.TargetDataDirectory));
    }

    private static void ValidateJournalPaths(string activeDataDirectory, PendingRestoreJournal journal)
    {
        var active = Path.GetFullPath(activeDataDirectory);
        if (!SamePath(active, journal.TargetDataDirectory))
        {
            throw new InvalidDataException("Pending restore targets a different data directory.");
        }
        var parent = Path.GetDirectoryName(active)!;
        foreach (var path in new[] { journal.StagingDirectory, journal.BeforeRestoreDirectory })
        {
            if (!SamePath(parent, Path.GetDirectoryName(Path.GetFullPath(path))!))
            {
                throw new InvalidDataException("Pending restore is not on the same volume and directory level.");
            }
        }
    }

    private static string SafeDestination(string root, string relative)
    {
        var destination = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
        var normalizedRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!destination.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Backup entry escapes the restore staging directory.");
        }
        return destination;
    }

    private static bool SamePath(string left, string right) =>
        string.Equals(Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar),
            Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar),
            StringComparison.OrdinalIgnoreCase);

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

    private static void TryDeleteDirectory(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); } catch { }
    }
}
