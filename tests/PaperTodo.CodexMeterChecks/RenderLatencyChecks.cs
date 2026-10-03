using System.IO;
using System.Text.Json;
using System.Threading.Channels;
using System.Windows;
using System.Windows.Threading;
using PaperTodo;

internal static class RenderLatencyChecks
{
    internal static Task RunAsync(bool live)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var window = new Window
            {
                Title = "PaperNook animation latency check", Width = 180, Height = 90,
                ShowActivated = false, ShowInTaskbar = false, Topmost = true,
                WindowStyle = WindowStyle.ToolWindow
            };
            var ring = new CodexInkRing { Width = 36, Height = 36, Value = .74, MotionAllowed = () => true };
            window.Content = ring;
            window.Loaded += async (_, _) =>
            {
                try { await ObserveAsync(window, ring, live); completion.TrySetResult(); }
                catch (Exception ex) { completion.TrySetException(ex); }
                finally { window.Close(); Dispatcher.CurrentDispatcher.BeginInvokeShutdown(DispatcherPriority.Background); }
            };
            window.Show();
            Dispatcher.Run();
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task.WaitAsync(TimeSpan.FromSeconds(live ? 40 : 25));
    }

    private static async Task ObserveAsync(Window window, CodexInkRing ring, bool live)
    {
        var frames = Channel.CreateUnbounded<(string Kind, DateTimeOffset At, double Delay)>();
        DateTimeOffset? lastFrame = null;
        ring.FrameDrawn += (kind, at) =>
        {
            if (at == null || at == lastFrame) return;
            lastFrame = at;
            frames.Writer.TryWrite((kind, at.Value, (DateTimeOffset.UtcNow - at.Value).TotalMilliseconds));
        };
        var started = DateTimeOffset.UtcNow;
        void Update(CodexActivityObservation observation)
        {
            if (live && observation.Timestamp < started) return;
            _ = window.Dispatcher.BeginInvoke(DispatcherPriority.Normal, () =>
            {
                if (!window.IsVisible) return;
                var presentation = CodexMeterPluginRuntime.BuildCapsule(new CodexMeterSnapshot
                {
                    ActivityKind = observation.Kind, ActivityEventAt = observation.Timestamp,
                    FiveHour = new CodexRateLimitWindow { RemainingPercent = 74 }
                });
                // Exercise both host normalization boundaries before the native drawing pass.
                presentation = PaperWindow.NormalizePluginCapsulePresentation(presentation)!;
                presentation = PaperWindow.NormalizePluginCapsulePresentation(presentation)!;
                var component = presentation.Components[0];
                ring.ActivityKind = component.Text;
                ring.ActivityEventAt = CodexActivityLatency.Timestamp(component);
            });
        }
        if (live)
        {
            using var monitor = new CodexActivityMonitor(Update);
            await Task.Delay(TimeSpan.FromSeconds(25));
            var samples = new List<(string Kind, DateTimeOffset At, double Delay)>();
            while (frames.Reader.TryRead(out var sample)) samples.Add(sample);
            Console.WriteLine(samples.Count == 0
                ? "INCONCLUSIVE live drawing: no fresh event rendered."
                : $"Live visible WPF drawing: n={samples.Count}; max={samples.Max(s => s.Delay):F0}ms; kinds={string.Join(',', samples.Select(s => s.Kind).Distinct())}; not internal reasoning/GPU presentation.");
            if (samples.Any(s => s.Delay > 1500 || s.Delay < 0))
                throw new InvalidOperationException("Live observable event-to-draw latency exceeded the 1500 ms acceptance budget or the clock moved.");
            return;
        }
        var directory = Path.Combine(Path.GetTempPath(), "PaperNook-render-check-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            foreach (var watcher in new[] { true, false })
            {
                var path = Path.Combine(directory, watcher ? "watcher.jsonl" : "poller.jsonl");
                using var monitor = new CodexActivityMonitor(Update, directory, enableWatcher: watcher);
                monitor.TrackCurrentRollout(path);
                var samples = new List<double>();
                foreach (var (type, eventType, expected) in new[]
                {
                    ("event_msg", "turn_started", "thinking"),
                    ("event_msg", "exec_command_begin", "tool"),
                    ("response_item", "function_call", "waiting"),
                    ("response_item", "message", "answering"),
                    ("event_msg", "turn_complete", "idle")
                })
                {
                    var at = DateTimeOffset.UtcNow;
                    await File.AppendAllTextAsync(path, JsonSerializer.Serialize(new
                    {
                        timestamp = at.ToString("O"), type,
                        payload = new { type = eventType, role = eventType == "message" ? "assistant" : null,
                            name = eventType == "function_call" ? "functions.request_user_input" : null }
                    }) + "\n");
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                    var frame = await frames.Reader.ReadAsync(timeout.Token);
                    if (frame.At != at || frame.Kind != expected || frame.Delay < 0 || frame.Delay > 1500)
                        throw new InvalidOperationException($"Incorrect or late rendered state: {expected} -> {frame.Kind}, {frame.Delay:F0}ms.");
                    samples.Add(frame.Delay);
                }
                Console.WriteLine($"Visible WPF drawing ({(watcher ? "watcher" : "fallback")}): {samples.Count} states; max={samples.Max():F0}ms, budget=1500ms, target=500ms. Synthetic events, real WPF drawing; not GPU presentation.");
            }
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
}
