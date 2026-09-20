using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.IO.Compression;
using System.Text.Json;
using PaperTodo;

var checks = new (string Name, Action Run)[]
{
    ("primary-save-faults-keep-a-loadable-generation", PrimarySaveFaultsKeepALoadableGeneration),
    ("backup-refresh-fault-keeps-old-backup", BackupRefreshFaultKeepsOldBackup),
    ("corrupt-primary-never-refreshes-backup", CorruptPrimaryNeverRefreshesBackup),
    ("older-save-version-cannot-overwrite-newer", OlderSaveVersionCannotOverwriteNewer),
    ("backup-recovery-is-preserved-until-normal-save", BackupRecoveryIsPreservedUntilNormalSave),
    ("plugin-system-shutdown-skips-final-flush", PluginSystemShutdownSkipsFinalFlush),
    ("plugin-normal-dispose-still-final-flushes", PluginNormalDisposeStillFinalFlushes),
    ("shutdown-skips-deferred-plugin-cleanup", ShutdownSkipsDeferredPluginCleanup),
    ("temp-validator-failure-keeps-old-target", TempValidatorFailureKeepsOldTarget),
    ("flush-failure-keeps-old-target", FlushFailureKeepsOldTarget),
    ("replace-retries-transient-sharing-failures", ReplaceRetriesTransientSharingFailures),
    ("markdown-modes-migrate-and-roundtrip", MarkdownModesMigrateAndRoundTrip),
    ("downward-preview-default-and-roundtrip", DownwardPreviewDefaultAndRoundTrip),
    ("storage-paths-preserve-legacy-data", StoragePathsPreserveLegacyData),
    ("storage-paths-import-papertodo-profile", StoragePathsImportPaperTodoProfile),
    ("storage-paths-migrate-data-before-startup", StoragePathsMigrateDataBeforeStartup),
    ("storage-paths-copy-backups-before-switch", StoragePathsCopyBackupsBeforeSwitch),
    ("storage-paths-reject-overlap", StoragePathsRejectOverlap),
    ("storage-cache-clear-does-not-touch-data", StorageCacheClearDoesNotTouchData),
    ("storage-config-recovers-without-overwriting-evidence", StorageConfigRecoversWithoutOverwritingEvidence),
    ("storage-config-rejects-valid-json-with-invalid-paths", StorageConfigRejectsValidJsonWithInvalidPaths),
    ("storage-v1-upgrades-with-backup-directory", StorageV1UpgradesWithBackupDirectory),
    ("storage-portable-config-moves-with-app", StoragePortableConfigMovesWithApp),
    ("lmdb-online-snapshot-is-consistent", LmdbOnlineSnapshotIsConsistent),
    ("backup-package-is-verified-and-excludes-cache", BackupPackageIsVerifiedAndExcludesCache),
    ("backup-tampering-is-rejected", BackupTamperingIsRejected),
    ("backup-cancellation-leaves-no-visible-package", BackupCancellationLeavesNoVisiblePackage),
    ("restore-rejects-path-traversal", RestoreRejectsPathTraversal),
    ("restore-switches-and-keeps-rollback-copy", RestoreSwitchesAndKeepsRollbackCopy),
    ("restore-resumes-after-old-generation-move", RestoreResumesAfterOldGenerationMove),
    ("plugin-health-only-triggers-on-consecutive-startup-failures", PluginHealthOnlyTriggersOnConsecutiveStartupFailures),
    ("plugin-policy-keeps-builtin-and-resets-updated-fingerprint", PluginPolicyKeepsBuiltinAndResetsUpdatedFingerprint)
};

var failed = 0;
foreach (var check in checks)
{
    try
    {
        check.Run();
        Console.WriteLine($"PASS {check.Name}");
    }
    catch (Exception ex)
    {
        failed++;
        Console.Error.WriteLine($"FAIL {check.Name}: {ex}");
    }
}

if (failed != 0)
{
    Console.Error.WriteLine($"Persistence checks failed: {failed}/{checks.Length}");
    return 1;
}

Console.WriteLine($"Persistence checks passed: {checks.Length}/{checks.Length}");
return 0;

static void PrimarySaveFaultsKeepALoadableGeneration()
{
    var stages = new[]
    {
        DurableAtomicWriteStage.BeforeTempOpen,
        DurableAtomicWriteStage.AfterTempWrite,
        DurableAtomicWriteStage.AfterFlush,
        DurableAtomicWriteStage.BeforeReplace
    };

    foreach (var stage in stages)
    {
        using var scope = new TempDirectory();
        var seed = NewStore(scope.Path, DurableAtomicFileWriter.Shared);
        seed.SaveJsonSync(seed.SerializeState(NewState("light")), version: 1);
        Assert(seed.TryRefreshBackupFromPrimary(), $"seed backup failed for {stage}");

        var failingWriter = new DurableAtomicFileWriter((current, _) =>
        {
            if (current == stage)
            {
                throw new IOException($"Injected failure at {stage}");
            }
        });
        var store = NewStore(scope.Path, failingWriter);
        var nextJson = store.SerializeState(NewState("dark"));

        AssertThrows<IOException>(
            () => store.SaveJsonSync(nextJson, version: 2),
            $"save should fail at {stage}");

        var recovered = NewStore(scope.Path, DurableAtomicFileWriter.Shared).Load();
        Assert(recovered.Theme == "light", $"old generation was not recoverable after {stage}");
    }
}

static void BackupRefreshFaultKeepsOldBackup()
{
    using var scope = new TempDirectory();
    var seed = NewStore(scope.Path, DurableAtomicFileWriter.Shared);
    seed.SaveJsonSync(seed.SerializeState(NewState("light")), version: 1);
    Assert(seed.TryRefreshBackupFromPrimary(), "initial backup refresh failed");
    seed.SaveJsonSync(seed.SerializeState(NewState("dark")), version: 2);

    var failingWriter = new DurableAtomicFileWriter((stage, target) =>
    {
        if (stage == DurableAtomicWriteStage.BeforeReplace &&
            target.EndsWith("data.backup.json", StringComparison.OrdinalIgnoreCase))
        {
            throw new IOException("Injected backup replace failure");
        }
    });

    var store = NewStore(scope.Path, failingWriter);
    Assert(!store.TryRefreshBackupFromPrimary(), "failed backup refresh unexpectedly succeeded");
    Assert(ReadTheme(store.BackupPath) == "light", "old backup was overwritten on failed refresh");
}

static void CorruptPrimaryNeverRefreshesBackup()
{
    using var scope = new TempDirectory();
    var store = NewStore(scope.Path, DurableAtomicFileWriter.Shared);
    store.SaveJsonSync(store.SerializeState(NewState("light")), version: 1);
    Assert(store.TryRefreshBackupFromPrimary(), "initial backup refresh failed");

    File.WriteAllBytes(store.FilePath, Enumerable.Repeat((byte)0, 4096).ToArray());
    Assert(!store.TryRefreshBackupFromPrimary(), "corrupt primary was accepted for backup refresh");
    Assert(ReadTheme(store.BackupPath) == "light", "healthy backup changed after corrupt primary");
}

static void OlderSaveVersionCannotOverwriteNewer()
{
    using var scope = new TempDirectory();
    var store = NewStore(scope.Path, DurableAtomicFileWriter.Shared);
    store.SaveJsonSync(store.SerializeState(NewState("dark")), version: 2);
    store.SaveJsonSync(store.SerializeState(NewState("light")), version: 1);

    Assert(NewStore(scope.Path, DurableAtomicFileWriter.Shared).Load().Theme == "dark",
        "older save version overwrote the newer state");
}

static void BackupRecoveryIsPreservedUntilNormalSave()
{
    using var scope = new TempDirectory();
    var seed = NewStore(scope.Path, DurableAtomicFileWriter.Shared);
    seed.SaveJsonSync(seed.SerializeState(NewState("light")), version: 1);
    Assert(seed.TryRefreshBackupFromPrimary(), "initial backup refresh failed");

    File.WriteAllText(seed.FilePath, "not-json");
    var recoveredStore = NewStore(scope.Path, DurableAtomicFileWriter.Shared);
    Assert(recoveredStore.Load().Theme == "light", "backup recovery did not load");
    Assert(!recoveredStore.TryRefreshBackupFromPrimary(),
        "backup used for recovery was allowed to refresh immediately");
    Assert(ReadTheme(recoveredStore.BackupPath) == "light", "recovery backup was changed");
}

static void PluginSystemShutdownSkipsFinalFlush()
{
    using var scope = new TempDirectory();
    var writer = new RecordingWriter();
    var store = new PaperBodyPluginDataStore(scope.Path, writer);
    store.SavePaperState("sample.plugin", "paper-1", 1, "{\"value\":1}");
    store.SuppressFinalFlushOnDispose();
    store.Dispose();

    Assert(writer.WriteCount == 0, "system-shutdown disposal started a plugin final write");
}

static void PluginNormalDisposeStillFinalFlushes()
{
    using var scope = new TempDirectory();
    var writer = new RecordingWriter();
    var store = new PaperBodyPluginDataStore(scope.Path, writer);
    store.SavePaperState("sample.plugin", "paper-1", 1, "{\"value\":1}");
    store.Dispose();

    Assert(writer.WriteCount == 1, "normal plugin disposal did not flush dirty state exactly once");
}

static void ShutdownSkipsDeferredPluginCleanup()
{
    var controller = (AppController)RuntimeHelpers.GetUninitializedObject(typeof(AppController));
    var lifecycleField = typeof(AppController).GetField(
        "_lifecycleState",
        BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("AppController lifecycle field not found.");
    lifecycleField.SetValue(controller, Enum.Parse(lifecycleField.FieldType, "Exiting"));

    // An uninitialized controller has no plugin/store collections. The shutdown gate must return
    // before touching any of them; otherwise this call throws and the check fails.
    controller.TryFlushPendingPluginPaperStateDeletes();
}

static void TempValidatorFailureKeepsOldTarget()
{
    using var scope = new TempDirectory();
    var target = Path.Combine(scope.Path, "state.json");
    File.WriteAllText(target, "old");

    var writer = new DurableAtomicFileWriter();
    AssertThrows<InvalidDataException>(
        () => writer.Write(target, "new"u8.ToArray(), _ => false),
        "validator failure should reject the temp file");

    Assert(File.ReadAllText(target) == "old", "validator failure replaced the old target");
}

static void FlushFailureKeepsOldTarget()
{
    using var scope = new TempDirectory();
    var target = Path.Combine(scope.Path, "state.json");
    File.WriteAllText(target, "old");

    var operations = new ScriptedFileOperations
    {
        FailFlush = true
    };
    var writer = new DurableAtomicFileWriter(fileOperations: operations);

    AssertThrows<IOException>(
        () => writer.Write(target, "new"u8.ToArray()),
        "flush failure should abort the durable write");
    Assert(File.ReadAllText(target) == "old", "flush failure replaced the old target");
    Assert(operations.ReplaceCalls == 0, "replace ran after flush failure");
}

static void ReplaceRetriesTransientSharingFailures()
{
    using var scope = new TempDirectory();
    var target = Path.Combine(scope.Path, "state.json");
    File.WriteAllText(target, "old");

    var operations = new ScriptedFileOperations
    {
        ReplaceFailuresRemaining = 2
    };
    var writer = new DurableAtomicFileWriter(fileOperations: operations);
    writer.Write(target, "new"u8.ToArray());

    Assert(File.ReadAllText(target) == "new", "transient replace failures did not eventually commit");
    Assert(operations.ReplaceCalls == 3, $"expected 3 replace attempts, got {operations.ReplaceCalls}");
    Assert(operations.DelayCalls == 2, $"expected 2 retry delays, got {operations.DelayCalls}");
}

static void MarkdownModesMigrateAndRoundTrip()
{
    Assert(new AppState().MarkdownRenderMode == MarkdownRenderModes.Basic,
        "new state should default to Basic");
    var cases = new (string? Input, string Expected)[]
    {
        ("\"off\"", MarkdownRenderModes.Off),
        ("\"basic\"", MarkdownRenderModes.Basic),
        ("\"enhanced\"", MarkdownRenderModes.Basic),
        ("\"full\"", MarkdownRenderModes.Full),
        ("\"unknown\"", MarkdownRenderModes.Basic),
        ("null", MarkdownRenderModes.Basic),
        (null, MarkdownRenderModes.Basic)
    };

    foreach (var (input, expected) in cases)
    {
        using var scope = new TempDirectory();
        var store = NewStore(scope.Path, DurableAtomicFileWriter.Shared);
        var setting = input == null ? "" : ",\"markdownRenderMode\":" + input;
        File.WriteAllText(store.FilePath,
            "{\"papers\":[{\"id\":\"mode-note\",\"type\":\"note\",\"content\":\"# Keep **source**\"}]" + setting + "}");
        var state = store.Load();
        Assert(state.MarkdownRenderMode == expected, $"wrong migration for {input ?? "missing"}");
        Assert(state.Papers.Single().Content == "# Keep **source**", "mode migration changed note content");

        store.SaveJsonSync(store.SerializeState(state), version: 1);
        using var saved = JsonDocument.Parse(File.ReadAllText(store.FilePath));
        Assert(saved.RootElement.GetProperty("markdownRenderMode").GetString() == expected,
            "save retained a legacy/invalid Markdown mode");
        Assert(NewStore(scope.Path, DurableAtomicFileWriter.Shared).Load().MarkdownRenderMode == expected,
            "Markdown mode changed after reload");
    }
}

static void DownwardPreviewDefaultAndRoundTrip()
{
    Assert(!new AppState().EdgeCapsulePreviewPreferDownward, "new state should not prefer downward preview");
    foreach (var input in new string?[] { null, "true", "false" })
    {
        using var scope = new TempDirectory();
        var store = NewStore(scope.Path, DurableAtomicFileWriter.Shared);
        var setting = input == null ? "" : ",\"edgeCapsulePreviewPreferDownward\":" + input;
        File.WriteAllText(store.FilePath, "{\"papers\":[]" + setting + "}");
        var state = store.Load();
        var expected = input == "true";
        Assert(state.EdgeCapsulePreviewPreferDownward == expected,
            $"wrong preview preference for {input ?? "missing"}");

        store.SaveJsonSync(store.SerializeState(state), version: 1);
        using var saved = JsonDocument.Parse(File.ReadAllText(store.FilePath));
        Assert(saved.RootElement.GetProperty("edgeCapsulePreviewPreferDownward").GetBoolean() == expected,
            "save lost the preview preference");
        Assert(NewStore(scope.Path, DurableAtomicFileWriter.Shared).Load().EdgeCapsulePreviewPreferDownward == expected,
            "preview preference changed after reload");
    }
}

static void LmdbOnlineSnapshotIsConsistent()
{
    using var scope = new TempDirectory();
    var sourcePath = Path.Combine(scope.Path, "source.lmdb");
    var snapshotPath = Path.Combine(scope.Path, "snapshot.lmdb");
    using (var database = LmdbImageDatabase.Open(sourcePath))
    {
        database.AddImages([NewImageWrite("1", new byte[] { 1, 2, 3 })], nextImageNumber: 2);
        database.CreateSnapshot(snapshotPath);
        database.AddImages([NewImageWrite("2", new byte[] { 4, 5 })], nextImageNumber: 3);
    }

    using var snapshot = LmdbImageDatabase.Open(snapshotPath);
    var index = snapshot.ReadIndex();
    Assert(index.Assets.Count == 1 && index.Assets[0].Id == "1", "snapshot did not preserve its copy point");
    Assert(snapshot.TryReadBlob("1", out var bytes) && bytes.SequenceEqual(new byte[] { 1, 2, 3 }), "snapshot blob changed");
    Assert(!snapshot.TryReadBlob("2", out _), "snapshot included a later write");
}

static LmdbImageWrite NewImageWrite(string id, byte[] bytes)
{
    var asset = new NoteImageAsset
    {
        Id = id,
        NoteId = "note-1",
        Mime = "image/png",
        Width = 1,
        Height = 1,
        Sha256 = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes)),
        ByteLength = bytes.Length,
        CreatedAt = DateTimeOffset.UtcNow
    };
    return new LmdbImageWrite(asset, bytes);
}

static void StoragePathsPreserveLegacyData()
{
    using var scope = new TempDirectory();
    var app = Path.Combine(scope.Path, "app");
    var local = Path.Combine(scope.Path, "local");
    var documents = Path.Combine(scope.Path, "documents");
    Directory.CreateDirectory(app);
    File.WriteAllText(Path.Combine(app, "data.json"), "{\"papers\":[]}");

    var storage = AppStoragePaths.Load(app, local, documents);

    Assert(Path.GetFullPath(storage.DataDirectory) == Path.GetFullPath(app),
        "existing program-directory data was not preserved");
    Assert(storage.CacheDirectory == Path.GetFullPath(Path.Combine(local, "PaperNook", "Cache")),
        "cache did not use LocalAppData default");
    Assert(storage.NotesDirectory == Path.GetFullPath(Path.Combine(documents, "PaperNook Notes")),
        "notes did not use Documents default");
    Assert(storage.BackupDirectory == Path.GetFullPath(Path.Combine(documents, "PaperNook Backups")),
        "backups did not use Documents default");
}

static void StoragePathsImportPaperTodoProfile()
{
    using var scope = new TempDirectory();
    var app = Path.Combine(scope.Path, "app");
    var local = Path.Combine(scope.Path, "local");
    var documents = Path.Combine(scope.Path, "documents");
    Directory.CreateDirectory(app);
    var legacyRoot = Path.Combine(local, "PaperTodo");
    var legacyData = Path.Combine(legacyRoot, "Data");
    var legacyCache = Path.Combine(legacyRoot, "Cache");
    var legacyNotes = Path.Combine(documents, "PaperTodo Notes");
    var legacyBackups = Path.Combine(documents, "PaperTodo Backups");
    Directory.CreateDirectory(legacyData);
    Directory.CreateDirectory(legacyCache);
    Directory.CreateDirectory(legacyNotes);
    Directory.CreateDirectory(legacyBackups);
    File.WriteAllText(Path.Combine(legacyData, "data.json"), "{\"papers\":[]}");
    File.WriteAllText(Path.Combine(legacyNotes, "imported.md"), "legacy note");
    File.WriteAllText(Path.Combine(legacyBackups, "legacy.papertodo-backup"), "legacy backup");
    File.WriteAllText(
        Path.Combine(legacyRoot, "storage.json"),
        JsonSerializer.Serialize(new
        {
            version = 2,
            dataDirectory = legacyData,
            notesDirectory = legacyNotes,
            cacheDirectory = legacyCache,
            backupDirectory = legacyBackups,
            pendingDataDirectory = "",
            pendingNotesDirectory = "",
            pendingCacheDirectory = "",
            pendingBackupDirectory = ""
        }));

    var storage = AppStoragePaths.Load(app, local, documents);

    Assert(storage.DataDirectory == Path.GetFullPath(Path.Combine(local, "PaperNook", "Data")),
        "legacy data was not imported to the PaperNook profile");
    Assert(File.Exists(Path.Combine(storage.DataDirectory, "data.json")), "legacy data file was not imported");
    Assert(File.Exists(Path.Combine(storage.NotesDirectory, "imported.md")), "legacy note was not imported");
    Assert(File.Exists(Path.Combine(storage.BackupDirectory, "legacy.papertodo-backup")), "legacy backup was not imported");
    Assert(File.Exists(Path.Combine(legacyData, "data.json")), "legacy source data was deleted");
    Assert(storage.ConfigurationFilePath == Path.GetFullPath(Path.Combine(local, "PaperNook", "storage.json")),
        "PaperNook did not create an independent storage configuration");
}

static void StorageV1UpgradesWithBackupDirectory()
{
    using var scope = new TempDirectory();
    var app = Path.Combine(scope.Path, "app");
    var local = Path.Combine(scope.Path, "local");
    var documents = Path.Combine(scope.Path, "documents");
    Directory.CreateDirectory(app);
    var configurationRoot = Path.Combine(local, "PaperNook");
    Directory.CreateDirectory(configurationRoot);
    File.WriteAllText(
        Path.Combine(configurationRoot, "storage.json"),
        JsonSerializer.Serialize(new
        {
            version = 1,
            dataDirectory = Path.Combine(local, "PaperNook", "Data"),
            notesDirectory = Path.Combine(documents, "PaperNook Notes"),
            cacheDirectory = Path.Combine(local, "PaperNook", "Cache"),
            pendingDataDirectory = "",
            pendingNotesDirectory = "",
            pendingCacheDirectory = ""
        }));

    var storage = AppStoragePaths.Load(app, local, documents);

    Assert(storage.BackupDirectory == Path.GetFullPath(Path.Combine(documents, "PaperNook Backups")),
        "version 1 storage config did not receive the backup default");
    using var saved = JsonDocument.Parse(File.ReadAllText(storage.ConfigurationFilePath));
    Assert(saved.RootElement.GetProperty("version").GetInt32() == 2,
        "version 1 storage config was not upgraded on save");
}

static void StoragePathsMigrateDataBeforeStartup()
{
    using var scope = new TempDirectory();
    var app = Path.Combine(scope.Path, "app");
    var local = Path.Combine(scope.Path, "local");
    var documents = Path.Combine(scope.Path, "documents");
    Directory.CreateDirectory(app);
    var storage = AppStoragePaths.Load(app, local, documents);
    File.WriteAllText(Path.Combine(storage.DataDirectory, "data.json"), "{\"papers\":[]}");
    Directory.CreateDirectory(storage.PluginDataDirectory);
    File.WriteAllText(Path.Combine(storage.PluginDataDirectory, "sample.json"), "{}");
    var target = Path.Combine(scope.Path, "migrated-data");

    Assert(storage.TryScheduleChange(AppStorageDirectoryKind.Data, target, out var scheduleError),
        $"could not schedule migration: {scheduleError}");
    var restarted = AppStoragePaths.Load(app, local, documents);

    Assert(restarted.DataDirectory == Path.GetFullPath(target), "new data path was not activated");
    Assert(File.Exists(Path.Combine(target, "data.json")), "core state was not migrated");
    Assert(File.Exists(Path.Combine(target, "plugins", "data", "sample.json")),
        "plugin state was not migrated");
    Assert(File.Exists(Path.Combine(storage.DataDirectory, "data.json")),
        "source data was deleted before user confirmation");
}

static void StoragePathsCopyBackupsBeforeSwitch()
{
    using var scope = new TempDirectory();
    var app = Path.Combine(scope.Path, "app");
    var local = Path.Combine(scope.Path, "local");
    var documents = Path.Combine(scope.Path, "documents");
    Directory.CreateDirectory(app);
    var storage = AppStoragePaths.Load(app, local, documents);
    var sourceBackup = Path.Combine(storage.BackupDirectory, "manual.papertodo-backup");
    File.WriteAllText(sourceBackup, "verified-fixture");
    var target = Path.Combine(scope.Path, "migrated-backups");

    Assert(storage.TryScheduleChange(AppStorageDirectoryKind.Backup, target, out var error),
        $"could not schedule backup migration: {error}");
    var restarted = AppStoragePaths.Load(app, local, documents);

    Assert(restarted.BackupDirectory == Path.GetFullPath(target), "new backup path was not activated");
    Assert(File.ReadAllText(Path.Combine(target, "manual.papertodo-backup")) == "verified-fixture",
        "backup was not copied before switching locations");
    Assert(File.Exists(sourceBackup), "source backup was deleted automatically");
}

static void StoragePathsRejectOverlap()
{
    using var scope = new TempDirectory();
    var app = Path.Combine(scope.Path, "app");
    var local = Path.Combine(scope.Path, "local");
    var documents = Path.Combine(scope.Path, "documents");
    Directory.CreateDirectory(app);
    var storage = AppStoragePaths.Load(app, local, documents);
    var dataTarget = Path.Combine(scope.Path, "custom-data");
    Assert(storage.TryScheduleChange(AppStorageDirectoryKind.Data, dataTarget, out _),
        "valid data directory was rejected");

    Assert(!storage.TryScheduleChange(
            AppStorageDirectoryKind.Notes,
            Path.Combine(dataTarget, "notes"),
            out _),
        "nested notes and data directories were accepted");
}

static void StorageCacheClearDoesNotTouchData()
{
    using var scope = new TempDirectory();
    var app = Path.Combine(scope.Path, "app");
    var local = Path.Combine(scope.Path, "local");
    var documents = Path.Combine(scope.Path, "documents");
    Directory.CreateDirectory(app);
    var storage = AppStoragePaths.Load(app, local, documents);
    var dataFile = Path.Combine(storage.DataDirectory, "data.json");
    var cacheFile = Path.Combine(storage.CacheDirectory, "WebView2", "cache.bin");
    Directory.CreateDirectory(Path.GetDirectoryName(cacheFile)!);
    File.WriteAllText(dataFile, "{\"papers\":[]}");
    File.WriteAllText(cacheFile, "cache");

    Assert(storage.TryClearCache(out var clearError), $"cache clear failed: {clearError}");
    Assert(File.Exists(dataFile), "cache clear removed durable data");
    Assert(!File.Exists(cacheFile), "cache file remained after clear");
}

static void StorageConfigRecoversWithoutOverwritingEvidence()
{
    using var scope = new TempDirectory();
    var app = Path.Combine(scope.Path, "app");
    var local = Path.Combine(scope.Path, "local");
    var documents = Path.Combine(scope.Path, "documents");
    Directory.CreateDirectory(app);
    var storage = AppStoragePaths.Load(app, local, documents);
    var customNotes = Path.Combine(scope.Path, "custom-notes");
    Assert(storage.TryScheduleChange(AppStorageDirectoryKind.Notes, customNotes, out _),
        "valid notes path was not scheduled");
    _ = AppStoragePaths.Load(app, local, documents);
    Assert(File.Exists(Path.Combine(Path.GetDirectoryName(storage.ConfigurationFilePath)!, "storage.backup.json")),
        "storage configuration backup was not created");
    File.WriteAllText(storage.ConfigurationFilePath, "not-json");

    var recovered = AppStoragePaths.Load(app, local, documents);

    Assert(recovered.NotesDirectory == Path.GetFullPath(customNotes),
        "storage configuration did not recover from backup");
    Assert(Directory.EnumerateFiles(
            Path.GetDirectoryName(storage.ConfigurationFilePath)!,
            "storage.failed_load.*.json").Any(),
        "invalid storage configuration was overwritten without preserving evidence");
}

static void StorageConfigRejectsValidJsonWithInvalidPaths()
{
    using var scope = new TempDirectory();
    var app = Path.Combine(scope.Path, "app");
    var local = Path.Combine(scope.Path, "local");
    var documents = Path.Combine(scope.Path, "documents");
    Directory.CreateDirectory(app);
    var configurationRoot = Path.Combine(local, "PaperNook");
    Directory.CreateDirectory(configurationRoot);
    var shared = Path.Combine(scope.Path, "shared");
    File.WriteAllText(
        Path.Combine(configurationRoot, "storage.json"),
        JsonSerializer.Serialize(new
        {
            version = 1,
            dataDirectory = shared,
            notesDirectory = Path.Combine(shared, "notes"),
            cacheDirectory = Path.Combine(scope.Path, "cache"),
            pendingDataDirectory = "",
            pendingNotesDirectory = "",
            pendingCacheDirectory = ""
        }));

    var recovered = AppStoragePaths.Load(app, local, documents);

    Assert(recovered.DataDirectory == Path.GetFullPath(Path.Combine(local, "PaperNook", "Data")),
        "semantically invalid storage configuration was accepted");
    Assert(Directory.EnumerateFiles(configurationRoot, "storage.failed_load.*.json").Any(),
        "semantically invalid storage configuration was not preserved");
}

static void StoragePortableConfigMovesWithApp()
{
    using var scope = new TempDirectory();
    var originalApp = Path.Combine(scope.Path, "portable-original");
    var movedApp = Path.Combine(scope.Path, "portable-moved");
    var local = Path.Combine(scope.Path, "local");
    var documents = Path.Combine(scope.Path, "documents");
    Directory.CreateDirectory(originalApp);
    File.WriteAllText(Path.Combine(originalApp, "papernook.portable"), "portable");
    var original = AppStoragePaths.Load(originalApp, local, documents);
    File.WriteAllText(Path.Combine(original.DataDirectory, "data.json"), "{\"papers\":[]}");

    Directory.Move(originalApp, movedApp);
    var moved = AppStoragePaths.Load(movedApp, local, documents);

    Assert(moved.DataDirectory == Path.GetFullPath(Path.Combine(movedApp, "Data")),
        "portable data path did not move with the application folder");
    Assert(moved.NotesDirectory == Path.GetFullPath(Path.Combine(movedApp, "Notes")),
        "portable notes path did not move with the application folder");
    Assert(moved.CacheDirectory == Path.GetFullPath(Path.Combine(movedApp, "Cache")),
        "portable cache path did not move with the application folder");
    Assert(moved.BackupDirectory == Path.GetFullPath(Path.Combine(movedApp, "Backups")),
        "portable backup path did not move with the application folder");
    Assert(File.Exists(Path.Combine(moved.DataDirectory, "data.json")),
        "portable data was not available after moving the application folder");
}

static void BackupPackageIsVerifiedAndExcludesCache()
{
    using var scope = new TempDirectory();
    var app = Path.Combine(scope.Path, "app");
    var local = Path.Combine(scope.Path, "local");
    var documents = Path.Combine(scope.Path, "documents");
    Directory.CreateDirectory(app);
    var storage = AppStoragePaths.Load(app, local, documents);
    var store = NewStore(storage.DataDirectory, DurableAtomicFileWriter.Shared);
    store.SaveJsonAsync(store.SerializeState(NewState("dark")), 1).GetAwaiter().GetResult();
    using var images = new NoteImageStore(Path.Combine(storage.DataDirectory, "note-assets.lmdb"));
    images.Load();
    File.WriteAllText(Path.Combine(storage.PluginDataDirectory, "sample.json"), "{}");
    File.WriteAllText(Path.Combine(storage.CacheDirectory, "should-not-back-up.bin"), "cache");
    var service = new BackupService(storage, store, images, () => { }, () => { });

    var result = service.CreateAsync(
        new BackupRequest(BackupKind.Manual, IncludeExportedNotes: false),
        progress: null,
        CancellationToken.None).GetAwaiter().GetResult();

    Assert(File.Exists(result.BackupPath), "verified backup was not published");
    using var zip = ZipFile.OpenRead(result.BackupPath);
    var names = zip.Entries.Select(entry => entry.FullName).ToArray();
    Assert(names.SequenceEqual(names.OrderBy(name => name, StringComparer.Ordinal)),
        "backup entries are not deterministically sorted");
    Assert(names.Contains("manifest.json"), "backup manifest is missing");
    Assert(names.Contains("data/data.json"), "core state is missing");
    Assert(names.Contains("data/note-assets.lmdb"), "LMDB snapshot is missing");
    Assert(names.Contains("plugins/data/sample.json"), "plugin state is missing");
    Assert(!names.Any(name => name.Contains("cache", StringComparison.OrdinalIgnoreCase)),
        "cache leaked into backup");
    Assert(!names.Any(name => name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)),
        "executable leaked into backup");
    Assert(BackupService.VerifyPackage(result.BackupPath).IsValid,
        "newly created backup did not pass independent verification");
}

static void BackupCancellationLeavesNoVisiblePackage()
{
    using var scope = new TempDirectory();
    var app = Path.Combine(scope.Path, "app");
    var local = Path.Combine(scope.Path, "local");
    var documents = Path.Combine(scope.Path, "documents");
    Directory.CreateDirectory(app);
    var storage = AppStoragePaths.Load(app, local, documents);
    var store = NewStore(storage.DataDirectory, DurableAtomicFileWriter.Shared);
    store.SaveJsonAsync(store.SerializeState(NewState("light")), 1).GetAwaiter().GetResult();
    using var images = new NoteImageStore(Path.Combine(storage.DataDirectory, "note-assets.lmdb"));
    images.Load();
    var service = new BackupService(storage, store, images, () => { }, () => { });
    using var cancellation = new CancellationTokenSource();
    cancellation.Cancel();

    try
    {
        service.CreateAsync(
            new BackupRequest(BackupKind.Manual, IncludeExportedNotes: false),
            progress: null,
            cancellation.Token).GetAwaiter().GetResult();
        throw new InvalidOperationException("cancelled backup unexpectedly succeeded");
    }
    catch (OperationCanceledException)
    {
    }

    Assert(!BackupService.EnumerateBackupFiles(storage.BackupDirectory).Any(),
        "cancelled backup published a visible package");
    Assert(!Directory.EnumerateFileSystemEntries(storage.BackupDirectory, "*.tmp*").Any(),
        "cancelled backup left temporary files");
}

static void BackupTamperingIsRejected()
{
    using var scope = new TempDirectory();
    var app = Path.Combine(scope.Path, "app");
    Directory.CreateDirectory(app);
    var storage = AppStoragePaths.Load(app, Path.Combine(scope.Path, "local"), Path.Combine(scope.Path, "documents"));
    var store = NewStore(storage.DataDirectory, DurableAtomicFileWriter.Shared);
    store.SaveJsonSync(store.SerializeState(NewState("dark")), 1);
    using var images = new NoteImageStore(Path.Combine(storage.DataDirectory, "note-assets.lmdb"));
    images.Load();
    var backup = new BackupService(storage, store, images, () => { }, () => { })
        .CreateAsync(new BackupRequest(BackupKind.Manual, false), null, CancellationToken.None)
        .GetAwaiter().GetResult();

    using (var archive = ZipFile.Open(backup.BackupPath, ZipArchiveMode.Update))
    {
        var state = archive.GetEntry("data/data.json")
            ?? throw new InvalidDataException("test backup state entry is missing");
        state.Delete();
        using var writer = new StreamWriter(archive.CreateEntry("data/data.json").Open());
        writer.Write("tampered");
    }

    Assert(!BackupService.VerifyPackage(backup.BackupPath).IsValid,
        "backup with a hash mismatch was accepted");
}

static void RestoreRejectsPathTraversal()
{
    using var scope = new TempDirectory();
    var malicious = Path.Combine(scope.Path, "malicious.papertodo-backup");
    using (var archive = ZipFile.Open(malicious, ZipArchiveMode.Create))
    {
        using var writer = new StreamWriter(archive.CreateEntry("../evil.txt").Open());
        writer.Write("evil");
    }
    var app = Path.Combine(scope.Path, "app");
    Directory.CreateDirectory(app);
    var storage = AppStoragePaths.Load(
        app,
        Path.Combine(scope.Path, "local"),
        Path.Combine(scope.Path, "documents"));
    var inspection = new BackupRestoreCoordinator(storage).Inspect(malicious);

    Assert(!inspection.IsCompatible, "path traversal backup was accepted");
    Assert(!File.Exists(Path.Combine(scope.Path, "evil.txt")), "path traversal wrote outside staging");
}

static void RestoreSwitchesAndKeepsRollbackCopy()
{
    using var scope = new TempDirectory();
    var app = Path.Combine(scope.Path, "app");
    var local = Path.Combine(scope.Path, "local");
    var documents = Path.Combine(scope.Path, "documents");
    Directory.CreateDirectory(app);
    var storage = AppStoragePaths.Load(app, local, documents);
    var store = NewStore(storage.DataDirectory, DurableAtomicFileWriter.Shared);
    store.SaveJsonSync(store.SerializeState(NewState("backup-theme")), 1);
    using var images = new NoteImageStore(Path.Combine(storage.DataDirectory, "note-assets.lmdb"));
    images.Load();
    var service = new BackupService(storage, store, images, () => { }, () => { });
    var backup = service.CreateAsync(
        new BackupRequest(BackupKind.Manual, false),
        null,
        CancellationToken.None).GetAwaiter().GetResult();
    store.SaveJsonSync(store.SerializeState(NewState("current-theme")), 2);

    var coordinator = new BackupRestoreCoordinator(storage);
    coordinator.ScheduleAsync(backup.BackupPath, CancellationToken.None).GetAwaiter().GetResult();
    images.Dispose();
    BackupRestoreCoordinator.ApplyPendingBeforeStoresOpen(storage);

    Assert(ReadTheme(Path.Combine(storage.DataDirectory, "data.json")) == "backup-theme",
        "restore did not switch to backup state");
    var journal = PendingRestoreJournal.TryLoad(storage.DataDirectory)
        ?? throw new InvalidOperationException("restore journal disappeared before health confirmation");
    Assert(Directory.Exists(journal.BeforeRestoreDirectory), "restore did not retain rollback data");
    Assert(ReadTheme(Path.Combine(journal.BeforeRestoreDirectory, "data.json")) == "current-theme",
        "rollback copy does not contain the replaced state");
    BackupRestoreCoordinator.MarkHealthy(storage);
    Assert(PendingRestoreJournal.TryLoad(storage.DataDirectory) == null,
        "healthy restore did not clear pending journal");
}

static void PluginHealthOnlyTriggersOnConsecutiveStartupFailures()
{
    using var scope = new TempDirectory();
    var first = new PluginStartupHealthStore(scope.Path);
    Assert(!first.BeginRun().ShouldEnterSafeMode, "first clean run entered safe mode");
    first.EnterStage(PluginStartupStage.Running);
    first.MarkHealthy();
    first.EnterStage(PluginStartupStage.ActivatingPlugin, "late-plugin", "late-fingerprint");

    var afterRunningPowerLoss = new PluginStartupHealthStore(scope.Path);
    Assert(!afterRunningPowerLoss.BeginRun().ShouldEnterSafeMode,
        "power loss during Running was blamed on a plugin");
    afterRunningPowerLoss.EnterStage(PluginStartupStage.DiscoveringPlugins, "sample", "fp1");

    var firstPluginFailure = new PluginStartupHealthStore(scope.Path);
    var firstAssessment = firstPluginFailure.BeginRun();
    Assert(!firstAssessment.ShouldEnterSafeMode && firstAssessment.ConsecutivePluginFailures == 1,
        "one plugin-stage failure entered safe mode");
    firstPluginFailure.EnterStage(PluginStartupStage.ActivatingPlugin, "sample", "fp1");

    var secondPluginFailure = new PluginStartupHealthStore(scope.Path);
    var secondAssessment = secondPluginFailure.BeginRun();
    Assert(secondAssessment.ShouldEnterSafeMode && secondAssessment.ConsecutivePluginFailures == 2,
        "two consecutive plugin-stage failures did not enter safe mode");
    Assert(secondAssessment.PluginId == "sample" && secondAssessment.Fingerprint == "fp1",
        "safe-mode assessment lost the suspected plugin identity");
}

static void RestoreResumesAfterOldGenerationMove()
{
    using var scope = new TempDirectory();
    var app = Path.Combine(scope.Path, "app");
    Directory.CreateDirectory(app);
    var storage = AppStoragePaths.Load(app, Path.Combine(scope.Path, "local"), Path.Combine(scope.Path, "documents"));
    var store = NewStore(storage.DataDirectory, DurableAtomicFileWriter.Shared);
    store.SaveJsonSync(store.SerializeState(NewState("restored")), 1);
    var images = new NoteImageStore(Path.Combine(storage.DataDirectory, "note-assets.lmdb"));
    images.Load();
    var backup = new BackupService(storage, store, images, () => { }, () => { })
        .CreateAsync(new BackupRequest(BackupKind.Manual, false), null, CancellationToken.None)
        .GetAwaiter().GetResult();
    store.SaveJsonSync(store.SerializeState(NewState("current")), 2);
    new BackupRestoreCoordinator(storage)
        .ScheduleAsync(backup.BackupPath, CancellationToken.None).GetAwaiter().GetResult();
    images.Dispose();
    var journal = PendingRestoreJournal.TryLoad(storage.DataDirectory)
        ?? throw new InvalidOperationException("pending restore journal is missing");
    Directory.Move(journal.TargetDataDirectory, journal.BeforeRestoreDirectory);

    BackupRestoreCoordinator.ApplyPendingBeforeStoresOpen(storage);

    Assert(ReadTheme(Path.Combine(storage.DataDirectory, "data.json")) == "restored",
        "restore did not resume after interruption following the old generation move");
    BackupRestoreCoordinator.MarkHealthy(storage);
}

static void PluginPolicyKeepsBuiltinAndResetsUpdatedFingerprint()
{
    using var scope = new TempDirectory();
    var policy = new PluginPolicyStore(scope.Path);
    policy.EnterAutomaticSafeMode(new PluginStartupAssessment(
        true, "sample", "old-fingerprint", 2, DateTimeOffset.UtcNow));

    Assert(policy.IsEnabled(PaperBodyProviderIds.Markdown, "builtin"),
        "built-in Markdown was disabled in safe mode");
    Assert(!policy.IsEnabled("sample", "old-fingerprint"),
        "suspected plugin remained enabled in safe mode");
    policy.ObserveFingerprint("sample", "new-fingerprint");
    Assert(!policy.SafeMode.Active && policy.IsEnabled("sample", "new-fingerprint"),
        "updated plugin fingerprint did not reset stale quarantine");
}

static StateStore NewStore(string directory, IDurableAtomicFileWriter writer) =>
    new(directory, writer);

static AppState NewState(string theme) => new()
{
    Theme = theme,
    Papers = []
};

static string ReadTheme(string path)
{
    using var document = JsonDocument.Parse(File.ReadAllText(path));
    return document.RootElement.GetProperty("theme").GetString()
        ?? throw new InvalidDataException($"Missing theme in {path}");
}

static void Assert(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}

static void AssertThrows<TException>(Action action, string message)
    where TException : Exception
{
    try
    {
        action();
    }
    catch (TException)
    {
        return;
    }

    throw new InvalidOperationException(message);
}

internal sealed class RecordingWriter : IDurableAtomicFileWriter
{
    public int WriteCount { get; private set; }

    public void Write(
        string targetPath,
        byte[] bytes,
        Func<string, bool>? validateTemp = null)
    {
        WriteCount++;
    }
}

internal sealed class ScriptedFileOperations : IDurableAtomicFileOperations
{
    public bool FailFlush { get; init; }
    public int ReplaceFailuresRemaining { get; set; }
    public int ReplaceCalls { get; private set; }
    public int DelayCalls { get; private set; }

    public void FlushToDisk(FileStream stream)
    {
        if (FailFlush)
        {
            throw new IOException("Injected Flush(true) failure.");
        }

        stream.Flush(flushToDisk: true);
    }

    public void Replace(string tempPath, string targetPath)
    {
        ReplaceCalls++;
        if (ReplaceFailuresRemaining > 0)
        {
            ReplaceFailuresRemaining--;
            throw new IOException("Injected transient sharing violation.");
        }

        File.Move(tempPath, targetPath, overwrite: true);
    }

    public void Delay(TimeSpan delay)
    {
        DelayCalls++;
    }

    public void Delete(string path)
    {
        File.Delete(path);
    }
}

internal sealed class TempDirectory : IDisposable
{
    public TempDirectory()
    {
        Path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "PaperTodo.PersistenceChecks",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch
        {
        }
    }
}
