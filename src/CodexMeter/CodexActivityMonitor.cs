using System.IO;
using System.Threading.Channels;

namespace PaperTodo;

internal sealed record CodexActivityObservation(string FilePath, string Kind, DateTimeOffset Timestamp);

// The watcher handles only activity. Usage accounting and authenticated quota reads stay on
// their existing slower refresh path, so a new turn can animate without a network round trip.
internal sealed class CodexActivityMonitor : IDisposable
{
    private readonly Action<CodexActivityObservation> _onActivity;
    private readonly string _sessionsDirectory;
    private readonly Channel<string> _changes = Channel.CreateBounded<string>(
        new BoundedChannelOptions(128)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.DropOldest
        });
    private readonly CancellationTokenSource _cancellation = new();
    private readonly FileSystemWatcher? _watcher;
    private readonly Task _worker;
    private readonly Task _poller;
    private CodexActivityObservation? _latest;
    private string? _trackedPath;
    private long _trackedLength = -1;
    private long _trackedWriteTicks;

    internal CodexActivityMonitor(Action<CodexActivityObservation> onActivity,
        string? sessionsDirectory = null, bool enableWatcher = true)
    {
        _onActivity = onActivity;
        var directory = Path.GetFullPath(sessionsDirectory ?? CodexSessionScanner.DefaultSessionsDirectory);
        _sessionsDirectory = directory.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var watchRoot = Directory.Exists(directory) ? directory : Path.GetDirectoryName(directory);
        if (enableWatcher && watchRoot != null && Directory.Exists(watchRoot))
        {
            FileSystemWatcher? watcher = null;
            try
            {
                watcher = new FileSystemWatcher(watchRoot, "*.jsonl")
                {
                    IncludeSubdirectories = true,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size
                };
                watcher.Changed += OnChanged;
                watcher.Created += OnChanged;
                watcher.Renamed += OnRenamed;
                watcher.EnableRaisingEvents = true;
                _watcher = watcher;
            }
            catch (IOException) { watcher?.Dispose(); }
            catch (UnauthorizedAccessException) { watcher?.Dispose(); }
        }
        _worker = Task.Run(ProcessAsync);
        _poller = Task.Run(PollTrackedRolloutAsync);
    }

    internal void TrackCurrentRollout(string? path) => Volatile.Write(ref _trackedPath, path);

    private void OnChanged(object sender, FileSystemEventArgs args)
    {
        if (args.FullPath.StartsWith(_sessionsDirectory, StringComparison.OrdinalIgnoreCase))
            _changes.Writer.TryWrite(args.FullPath);
    }

    private void OnRenamed(object sender, RenamedEventArgs args) => OnChanged(sender, args);

    private async Task ProcessAsync()
    {
        try
        {
            await foreach (var first in _changes.Reader.ReadAllAsync(_cancellation.Token))
            {
                // A rollout append can arrive as several notifications. The short settle period
                // avoids parsing a half-written JSON line without adding a quota refresh delay.
                await Task.Delay(20, _cancellation.Token).ConfigureAwait(false);
                var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { first };
                while (_changes.Reader.TryRead(out var path)) paths.Add(path);
                foreach (var path in paths)
                {
                    try
                    {
                        var observation = await CodexSessionScanner.ReadLatestActivityAsync(
                            path, _cancellation.Token).ConfigureAwait(false);
                        if (observation == null ||
                            _latest != null && observation.Timestamp <= _latest.Timestamp)
                            continue;
                        _latest = observation;
                        TrackCurrentRollout(path);
                        _onActivity(observation);
                    }
                    catch (OperationCanceledException) when (_cancellation.IsCancellationRequested) { return; }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                    catch (ObjectDisposedException) { }
                    catch (InvalidOperationException) { }
                }
            }
        }
        catch (OperationCanceledException) when (_cancellation.IsCancellationRequested) { }
    }

    private async Task PollTrackedRolloutAsync()
    {
        try
        {
            while (!_cancellation.IsCancellationRequested)
            {
                await Task.Delay(250, _cancellation.Token).ConfigureAwait(false);
                var path = Volatile.Read(ref _trackedPath);
                if (path == null) continue;
                try
                {
                    var file = new FileInfo(path);
                    if (!file.Exists || DateTime.UtcNow - file.LastWriteTimeUtc >
                        CodexSessionScanner.ActivityStaleAfter) continue;
                    if (file.Length == _trackedLength &&
                        file.LastWriteTimeUtc.Ticks == _trackedWriteTicks) continue;
                    _trackedLength = file.Length;
                    _trackedWriteTicks = file.LastWriteTimeUtc.Ticks;
                    _changes.Writer.TryWrite(path);
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
        catch (OperationCanceledException) when (_cancellation.IsCancellationRequested) { }
    }

    public void Dispose()
    {
        _watcher?.Dispose();
        _changes.Writer.TryComplete();
        _cancellation.Cancel();
    }
}
