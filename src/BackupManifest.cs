using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PaperTodo;

internal enum BackupKind
{
    Manual,
    Automatic
}

internal sealed record BackupManifestFile(
    string Path,
    long Length,
    string Sha256,
    string Role);

internal sealed record BackupManifest(
    int FormatVersion,
    string AppVersion,
    DateTimeOffset CreatedUtc,
    bool Portable,
    BackupKind Kind,
    IReadOnlyList<BackupManifestFile> Files)
{
    internal const int CurrentFormatVersion = 1;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerOptions.Strict)
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    internal byte[] Serialize() => JsonSerializer.SerializeToUtf8Bytes(this, JsonOptions);

    internal static BackupManifest Deserialize(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var manifest = JsonSerializer.Deserialize<BackupManifest>(stream, JsonOptions)
            ?? throw new InvalidDataException("Backup manifest is empty.");
        if (manifest.FormatVersion != CurrentFormatVersion)
        {
            throw new InvalidDataException($"Unsupported backup format version: {manifest.FormatVersion}.");
        }
        if (string.IsNullOrWhiteSpace(manifest.AppVersion) || manifest.Files == null)
        {
            throw new InvalidDataException("Backup manifest is incomplete.");
        }
        return manifest;
    }
}
