using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using PaperTodo;

internal static partial class Program
{
    private static void CheckLongSoak(string temp, string[] args)
    {
        string? Option(string name)
        {
            var index = Array.IndexOf(args, name);
            return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
        }
        var seconds = double.Parse(Option("--soak-seconds") ?? "28800", System.Globalization.CultureInfo.InvariantCulture);
        if (!double.IsFinite(seconds) || seconds < 20 || seconds > 86400) throw new ArgumentException("Invalid soak duration.");
        var report = Path.GetFullPath(Option("--report") ?? throw new ArgumentException("--report is required."));
        if (File.Exists(report) || File.Exists(report + ".samples.jsonl")) throw new IOException("Refusing to overwrite soak evidence.");
        Directory.CreateDirectory(Path.GetDirectoryName(report)!);
        var state = Fixture(); state.Papers[0].Items.Clear();
        for (var i = 0; i < 1500; i++) state.Papers[0].Items.Add(new() {
            Text = "长期验收任务 " + i, Id = "long-" + i, Planning = new() {
                PlannedDate = DateOnly.FromDateTime(DateTime.Now).AddDays(i % 7), Priority = i % 4,
                ListId = i % 3 == 0 ? "work" : null } });
        var store = new StateStore(Path.Combine(temp, "long-soak"), DurableAtomicFileWriter.Shared);
        var controller = Controller(state, store);
        var window = new PlannerWindow(controller, "inbox") { ShowActivated = false, ShowInTaskbar = false,
            Left = -10000, Top = -10000, Width = 1080, Height = 700 };
        var started = DateTimeOffset.UtcNow; var clock = Stopwatch.StartNew();
        using var process = Process.GetCurrentProcess();
        var latency = new List<double>(); var baselines = new List<long>();
        var samples = 0; var switches = 0; var edits = 0; var peakRows = 0;
        long baselineBytes = 0, latestBytes = 0; var cpuSeconds = 0d;
        var version = typeof(AppController).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        void WriteReport(string status, string? error = null)
        {
            var sorted = latency.Order().ToArray();
            File.WriteAllText(report, JsonSerializer.Serialize(new {
                status, scope = "isolated WPF planner; synthetic data; not complete application or input-to-photon timing",
                pid = Environment.ProcessId, version, startedUtc = started, updatedUtc = DateTimeOffset.UtcNow,
                expectedEndUtc = started.AddSeconds(seconds), elapsedSeconds = clock.Elapsed.TotalSeconds,
                requestedSeconds = seconds, switches, edits, samples, peakRows, baselinePrivateBytes = baselineBytes,
                privateBytes = latestBytes, cpuSeconds,
                latencyWindowSamples = sorted.Length,
                layoutP95Ms = sorted.Length == 0 ? 0 : sorted[(int)((sorted.Length - 1) * .95)], error
            }));
        }
        void Pump(TimeSpan delay)
        {
            var frame = new DispatcherFrame();
            var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = delay };
            timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
            timer.Start(); Dispatcher.PushFrame(frame);
        }
        try
        {
            window.Show(); DrainDispatcher(); WriteReport("running");
            var navigate = typeof(PlannerWindow).GetMethod("Navigate", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var views = new[] { "today", "tomorrow", "inbox", "list", "all", "week" };
            var lastTick = clock.Elapsed; var lastSample = TimeSpan.FromSeconds(-30);
            while (clock.Elapsed.TotalSeconds < seconds)
            {
                if (File.Exists(report + ".cancel")) { WriteReport("cancelled"); return; }
                if (clock.Elapsed - lastTick > TimeSpan.FromSeconds(15))
                    throw new InvalidOperationException("Host suspension or dispatcher stall interrupted continuous verification.");
                lastTick = clock.Elapsed;
                ((TextBox)Field(window, "_search")).Text = switches % 5 == 0 ? "任务 1" : "";
                var watch = Stopwatch.StartNew();
                var view = views[switches % views.Length];
                navigate.Invoke(window, [view, view == "list" ? "work" : null]);
                window.UpdateLayout(); DrainDispatcher();
                latency.Add(watch.Elapsed.TotalMilliseconds);
                if (latency.Count > 600) latency.RemoveAt(0);
                peakRows = Math.Max(peakRows, ((System.Collections.IDictionary)Field(window, "_rowStamps")).Count);
                Assert(peakRows < 500, "long-running virtual row cache stays bounded"); switches++;
                if (switches % 60 == 0)
                {
                    var task = state.Papers[0].Items[edits % 1500];
                    controller.PaperCommands.SetTaskDetails("inbox", task.Id, TaskPlanningRules.Version(task),
                        "长期保存 " + edits, task.Planning!, PaperOperationContext.User()); edits++;
                    Assert(store.Load().Papers[0].Items.Single(t => t.Id == task.Id).Text == task.Text,
                        "long-running edits remain durable");
                }
                if (clock.Elapsed - lastSample >= TimeSpan.FromSeconds(seconds < 120 ? 5 : 30))
                {
                    process.Refresh(); latestBytes = process.PrivateMemorySize64; cpuSeconds = process.TotalProcessorTime.TotalSeconds;
                    if (clock.Elapsed.TotalSeconds < Math.Min(120, seconds / 3)) baselines.Add(latestBytes);
                    baselineBytes = baselines.Count == 0 ? latestBytes : baselines.Order().ElementAt(baselines.Count / 2);
                    samples++;
                    File.AppendAllText(report + ".samples.jsonl", JsonSerializer.Serialize(new {
                        utc = DateTimeOffset.UtcNow, elapsedSeconds = clock.Elapsed.TotalSeconds, switches, edits,
                        privateBytes = latestBytes, managedBytes = GC.GetTotalMemory(false), cpuSeconds,
                        handles = process.HandleCount, peakRows, layoutMs = watch.Elapsed.TotalMilliseconds
                    }) + Environment.NewLine);
                    WriteReport("running"); lastSample = clock.Elapsed;
                }
                Pump(TimeSpan.FromSeconds(1));
            }
            Assert(latestBytes - baselineBytes < 128L * 1024 * 1024, "private memory growth stays below 128 MiB after warmup");
            Assert(controller.TryPrepareNormalExit(), "long-running final state saves successfully");
            WriteReport("passed"); Console.WriteLine("PASS long soak: " + report);
        }
        catch (Exception ex) { WriteReport("failed", ex.GetBaseException().Message); throw; }
        finally
        {
            window.Close(); StopTimers(controller);
        }
    }
}
