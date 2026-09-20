using System.IO;
using System.Diagnostics;
using System.Security.Cryptography;

namespace PaperTodo;

internal static class UpdateApplyMode
{
    internal static bool IsRequested(IReadOnlyList<string> args) =>
        args.Count > 0 && string.Equals(args[0], "--apply-update", StringComparison.Ordinal);

    internal static async Task<bool> TryRunAsync(IReadOnlyList<string> args)
    {
        if (!IsRequested(args)) return false;
        if (args.Count != 4 || !int.TryParse(args[2], out var parentPid) || !IsValidNonce(args[3]))
        {
            Environment.ExitCode = 2;
            return true;
        }

        try
        {
            var processPath = Environment.ProcessPath
                ?? throw new InvalidOperationException("Update coordinator path is unavailable.");
            var versionDirectory = Path.GetDirectoryName(processPath)
                ?? throw new InvalidDataException("Update coordinator directory is invalid.");
            var updatesDirectory = Directory.GetParent(versionDirectory)?.FullName
                ?? throw new InvalidDataException("Updates directory is invalid.");
            if (!string.Equals(Path.GetFileName(updatesDirectory), "Updates", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Update coordinator is outside the Updates directory.");
            var cacheDirectory = Directory.GetParent(updatesDirectory)?.FullName
                ?? throw new InvalidDataException("Cache directory is invalid.");
            var store = new UpdateStateStore(cacheDirectory, DurableAtomicFileWriter.Shared);
            var targetPath = Path.GetFullPath(args[1]);
            ValidateTargetPath(targetPath);
            if (!store.TryAuthorizeApply(args[3], targetPath, out var pending) ||
                !string.Equals(Path.GetFullPath(pending.SourcePath), Path.GetFullPath(processPath), StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Update apply request is not authorized.");

            var signature = new AuthenticodeVerifier().Verify(processPath, ReleaseTrust.Current);
            if (!signature.IsTrusted || PaperTodoVersion.Parse(signature.ProductVersion) != PaperTodoVersion.Parse(pending.Version))
                throw new InvalidDataException("Update coordinator signature or version is not trusted.");

            await UpdateApplyEngine.ApplyAsync(
                store,
                pending,
                parentPid,
                launchUpdatedTarget: path => LaunchUpdatedTarget(path, args[3]),
                isLaunchHealthy: () => store.Load().PendingApply?.HealthyUtc != null,
                healthTimeout: TimeSpan.FromSeconds(30),
                stageReached: null,
                CancellationToken.None).ConfigureAwait(false);

            var final = store.Load().PendingApply;
            if (final != null && string.Equals(final.Stage, UpdateApplyStage.RolledBack.ToString(), StringComparison.Ordinal))
                LaunchNormally(targetPath);
            Environment.ExitCode = 0;
        }
        catch (Exception ex)
        {
            try { Console.Error.WriteLine($"PaperNook update failed: {ex.GetBaseException().Message}"); }
            catch { }
            Environment.ExitCode = 1;
        }
        return true;
    }

    internal static void ValidateTargetPath(string targetPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetPath);
        if (!Path.IsPathFullyQualified(targetPath))
            throw new InvalidDataException("Update target path must be absolute.");
        if (targetPath.StartsWith(@"\\", StringComparison.Ordinal))
            throw new InvalidDataException("Update target path cannot be UNC.");

        var fullPath = Path.GetFullPath(targetPath);
        var root = Path.GetPathRoot(fullPath);
        if (string.IsNullOrWhiteSpace(root) ||
            string.Equals(Path.TrimEndingDirectorySeparator(fullPath), Path.TrimEndingDirectorySeparator(root), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Update target path cannot be a volume root.");
        if (!string.Equals(Path.GetExtension(fullPath), ".exe", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Update target must be an executable.");
        if (!string.Equals(Path.GetFileName(fullPath), "PaperNook.exe", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Update target must be PaperNook.exe.");

        var cursor = File.Exists(fullPath) ? fullPath : Path.GetDirectoryName(fullPath);
        while (!string.IsNullOrWhiteSpace(cursor))
        {
            if ((File.GetAttributes(cursor) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Update target cannot pass through a reparse point.");
            var parent = Path.GetDirectoryName(cursor);
            if (string.IsNullOrWhiteSpace(parent) || string.Equals(parent, cursor, StringComparison.OrdinalIgnoreCase)) break;
            cursor = parent;
        }
    }

    private static bool LaunchUpdatedTarget(string path, string nonce)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = path,
            WorkingDirectory = Path.GetDirectoryName(path) ?? AppContext.BaseDirectory,
            UseShellExecute = false
        };
        startInfo.ArgumentList.Add("--update-health");
        startInfo.ArgumentList.Add(nonce);
        return Process.Start(startInfo) != null;
    }

    private static bool LaunchNormally(string path) => Process.Start(new ProcessStartInfo
    {
        FileName = path,
        WorkingDirectory = Path.GetDirectoryName(path) ?? AppContext.BaseDirectory,
        UseShellExecute = true
    }) != null;

    private static bool IsValidNonce(string value) =>
        value.Length is >= 32 and <= 128 && value.All(static character =>
            character is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F');
}

internal enum UpdateApplyStage
{
    Registered,
    WaitingParent,
    PreviousCopied,
    TargetTempWritten,
    TargetReplaced,
    NewLaunched,
    HealthTimeout,
    Healthy,
    RolledBack
}

internal static class UpdateApplyEngine
{
    private const string TargetTempSuffix = ".papertodo.update.tmp";

    internal static async Task ApplyAsync(
        UpdateStateStore store,
        PendingUpdateApply pending,
        int parentPid,
        Func<string, bool> launchUpdatedTarget,
        Func<bool> isLaunchHealthy,
        TimeSpan healthTimeout,
        Action<UpdateApplyStage>? stageReached,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(pending);
        ArgumentNullException.ThrowIfNull(launchUpdatedTarget);
        ArgumentNullException.ThrowIfNull(isLaunchHealthy);
        UpdateApplyMode.ValidateTargetPath(pending.TargetPath);
        VerifyFile(pending.SourcePath, pending.Length, pending.Sha256, "verified update");

        SetStage(store, pending, UpdateApplyStage.WaitingParent, stageReached);
        await WaitForParentAsync(parentPid, cancellationToken).ConfigureAwait(false);

        var updatesDirectory = Path.GetDirectoryName(store.FilePath)
            ?? throw new InvalidDataException("Update state directory is invalid.");
        var rollbackDirectory = Path.Combine(updatesDirectory, "rollback");
        Directory.CreateDirectory(rollbackDirectory);
        pending.PreviousPath = Path.Combine(rollbackDirectory, "PaperNook.previous.exe");
        pending.FailedPath = Path.Combine(rollbackDirectory, "PaperNook.failed.exe");
        if (!File.Exists(pending.TargetPath)) throw new FileNotFoundException("Current PaperNook executable is missing.", pending.TargetPath);
        pending.PreviousLength = new FileInfo(pending.TargetPath).Length;
        pending.PreviousSha256 = HashFile(pending.TargetPath);
        CopyDurable(pending.TargetPath, pending.PreviousPath);
        VerifyFile(pending.PreviousPath, pending.PreviousLength, pending.PreviousSha256, "rollback executable");
        store.UpdatePending(value =>
        {
            value.PreviousPath = pending.PreviousPath;
            value.FailedPath = pending.FailedPath;
            value.PreviousLength = pending.PreviousLength;
            value.PreviousSha256 = pending.PreviousSha256;
        });
        SetStage(store, pending, UpdateApplyStage.PreviousCopied, stageReached);

        var targetTemp = pending.TargetPath + TargetTempSuffix;
        CopyDurable(pending.SourcePath, targetTemp);
        VerifyFile(targetTemp, pending.Length, pending.Sha256, "target temp executable");
        SetStage(store, pending, UpdateApplyStage.TargetTempWritten, stageReached);

        File.Move(targetTemp, pending.TargetPath, overwrite: true);
        VerifyFile(pending.TargetPath, pending.Length, pending.Sha256, "updated executable");
        SetStage(store, pending, UpdateApplyStage.TargetReplaced, stageReached);

        if (!launchUpdatedTarget(pending.TargetPath))
            throw new InvalidOperationException("Updated PaperNook could not be launched.");
        SetStage(store, pending, UpdateApplyStage.NewLaunched, stageReached);

        var deadline = DateTimeOffset.UtcNow + healthTimeout;
        do
        {
            if (isLaunchHealthy())
            {
                store.UpdatePending(value => value.HealthyUtc = DateTimeOffset.UtcNow);
                SetStage(store, pending, UpdateApplyStage.Healthy, stageReached);
                store.ClearPending();
                return;
            }
            if (healthTimeout <= TimeSpan.Zero) break;
            await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken).ConfigureAwait(false);
        }
        while (DateTimeOffset.UtcNow < deadline);

        SetStage(store, pending, UpdateApplyStage.HealthTimeout, stageReached);
        RecoverInterruptedApply(store);
    }

    internal static void RecoverInterruptedApply(UpdateStateStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        var state = store.Load();
        var pending = state.PendingApply;
        if (pending == null) return;
        if (!Enum.TryParse<UpdateApplyStage>(pending.Stage, ignoreCase: true, out var stage))
            throw new InvalidDataException("Update journal stage is invalid.");

        var targetTemp = pending.TargetPath + TargetTempSuffix;
        TryDelete(targetTemp);
        if (stage < UpdateApplyStage.TargetReplaced || stage is UpdateApplyStage.Healthy or UpdateApplyStage.RolledBack)
            return;
        if (pending.HealthyUtc.HasValue) return;
        if (string.IsNullOrWhiteSpace(pending.PreviousPath) || string.IsNullOrWhiteSpace(pending.PreviousSha256))
            throw new InvalidDataException("Rollback journal is incomplete.");

        VerifyFile(pending.PreviousPath, pending.PreviousLength, pending.PreviousSha256, "rollback executable");
        if (File.Exists(pending.TargetPath) && !string.IsNullOrWhiteSpace(pending.FailedPath))
        {
            CopyDurable(pending.TargetPath, pending.FailedPath);
        }
        var rollbackTemp = pending.TargetPath + ".papertodo.rollback.tmp";
        CopyDurable(pending.PreviousPath, rollbackTemp);
        VerifyFile(rollbackTemp, pending.PreviousLength, pending.PreviousSha256, "rollback temp executable");
        File.Move(rollbackTemp, pending.TargetPath, overwrite: true);
        VerifyFile(pending.TargetPath, pending.PreviousLength, pending.PreviousSha256, "restored executable");
        SetStage(store, pending, UpdateApplyStage.RolledBack, stageReached: null);
    }

    private static async Task WaitForParentAsync(int parentPid, CancellationToken cancellationToken)
    {
        if (parentPid <= 0) return;
        try
        {
            using var parent = Process.GetProcessById(parentPid);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            await parent.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (ArgumentException)
        {
            // The parent already exited.
        }
    }

    private static void SetStage(
        UpdateStateStore store,
        PendingUpdateApply pending,
        UpdateApplyStage stage,
        Action<UpdateApplyStage>? stageReached)
    {
        pending.Stage = stage.ToString();
        store.UpdatePending(value => value.Stage = pending.Stage);
        stageReached?.Invoke(stage);
    }

    private static void CopyDurable(string source, string destination)
    {
        var directory = Path.GetDirectoryName(destination);
        if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
        using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.SequentialScan);
        using var output = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.WriteThrough);
        input.CopyTo(output);
        output.Flush(flushToDisk: true);
    }

    private static void VerifyFile(string path, long expectedLength, string expectedSha256, string label)
    {
        if (!File.Exists(path)) throw new InvalidDataException($"{label} is missing.");
        if (new FileInfo(path).Length != expectedLength) throw new InvalidDataException($"{label} length changed.");
        if (!string.Equals(HashFile(path), expectedSha256, StringComparison.Ordinal))
            throw new InvalidDataException($"{label} hash changed.");
    }

    private static string HashFile(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch { }
    }
}
