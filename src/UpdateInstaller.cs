using System.IO;
using System.Security.Cryptography;

namespace PaperTodo;

internal sealed record PreparedUpdateApply(string CoordinatorPath, string TargetPath, string Nonce);

internal sealed class UpdateInstaller(string cacheDirectory, UpdateStateStore stateStore)
{
    private readonly string _cacheDirectory = Path.GetFullPath(cacheDirectory);

    internal PreparedUpdateApply Prepare(VerifiedUpdate update, string targetPath)
    {
        ArgumentNullException.ThrowIfNull(update);
        UpdateApplyMode.ValidateTargetPath(targetPath);
        targetPath = Path.GetFullPath(targetPath);
        if (!File.Exists(targetPath)) throw new FileNotFoundException("PaperNook executable is missing.", targetPath);

        var versionDirectory = Path.Combine(_cacheDirectory, "Updates", update.Asset.Version.ToString());
        Directory.CreateDirectory(versionDirectory);
        var coordinatorPath = Path.Combine(versionDirectory, "PaperNook.update.exe");
        CopyDurable(update.DownloadPath, coordinatorPath);
        if (new FileInfo(coordinatorPath).Length != update.Asset.Length ||
            !string.Equals(HashFile(coordinatorPath), update.Asset.Sha256, StringComparison.Ordinal))
            throw new InvalidDataException("Prepared update coordinator failed verification.");

        var nonce = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
        stateStore.RegisterPendingApply(update.Asset, coordinatorPath, targetPath, nonce);
        return new PreparedUpdateApply(coordinatorPath, targetPath, nonce);
    }

    private static void CopyDurable(string source, string destination)
    {
        using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.SequentialScan);
        using var output = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.WriteThrough);
        input.CopyTo(output);
        output.Flush(flushToDisk: true);
    }

    private static string HashFile(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }
}
