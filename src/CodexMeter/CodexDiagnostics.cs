using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.RegularExpressions;

namespace PaperTodo;

internal static partial class CodexComponentInventory
{
    internal static CodexComponentSummary Scan(string? codexHome = null)
    {
        var root = codexHome ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");
        var skills = NamesFromFiles(Path.Combine(root, "skills"), "SKILL.md");
        var plugins = NamesFromFiles(Path.Combine(root, "plugins"), "plugin.json");
        var mcp = new SortedSet<string>(StringComparer.CurrentCultureIgnoreCase);
        var config = Path.Combine(root, "config.toml");
        try
        {
            foreach (var line in File.ReadLines(config))
            {
                var match = McpHeader().Match(line.Trim());
                if (match.Success) mcp.Add(match.Groups[1].Value.Trim('"', '\''));
            }
        }
        catch { }
        return new CodexComponentSummary
        {
            SkillCount = skills.Count,
            PluginCount = plugins.Count,
            McpServerCount = mcp.Count,
            Skills = skills.Take(12).ToArray(),
            Plugins = plugins.Take(12).ToArray(),
            McpServers = mcp.Take(12).ToArray()
        };
    }

    private static SortedSet<string> NamesFromFiles(string root, string fileName)
    {
        var names = new SortedSet<string>(StringComparer.CurrentCultureIgnoreCase);
        try
        {
            if (!Directory.Exists(root)) return names;
            foreach (var path in Directory.EnumerateFiles(root, fileName, SearchOption.AllDirectories))
            {
                var directory = Path.GetDirectoryName(path);
                var name = directory == null ? "" : Path.GetFileName(directory);
                if (!string.IsNullOrWhiteSpace(name)) names.Add(name);
            }
        }
        catch { }
        return names;
    }

    [GeneratedRegex("^\\[mcp_servers\\.([^\\]]+)\\]$", RegexOptions.CultureInvariant)]
    private static partial Regex McpHeader();
}

internal static class CodexNetworkDiagnostics
{
    private static readonly (string Name, Uri Uri)[] Targets =
    [
        ("ChatGPT", new Uri("https://chatgpt.com/")),
        ("OpenAI API", new Uri("https://api.openai.com/v1/models"))
    ];

    internal static async Task<CodexNetworkSnapshot> CheckAsync(CancellationToken cancellationToken)
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("PaperNook-CodexMeter/1.0");
        var probes = new List<CodexNetworkProbe>();
        foreach (var target in Targets)
        {
            var started = Stopwatch.GetTimestamp();
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, target.Uri);
                using var response = await client.SendAsync(
                    request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
                probes.Add(new CodexNetworkProbe
                {
                    Name = target.Name,
                    Reachable = true,
                    StatusCode = (int)response.StatusCode,
                    LatencyMilliseconds = (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds
                });
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                probes.Add(new CodexNetworkProbe
                {
                    Name = target.Name,
                    Reachable = false,
                    LatencyMilliseconds = (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds,
                    Error = ex.GetBaseException().Message
                });
            }
        }
        return new CodexNetworkSnapshot { CheckedAt = DateTimeOffset.UtcNow, Probes = probes };
    }
}
