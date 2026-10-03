using System.Diagnostics;
using System.IO;

namespace PaperTodo;

internal static class CodexSessionLauncher
{
    internal static bool IsValidSessionId(string value) => Guid.TryParse(value, out _);

    internal static void Launch(CodexSessionSummary session)
    {
        if (!IsValidSessionId(session.SessionId))
            throw new InvalidDataException("Codex session id is invalid.");
        var codex = CodexRateLimitReader.FindCodexBinary()
            ?? throw new FileNotFoundException("Codex CLI was not found on PATH.");
        var workingDirectory = Directory.Exists(session.WorkingDirectory)
            ? session.WorkingDirectory
            : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var terminal = FindWindowsTerminal();
        var start = new ProcessStartInfo
        {
            FileName = terminal ?? codex,
            UseShellExecute = true,
            WorkingDirectory = workingDirectory
        };
        if (terminal != null)
        {
            start.ArgumentList.Add("-d");
            start.ArgumentList.Add(workingDirectory);
            start.ArgumentList.Add(codex);
        }
        start.ArgumentList.Add("resume");
        start.ArgumentList.Add(session.SessionId);
        _ = Process.Start(start) ?? throw new InvalidOperationException("Could not launch the Codex session.");
    }

    private static string? FindWindowsTerminal()
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "where.exe",
                Arguments = "wt.exe",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                CreateNoWindow = true
            });
            if (process == null) return null;
            var path = process.StandardOutput.ReadLine();
            process.WaitForExit(2000);
            return !string.IsNullOrWhiteSpace(path) && File.Exists(path) ? path : null;
        }
        catch { return null; }
    }
}
