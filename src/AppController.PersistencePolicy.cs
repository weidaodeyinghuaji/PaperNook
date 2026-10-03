using System.Windows;
using System.Windows.Threading;

namespace PaperTodo;

public sealed partial class AppController
{
    private DispatcherTimer? _stateBackupTimer;
    private bool _preparingNormalExit;

    internal bool TryPrepareNormalExit(Func<MessageBoxResult>? choose = null, Action? reportSaveFailure = null)
    {
        if (!IsRunning || _preparingNormalExit) return false;
        _preparingNormalExit = true;
        try
        {
            // Resolve UI drafts while commands are still allowed. A failed save must keep
            // the running lifecycle and its retry timers intact; shutdown is not rollback.
            foreach (var window in _plannerWindows.ToArray())
                if (!window.TryPrepareExit(choose)) return false;
            if (!IsRunning) return false;
            CommitSettingsExternalMarkdownEditor(saveImmediately: false);
            foreach (var window in _windows.Values.ToArray()) window.CommitPendingEditsForSave();
            if (TrySaveNow(sync: true, notifyFailure: false)) return true;
            // Ignoring routine autosave warnings must not hide a cancelled exit.
            if (reportSaveFailure != null) reportSaveFailure();
            else
                MessageBox.Show(Strings.Get("NormalExitSaveFailure"), Strings.Get("SaveFailureTitle"),
                    MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }
        finally { _preparingNormalExit = false; }
    }

    internal void StartStateBackupPolicy()
    {
        if (IsExiting || _stateBackupTimer != null)
        {
            return;
        }

        TryRefreshStateBackup();

        _stateBackupTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromHours(6)
        };
        _stateBackupTimer.Tick += OnStateBackupTimerTick;
        _stateBackupTimer.Start();
    }

    private void OnStateBackupTimerTick(object? sender, EventArgs e)
    {
        if (IsExiting)
        {
            StopStateBackupPolicy();
            return;
        }

        TryRefreshStateBackup();
    }

    private void TryRefreshStateBackup()
    {
        try
        {
            _store.TryRefreshBackupFromPrimary();
        }
        catch
        {
            // Backup refresh is an independent safety layer. A failed snapshot must never stop
            // normal primary autosave or make startup/long-running sessions unusable.
        }
    }

    private void StopStateBackupPolicy()
    {
        var timer = _stateBackupTimer;
        _stateBackupTimer = null;
        if (timer == null)
        {
            return;
        }

        timer.Stop();
        timer.Tick -= OnStateBackupTimerTick;
    }

    internal void ExitForSystemShutdown()
    {
        if (IsExiting)
        {
            return;
        }

        // Autosave already owns durability during normal runtime. Do not start new core or plugin
        // state writes while Windows is ending the session. The plugin store waits for any write
        // already holding its gate to finish, then disables queued/final flushes before disposal.
        _lifecycleState = AppLifecycleState.Exiting;
        StopStateBackupPolicy();
        _paperBodyPlugins.DataStore.SuppressFinalFlushOnDispose();
        DisposeRuntimeResources();
        try { _pluginStartupHealthStore.MarkCleanExit(); } catch { }
        _lifecycleState = AppLifecycleState.Disposed;

        try
        {
            Application.Current.Shutdown();
        }
        catch
        {
            // Windows is already ending the session; cleanup must not delay shutdown.
        }
    }
}
