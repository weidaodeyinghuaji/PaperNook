using System.Diagnostics;
using System.Runtime.InteropServices;

var repo = Directory.GetCurrentDirectory();
var runtimeDirectory = RuntimeEnvironment.GetRuntimeDirectory();
var dotnet = Path.GetFullPath(Path.Combine(runtimeDirectory, "..", "..", "..", "dotnet.exe"));
if (!File.Exists(dotnet)) dotnet = "dotnet";
var harness = Path.Combine(repo, "tools", "PaperTodo.CrashHarness", "bin", "Release", "net10.0-windows10.0.17763.0", "PaperTodo.CrashHarness.dll");
if (!File.Exists(harness)) throw new FileNotFoundException("Crash harness was not built.", harness);

var cases = new List<(string Scenario, string Stage)>
{
    ("state-save", "BeforeTempOpen"),
    ("state-save", "AfterTempWrite"),
    ("state-save", "AfterFlush"),
    ("state-save", "BeforeReplace"),
    ("storage-config", "AfterFlush"),
    ("backup-create", "BeforeReplace"),
    ("restore-switch", "BeforeOldMove"),
    ("restore-switch", "AfterOldMove"),
    ("restore-switch", "AfterNewMove"),
    ("update-replace", "BeforeOldMove"),
    ("update-replace", "AfterOldMove"),
    ("update-replace", "AfterNewMove"),
    ("plugin-health", "Running"),
    ("plugin-health", "DiscoveringPlugins")
};

var failures = 0;
foreach (var test in cases)
{
    var workspace = Path.Combine(Path.GetTempPath(), $"PaperTodo-crash-{test.Scenario}-{test.Stage}-{Guid.NewGuid():N}");
    Directory.CreateDirectory(workspace);
    Process? process = null;
    try
    {
        process = Process.Start(new ProcessStartInfo
        {
            FileName = dotnet,
            ArgumentList =
            {
                harness,
                "--scenario", test.Scenario,
                "--stage", test.Stage,
                "--workspace", workspace
            },
            UseShellExecute = false,
            CreateNoWindow = true
        }) ?? throw new InvalidOperationException("Could not start crash harness.");
        var marker = Path.Combine(workspace, "barrier.ready");
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!File.Exists(marker) && DateTime.UtcNow < deadline)
        {
            if (process.HasExited) throw new InvalidOperationException($"Harness exited early: {process.ExitCode}");
            Thread.Sleep(20);
        }
        if (!File.Exists(marker)) throw new TimeoutException("Crash barrier was not reached.");
        process.Kill(entireProcessTree: true);
        process.WaitForExit(5000);
        RecoverAndAssert(test.Scenario, test.Stage, workspace);
        Console.WriteLine($"PASS {test.Scenario}/{test.Stage}");
        Directory.Delete(workspace, recursive: true);
    }
    catch (Exception ex)
    {
        failures++;
        Console.Error.WriteLine($"FAIL {test.Scenario}/{test.Stage}: {ex.Message}; workspace={workspace}");
    }
    finally
    {
        if (process is { HasExited: false })
        {
            try { process.Kill(entireProcessTree: true); } catch { }
        }
        process?.Dispose();
    }
}

Console.WriteLine($"Crash recovery checks: {cases.Count - failures}/{cases.Count} passed");
return failures == 0 ? 0 : 1;

static void RecoverAndAssert(string scenario, string stage, string workspace)
{
    if (scenario is "state-save" or "storage-config" or "backup-create")
    {
        var target = Path.Combine(workspace, scenario == "backup-create" ? "visible.papernook-backup" : "primary.json");
        Assert(File.Exists(target), "visible target disappeared");
        Assert(File.ReadAllText(target) is "old" or "new", "visible target is partial");
        return;
    }
    if (scenario == "restore-switch")
    {
        RecoverMove(workspace, "Data", "Data.before-restore");
        Assert(File.Exists(Path.Combine(workspace, "Data", "generation.txt")), "no readable Data generation remains");
        return;
    }
    if (scenario == "update-replace")
    {
        var target = Path.Combine(workspace, "PaperNook.exe");
        var previous = Path.Combine(workspace, "PaperNook.exe.previous");
        if (!File.Exists(target) && File.Exists(previous)) File.Move(previous, target);
        Assert(File.Exists(target) && File.ReadAllText(target) is "old" or "new", "no complete executable remains");
        return;
    }
    if (scenario == "plugin-health")
    {
        var json = File.ReadAllText(Path.Combine(workspace, "startup-health.json"));
        if (stage == "Running") Assert(json.Contains("\"stage\": \"running\"", StringComparison.Ordinal), "running power loss was reclassified");
        else Assert(json.Contains("discoveringPlugins", StringComparison.Ordinal), "plugin startup stage was not durable");
    }
}

static void RecoverMove(string root, string targetName, string previousName)
{
    var target = Path.Combine(root, targetName);
    var previous = Path.Combine(root, previousName);
    if (!Directory.Exists(target) && Directory.Exists(previous)) Directory.Move(previous, target);
}

static void Assert(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}
