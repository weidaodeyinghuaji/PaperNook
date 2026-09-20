using System.IO;
using System.Text.Json;

namespace PaperTodo;

internal enum RestoreJournalStage
{
    Staged,
    Switched
}

internal sealed record PendingRestoreJournal(
    int Version,
    string Nonce,
    string BackupPath,
    string TargetDataDirectory,
    string StagingDirectory,
    string BeforeRestoreDirectory,
    RestoreJournalStage Stage,
    bool StartupAttempted,
    DateTimeOffset CreatedUtc)
{
    internal const int CurrentVersion = 1;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerOptions.Strict)
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    internal static string PathFor(string dataDirectory) =>
        Path.Combine(
            Path.GetDirectoryName(Path.GetFullPath(dataDirectory))!,
            $".{Path.GetFileName(dataDirectory)}.pending-restore.json");

    internal static PendingRestoreJournal? TryLoad(string dataDirectory)
    {
        var path = PathFor(dataDirectory);
        if (!File.Exists(path))
        {
            return null;
        }
        var journal = JsonSerializer.Deserialize<PendingRestoreJournal>(File.ReadAllText(path), JsonOptions)
            ?? throw new InvalidDataException("Pending restore journal is empty.");
        if (journal.Version != CurrentVersion || string.IsNullOrWhiteSpace(journal.Nonce))
        {
            throw new InvalidDataException("Pending restore journal is incompatible.");
        }
        return journal;
    }

    internal void Save(IDurableAtomicFileWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.Write(PathFor(TargetDataDirectory), JsonSerializer.SerializeToUtf8Bytes(this, JsonOptions));
    }
}
