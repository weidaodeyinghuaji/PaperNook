using System.IO;
using System.Security.Cryptography;
using System.Text.Json;

namespace PaperTodo;

internal enum AppStorageDirectoryKind
{
    Data,
    Notes,
    Cache,
    Backup
}

internal sealed class AppStoragePaths
{
    private const int ConfigurationVersion = 2;
    private const string PortableMarkerFileName = "papernook.portable";
    private const string LegacyPortableMarkerFileName = "papertodo.portable";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerOptions.Strict)
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private sealed class StorageConfiguration
    {
        public int Version { get; set; } = ConfigurationVersion;
        public string DataDirectory { get; set; } = "";
        public string NotesDirectory { get; set; } = "";
        public string CacheDirectory { get; set; } = "";
        public string BackupDirectory { get; set; } = "";
        public string PendingDataDirectory { get; set; } = "";
        public string PendingNotesDirectory { get; set; } = "";
        public string PendingCacheDirectory { get; set; } = "";
        public string PendingBackupDirectory { get; set; } = "";
    }

    private readonly string _appDirectory;
    private readonly string _localAppDataDirectory;
    private readonly string _documentsDirectory;
    private StorageConfiguration _configuration;
    private bool _preserveInvalidConfiguration;

    private AppStoragePaths(
        string appDirectory,
        string localAppDataDirectory,
        string documentsDirectory,
        string configurationFilePath,
        bool isPortable,
        StorageConfiguration configuration,
        bool preserveInvalidConfiguration)
    {
        _appDirectory = appDirectory;
        _localAppDataDirectory = localAppDataDirectory;
        _documentsDirectory = documentsDirectory;
        ConfigurationFilePath = configurationFilePath;
        IsPortable = isPortable;
        _configuration = configuration;
        _preserveInvalidConfiguration = preserveInvalidConfiguration;
        RefreshResolvedPaths();
    }

    internal static AppStoragePaths? Current { get; private set; }

    internal string ConfigurationFilePath { get; }
    internal bool IsPortable { get; }
    internal string DataDirectory { get; private set; } = "";
    internal string NotesDirectory { get; private set; } = "";
    internal string CacheDirectory { get; private set; } = "";
    internal string BackupDirectory { get; private set; } = "";
    internal string PluginDataDirectory => Path.Combine(DataDirectory, "plugins", "data");
    internal bool HasPendingChanges =>
        !string.IsNullOrWhiteSpace(_configuration.PendingDataDirectory) ||
        !string.IsNullOrWhiteSpace(_configuration.PendingNotesDirectory) ||
        !string.IsNullOrWhiteSpace(_configuration.PendingCacheDirectory) ||
        !string.IsNullOrWhiteSpace(_configuration.PendingBackupDirectory);
    internal string? StartupMigrationError { get; private set; }

    internal static AppStoragePaths Initialize()
    {
        Current ??= Load(
            AppContext.BaseDirectory,
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments));
        return Current;
    }

    internal static AppStoragePaths Load(
        string appDirectory,
        string localAppDataDirectory,
        string documentsDirectory)
    {
        appDirectory = NormalizeDirectory(appDirectory);
        localAppDataDirectory = NormalizeDirectory(localAppDataDirectory);
        documentsDirectory = NormalizeDirectory(documentsDirectory);

        var isPortable = File.Exists(Path.Combine(appDirectory, PortableMarkerFileName)) ||
                         File.Exists(Path.Combine(appDirectory, LegacyPortableMarkerFileName));
        var configurationRoot = isPortable
            ? appDirectory
            : Path.Combine(localAppDataDirectory, ProductIdentity.Name);
        var configurationPath = Path.Combine(configurationRoot, "storage.json");
        var portableRoot = isPortable ? appDirectory : null;
        var defaultBackupDirectory = isPortable
            ? Path.Combine(appDirectory, "Backups")
            : Path.Combine(documentsDirectory, "PaperNook Backups");
        var configuration = TryReadConfiguration(configurationPath, portableRoot, defaultBackupDirectory);
        var preserveInvalidConfiguration = configuration == null && File.Exists(configurationPath);
        configuration ??= TryReadConfiguration(
            Path.Combine(configurationRoot, "storage.backup.json"),
            portableRoot,
            defaultBackupDirectory);
        if (configuration == null && !isPortable && !preserveInvalidConfiguration)
        {
            configuration = TryCreateLegacyImport(
                appDirectory,
                localAppDataDirectory,
                documentsDirectory);
        }
        configuration ??= CreateDefaults(appDirectory, localAppDataDirectory, documentsDirectory, isPortable);
        var result = new AppStoragePaths(
            appDirectory,
            localAppDataDirectory,
            documentsDirectory,
            configurationPath,
            isPortable,
            configuration,
            preserveInvalidConfiguration);
        result.ApplyPendingChanges();
        result.EnsureDirectories();
        result.TrySaveConfiguration();
        return result;
    }

    internal string EffectiveDirectory(AppStorageDirectoryKind kind) => kind switch
    {
        AppStorageDirectoryKind.Data => PendingOrCurrent(
            _configuration.PendingDataDirectory,
            DataDirectory),
        AppStorageDirectoryKind.Notes => PendingOrCurrent(
            _configuration.PendingNotesDirectory,
            NotesDirectory),
        AppStorageDirectoryKind.Cache => PendingOrCurrent(
            _configuration.PendingCacheDirectory,
            CacheDirectory),
        AppStorageDirectoryKind.Backup => PendingOrCurrent(
            _configuration.PendingBackupDirectory,
            BackupDirectory),
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    internal bool TryScheduleChange(
        AppStorageDirectoryKind kind,
        string directory,
        out string error)
    {
        string normalized;
        try
        {
            normalized = NormalizeDirectory(directory);
            ValidateCandidate(kind, normalized);
            VerifyWritable(normalized);
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }

        var previousPending = kind switch
        {
            AppStorageDirectoryKind.Data => _configuration.PendingDataDirectory,
            AppStorageDirectoryKind.Notes => _configuration.PendingNotesDirectory,
            AppStorageDirectoryKind.Cache => _configuration.PendingCacheDirectory,
            AppStorageDirectoryKind.Backup => _configuration.PendingBackupDirectory,
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };
        switch (kind)
        {
            case AppStorageDirectoryKind.Data:
                _configuration.PendingDataDirectory = normalized;
                break;
            case AppStorageDirectoryKind.Notes:
                _configuration.PendingNotesDirectory = normalized;
                break;
            case AppStorageDirectoryKind.Cache:
                _configuration.PendingCacheDirectory = normalized;
                break;
            case AppStorageDirectoryKind.Backup:
                _configuration.PendingBackupDirectory = normalized;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(kind));
        }

        try
        {
            SaveConfiguration();
            error = "";
            return true;
        }
        catch (Exception ex)
        {
            switch (kind)
            {
                case AppStorageDirectoryKind.Data:
                    _configuration.PendingDataDirectory = previousPending;
                    break;
                case AppStorageDirectoryKind.Notes:
                    _configuration.PendingNotesDirectory = previousPending;
                    break;
                case AppStorageDirectoryKind.Cache:
                    _configuration.PendingCacheDirectory = previousPending;
                    break;
                case AppStorageDirectoryKind.Backup:
                    _configuration.PendingBackupDirectory = previousPending;
                    break;
            }
            error = ex.Message;
            return false;
        }
    }

    internal bool TryResetToDefault(AppStorageDirectoryKind kind, out string error)
    {
        var defaults = CreateDefaults(
            _appDirectory,
            _localAppDataDirectory,
            _documentsDirectory,
            IsPortable);
        var directory = kind switch
        {
            AppStorageDirectoryKind.Data => defaults.DataDirectory,
            AppStorageDirectoryKind.Notes => defaults.NotesDirectory,
            AppStorageDirectoryKind.Cache => defaults.CacheDirectory,
            AppStorageDirectoryKind.Backup => defaults.BackupDirectory,
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };
        return TryScheduleChange(kind, directory, out error);
    }

    internal bool TryClearCache(out string error)
    {
        try
        {
            if (Directory.Exists(CacheDirectory))
            {
                foreach (var entry in Directory.EnumerateFileSystemEntries(CacheDirectory))
                {
                    if (Directory.Exists(entry))
                    {
                        Directory.Delete(entry, recursive: true);
                    }
                    else
                    {
                        File.Delete(entry);
                    }
                }
            }
            Directory.CreateDirectory(CacheDirectory);
            error = "";
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    internal long CacheSizeBytes()
    {
        try
        {
            return Directory.Exists(CacheDirectory)
                ? Directory.EnumerateFiles(CacheDirectory, "*", SearchOption.AllDirectories)
                    .Sum(path => new FileInfo(path).Length)
                : 0;
        }
        catch
        {
            return -1;
        }
    }

    internal long BackupSizeBytes()
    {
        try
        {
            return Directory.Exists(BackupDirectory)
                ? Directory.EnumerateFiles(BackupDirectory, "*", SearchOption.AllDirectories)
                    .Sum(path => new FileInfo(path).Length)
                : 0;
        }
        catch
        {
            return -1;
        }
    }

    private static StorageConfiguration CreateDefaults(
        string appDirectory,
        string localAppDataDirectory,
        string documentsDirectory,
        bool isPortable)
    {
        if (isPortable)
        {
            return new StorageConfiguration
            {
                DataDirectory = Path.Combine(appDirectory, "Data"),
                NotesDirectory = Path.Combine(appDirectory, "Notes"),
                CacheDirectory = Path.Combine(appDirectory, "Cache"),
                BackupDirectory = Path.Combine(appDirectory, "Backups")
            };
        }

        // Existing releases stored durable data beside the executable. Keep that location until
        // the user explicitly migrates it, so an upgrade can never appear to lose all papers.
        var hasLegacyData = File.Exists(Path.Combine(appDirectory, "data.json")) ||
                            File.Exists(Path.Combine(appDirectory, "data.backup.json")) ||
                            File.Exists(Path.Combine(appDirectory, "note-assets.lmdb")) ||
                            Directory.Exists(Path.Combine(appDirectory, "plugins", "data"));
        return new StorageConfiguration
        {
            DataDirectory = hasLegacyData
                ? appDirectory
                : Path.Combine(localAppDataDirectory, ProductIdentity.Name, "Data"),
            NotesDirectory = Path.Combine(documentsDirectory, "PaperNook Notes"),
            CacheDirectory = Path.Combine(localAppDataDirectory, ProductIdentity.Name, "Cache"),
            BackupDirectory = Path.Combine(documentsDirectory, "PaperNook Backups")
        };
    }

    private static StorageConfiguration? TryCreateLegacyImport(
        string appDirectory,
        string localAppDataDirectory,
        string documentsDirectory)
    {
        var legacyRoot = Path.Combine(localAppDataDirectory, ProductIdentity.LegacyName);
        var legacyBackupDefault = Path.Combine(documentsDirectory, "PaperTodo Backups");
        var legacy = TryReadConfiguration(
            Path.Combine(legacyRoot, "storage.json"),
            defaultBackupDirectory: legacyBackupDefault)
            ?? TryReadConfiguration(
                Path.Combine(legacyRoot, "storage.backup.json"),
                defaultBackupDirectory: legacyBackupDefault);
        if (legacy == null) return null;

        var defaults = CreateDefaults(appDirectory, localAppDataDirectory, documentsDirectory, isPortable: false);
        legacy.PendingDataDirectory = defaults.DataDirectory;
        legacy.PendingNotesDirectory = defaults.NotesDirectory;
        legacy.PendingCacheDirectory = defaults.CacheDirectory;
        legacy.PendingBackupDirectory = defaults.BackupDirectory;
        return legacy;
    }

    private static StorageConfiguration? TryReadConfiguration(
        string path,
        string? portableRoot = null,
        string? defaultBackupDirectory = null)
    {
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }
            var configuration = JsonSerializer.Deserialize<StorageConfiguration>(
                File.ReadAllText(path),
                JsonOptions);
            if (configuration == null || configuration.Version is < 1 or > ConfigurationVersion)
            {
                return null;
            }

            if (configuration.Version == 1)
            {
                configuration.Version = ConfigurationVersion;
                configuration.BackupDirectory = defaultBackupDirectory
                    ?? throw new InvalidDataException("Backup directory default is required for a version 1 configuration.");
            }

            configuration.DataDirectory = NormalizeConfiguredDirectory(
                configuration.DataDirectory,
                portableRoot);
            configuration.NotesDirectory = NormalizeConfiguredDirectory(
                configuration.NotesDirectory,
                portableRoot);
            configuration.CacheDirectory = NormalizeConfiguredDirectory(
                configuration.CacheDirectory,
                portableRoot);
            configuration.BackupDirectory = NormalizeConfiguredDirectory(
                configuration.BackupDirectory,
                portableRoot);
            configuration.PendingDataDirectory = NormalizeConfiguredOptionalDirectory(
                configuration.PendingDataDirectory,
                portableRoot);
            configuration.PendingNotesDirectory = NormalizeConfiguredOptionalDirectory(
                configuration.PendingNotesDirectory,
                portableRoot);
            configuration.PendingCacheDirectory = NormalizeConfiguredOptionalDirectory(
                configuration.PendingCacheDirectory,
                portableRoot);
            configuration.PendingBackupDirectory = NormalizeConfiguredOptionalDirectory(
                configuration.PendingBackupDirectory,
                portableRoot);
            ValidateAllDirectories(
                configuration.DataDirectory,
                configuration.NotesDirectory,
                configuration.CacheDirectory,
                configuration.BackupDirectory);
            return configuration;
        }
        catch
        {
            return null;
        }
    }

    private void ApplyPendingChanges()
    {
        var originalData = _configuration.DataDirectory;
        var originalNotes = _configuration.NotesDirectory;
        var originalCache = _configuration.CacheDirectory;
        var originalBackup = _configuration.BackupDirectory;
        try
        {
            var pendingData = NormalizeOptional(_configuration.PendingDataDirectory);
            var pendingNotes = NormalizeOptional(_configuration.PendingNotesDirectory);
            var pendingCache = NormalizeOptional(_configuration.PendingCacheDirectory);
            var pendingBackup = NormalizeOptional(_configuration.PendingBackupDirectory);
            ValidateAllDirectories(
                pendingData ?? DataDirectory,
                pendingNotes ?? NotesDirectory,
                pendingCache ?? CacheDirectory,
                pendingBackup ?? BackupDirectory);

            if (pendingData != null && !SamePath(pendingData, DataDirectory))
            {
                MigrateDurableData(DataDirectory, pendingData);
                _configuration.DataDirectory = pendingData;
            }
            if (pendingNotes != null && !SamePath(pendingNotes, NotesDirectory))
            {
                CopyDirectoryWithoutOverwrite(NotesDirectory, pendingNotes);
                _configuration.NotesDirectory = pendingNotes;
            }
            if (pendingCache != null)
            {
                _configuration.CacheDirectory = pendingCache;
            }
            if (pendingBackup != null)
            {
                CopyDirectoryWithoutOverwrite(BackupDirectory, pendingBackup);
                _configuration.BackupDirectory = pendingBackup;
            }

            _configuration.PendingDataDirectory = "";
            _configuration.PendingNotesDirectory = "";
            _configuration.PendingCacheDirectory = "";
            _configuration.PendingBackupDirectory = "";
            RefreshResolvedPaths();
        }
        catch (Exception ex)
        {
            _configuration.DataDirectory = originalData;
            _configuration.NotesDirectory = originalNotes;
            _configuration.CacheDirectory = originalCache;
            _configuration.BackupDirectory = originalBackup;
            StartupMigrationError = ex.Message;
            // Keep the active paths untouched. Pending values remain in storage.json so the UI
            // can show that the migration did not complete and the user can choose another path.
        }
    }

    private void MigrateDurableData(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (var pattern in new[]
                 {
                     "data.json",
                     "data.backup.json",
                     "data.failed_load.*.json",
                     "data.backup.used_for_recovery.*.json",
                     "note-assets.lmdb*"
                 })
        {
            if (!Directory.Exists(source))
            {
                break;
            }
            foreach (var file in Directory.EnumerateFiles(source, pattern, SearchOption.TopDirectoryOnly))
            {
                CopyFileVerified(file, Path.Combine(target, Path.GetFileName(file)));
            }
        }

        CopyDirectoryWithoutOverwrite(
            Path.Combine(source, "plugins", "data"),
            Path.Combine(target, "plugins", "data"));
    }

    private static void CopyDirectoryWithoutOverwrite(string source, string target)
    {
        if (!Directory.Exists(source) || SamePath(source, target))
        {
            return;
        }
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(source, file);
            CopyFileVerified(file, Path.Combine(target, relative));
        }
    }

    private static void CopyFileVerified(string source, string target)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        if (File.Exists(target))
        {
            if (FilesMatch(source, target))
            {
                return;
            }
            throw new IOException($"目标位置已存在不同内容：{target}");
        }

        var temporary = target + ".papertodo-migration.tmp";
        File.Copy(source, temporary, overwrite: true);
        if (!FilesMatch(source, temporary))
        {
            File.Delete(temporary);
            throw new IOException($"迁移校验失败：{source}");
        }
        File.Move(temporary, target);
    }

    private static bool FilesMatch(string left, string right)
    {
        var leftInfo = new FileInfo(left);
        var rightInfo = new FileInfo(right);
        if (leftInfo.Length != rightInfo.Length)
        {
            return false;
        }
        using var leftStream = File.OpenRead(left);
        using var rightStream = File.OpenRead(right);
        return SHA256.HashData(leftStream).SequenceEqual(SHA256.HashData(rightStream));
    }

    private void ValidateCandidate(AppStorageDirectoryKind kind, string candidate)
    {
        var active = kind switch
        {
            AppStorageDirectoryKind.Data => DataDirectory,
            AppStorageDirectoryKind.Notes => NotesDirectory,
            AppStorageDirectoryKind.Cache => CacheDirectory,
            AppStorageDirectoryKind.Backup => BackupDirectory,
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };
        if (!IsPortable && !SamePath(candidate, active) &&
            (SamePath(candidate, _appDirectory) || IsUnder(candidate, _appDirectory)))
        {
            throw new InvalidOperationException("普通模式不能把新位置设置为程序目录；如需便携模式，请创建 papernook.portable 标记后重启。");
        }
        var data = kind == AppStorageDirectoryKind.Data ? candidate : EffectiveDirectory(AppStorageDirectoryKind.Data);
        var notes = kind == AppStorageDirectoryKind.Notes ? candidate : EffectiveDirectory(AppStorageDirectoryKind.Notes);
        var cache = kind == AppStorageDirectoryKind.Cache ? candidate : EffectiveDirectory(AppStorageDirectoryKind.Cache);
        var backup = kind == AppStorageDirectoryKind.Backup ? candidate : EffectiveDirectory(AppStorageDirectoryKind.Backup);
        ValidateAllDirectories(data, notes, cache, backup);
    }

    private static void ValidateAllDirectories(string data, string notes, string cache, string backup)
    {
        data = NormalizeDirectory(data);
        notes = NormalizeDirectory(notes);
        cache = NormalizeDirectory(cache);
        backup = NormalizeDirectory(backup);
        foreach (var path in new[] { data, notes, cache, backup })
        {
            var root = Path.GetPathRoot(path);
            if (string.IsNullOrWhiteSpace(root) || SamePath(path, root))
            {
                throw new InvalidOperationException("不能选择磁盘根目录。");
            }
        }
        EnsureNotNested(data, notes);
        EnsureNotNested(data, cache);
        EnsureNotNested(notes, cache);
        EnsureNotNested(data, backup);
        EnsureNotNested(notes, backup);
        EnsureNotNested(cache, backup);
    }

    private static void EnsureNotNested(string left, string right)
    {
        if (IsUnder(left, right) || IsUnder(right, left) || SamePath(left, right))
        {
            throw new InvalidOperationException("缓存、数据、笔记和备份目录不能相同或互相嵌套。");
        }
    }

    private static bool IsUnder(string path, string parent) =>
        path.StartsWith(parent.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase);

    private static void VerifyWritable(string directory)
    {
        Directory.CreateDirectory(directory);
        var probe = Path.Combine(directory, $".papernook-write-test-{Guid.NewGuid():N}");
        try
        {
            File.WriteAllText(probe, ProductIdentity.Name);
        }
        finally
        {
            File.Delete(probe);
        }
    }

    private void RefreshResolvedPaths()
    {
        DataDirectory = NormalizeDirectory(_configuration.DataDirectory);
        NotesDirectory = NormalizeDirectory(_configuration.NotesDirectory);
        CacheDirectory = NormalizeDirectory(_configuration.CacheDirectory);
        BackupDirectory = NormalizeDirectory(_configuration.BackupDirectory);
        ValidateAllDirectories(DataDirectory, NotesDirectory, CacheDirectory, BackupDirectory);
    }

    private void EnsureDirectories()
    {
        Directory.CreateDirectory(DataDirectory);
        Directory.CreateDirectory(NotesDirectory);
        Directory.CreateDirectory(CacheDirectory);
        Directory.CreateDirectory(BackupDirectory);
        Directory.CreateDirectory(PluginDataDirectory);
    }

    private void SaveConfiguration()
    {
        var directory = Path.GetDirectoryName(ConfigurationFilePath)!;
        Directory.CreateDirectory(directory);
        if (_preserveInvalidConfiguration && File.Exists(ConfigurationFilePath))
        {
            var failedPath = Path.Combine(
                directory,
                $"storage.failed_load.{DateTime.UtcNow:yyyyMMddHHmmssfff}.json");
            File.Copy(ConfigurationFilePath, failedPath, overwrite: false);
            _preserveInvalidConfiguration = false;
        }
        var temporary = ConfigurationFilePath + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(ConfigurationForSave(), JsonOptions));
        if (TryReadConfiguration(
                ConfigurationFilePath,
                IsPortable ? _appDirectory : null,
                BackupDirectory) != null)
        {
            File.Copy(
                ConfigurationFilePath,
                Path.Combine(directory, "storage.backup.json"),
                overwrite: true);
        }
        File.Move(temporary, ConfigurationFilePath, overwrite: true);
    }

    private void TrySaveConfiguration()
    {
        try
        {
            SaveConfiguration();
        }
        catch
        {
            // A read-only portable folder may still run from its resolved defaults. The settings
            // page will surface the write failure if the user later requests a path change.
        }
    }

    private StorageConfiguration ConfigurationForSave() => new()
    {
        Version = _configuration.Version,
        DataDirectory = ConfigurationPathForSave(_configuration.DataDirectory),
        NotesDirectory = ConfigurationPathForSave(_configuration.NotesDirectory),
        CacheDirectory = ConfigurationPathForSave(_configuration.CacheDirectory),
        BackupDirectory = ConfigurationPathForSave(_configuration.BackupDirectory),
        PendingDataDirectory = ConfigurationPathForSave(_configuration.PendingDataDirectory),
        PendingNotesDirectory = ConfigurationPathForSave(_configuration.PendingNotesDirectory),
        PendingCacheDirectory = ConfigurationPathForSave(_configuration.PendingCacheDirectory),
        PendingBackupDirectory = ConfigurationPathForSave(_configuration.PendingBackupDirectory)
    };

    private string ConfigurationPathForSave(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return "";
        }
        var normalized = NormalizeDirectory(path);
        return IsPortable && (SamePath(normalized, _appDirectory) || IsUnder(normalized, _appDirectory))
            ? Path.GetRelativePath(_appDirectory, normalized)
            : normalized;
    }

    private static string PendingOrCurrent(string pending, string current) =>
        NormalizeOptional(pending) ?? current;

    private static string? NormalizeOptional(string? path) =>
        string.IsNullOrWhiteSpace(path) ? null : NormalizeDirectory(path);

    private static string NormalizeConfiguredDirectory(string path, string? portableRoot) =>
        portableRoot != null && !Path.IsPathFullyQualified(path)
            ? NormalizeDirectory(Path.Combine(portableRoot, path))
            : NormalizeDirectory(path);

    private static string NormalizeConfiguredOptionalDirectory(string? path, string? portableRoot) =>
        string.IsNullOrWhiteSpace(path) ? "" : NormalizeConfiguredDirectory(path, portableRoot);

    private static string NormalizeDirectory(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return Path.GetFullPath(Environment.ExpandEnvironmentVariables(path))
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    private static bool SamePath(string left, string right) =>
        string.Equals(
            NormalizeDirectory(left),
            NormalizeDirectory(right),
            StringComparison.OrdinalIgnoreCase);
}
