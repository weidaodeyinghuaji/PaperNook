using System.Text;
using PaperTodo;

var options = Parse(args);
var scenario = Required(options, "scenario");
var stage = Required(options, "stage");
var workspace = Path.GetFullPath(Required(options, "workspace"));
Directory.CreateDirectory(workspace);

switch (scenario)
{
    case "state-save":
    case "storage-config":
    case "backup-create":
        RunAtomicWrite(scenario, stage, workspace);
        break;
    case "restore-switch":
        RunDirectorySwitch(stage, workspace);
        break;
    case "update-replace":
        RunExecutableSwitch(stage, workspace);
        break;
    case "plugin-health":
        RunPluginHealth(stage, workspace);
        break;
    default:
        throw new ArgumentException($"Unknown scenario: {scenario}.");
}

static void RunAtomicWrite(string scenario, string requestedStage, string workspace)
{
    var target = Path.Combine(workspace, scenario == "backup-create" ? "visible.papernook-backup" : "primary.json");
    File.WriteAllText(target, "old", Encoding.UTF8);
    var stage = Enum.Parse<DurableAtomicWriteStage>(requestedStage, ignoreCase: true);
    var writer = new DurableAtomicFileWriter((current, _) =>
    {
        if (current == stage) Barrier(workspace, current.ToString());
    });
    writer.Write(target, Encoding.UTF8.GetBytes("new"));
}

static void RunDirectorySwitch(string requestedStage, string workspace)
{
    var target = Path.Combine(workspace, "Data");
    var staging = Path.Combine(workspace, "Data.restore-staging");
    var before = Path.Combine(workspace, "Data.before-restore");
    Directory.CreateDirectory(target);
    Directory.CreateDirectory(staging);
    File.WriteAllText(Path.Combine(target, "generation.txt"), "old");
    File.WriteAllText(Path.Combine(staging, "generation.txt"), "new");
    MaybeBarrier("BeforeOldMove");
    Directory.Move(target, before);
    MaybeBarrier("AfterOldMove");
    Directory.Move(staging, target);
    MaybeBarrier("AfterNewMove");
    return;

    void MaybeBarrier(string stage)
    {
        if (string.Equals(requestedStage, stage, StringComparison.OrdinalIgnoreCase)) Barrier(workspace, stage);
    }
}

static void RunExecutableSwitch(string requestedStage, string workspace)
{
    var target = Path.Combine(workspace, "PaperNook.exe");
    var pending = Path.Combine(workspace, "PaperNook.exe.pending");
    var previous = Path.Combine(workspace, "PaperNook.exe.previous");
    File.WriteAllText(target, "old");
    File.WriteAllText(pending, "new");
    MaybeBarrier("BeforeOldMove");
    File.Move(target, previous);
    MaybeBarrier("AfterOldMove");
    File.Move(pending, target);
    MaybeBarrier("AfterNewMove");
    return;

    void MaybeBarrier(string stage)
    {
        if (string.Equals(requestedStage, stage, StringComparison.OrdinalIgnoreCase)) Barrier(workspace, stage);
    }
}

static void RunPluginHealth(string requestedStage, string workspace)
{
    var store = new PluginStartupHealthStore(workspace);
    _ = store.BeginRun();
    var stage = Enum.Parse<PluginStartupStage>(requestedStage, ignoreCase: true);
    store.EnterStage(stage, stage == PluginStartupStage.Running ? "" : "crash.sample", "fingerprint");
    Barrier(workspace, stage.ToString());
}

static void Barrier(string workspace, string stage)
{
    File.WriteAllText(Path.Combine(workspace, "barrier.ready"), stage);
    using var wait = new ManualResetEventSlim(false);
    wait.Wait();
}

static Dictionary<string, string> Parse(string[] args)
{
    var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    for (var index = 0; index + 1 < args.Length; index += 2)
    {
        result[args[index].TrimStart('-')] = args[index + 1];
    }
    return result;
}

static string Required(IReadOnlyDictionary<string, string> options, string key) =>
    options.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)
        ? value
        : throw new ArgumentException($"Missing --{key}.");
