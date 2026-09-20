using System.Diagnostics;
using System.IO;
using System.Windows;

namespace PaperTodo;

public partial class App
{
    private bool _restartAfterExitRequested;
    private PreparedUpdateApply? _preparedUpdateApply;
    private string[] _restartArguments = [];

    internal void RequestRestartAfterExit(params string[] arguments)
    {
        if (_restartAfterExitRequested)
        {
            return;
        }

        _restartAfterExitRequested = true;
        _restartArguments = arguments ?? [];
        Exit += OnRestartAfterExit;
    }

    internal void RequestUpdateAfterExit(PreparedUpdateApply prepared)
    {
        ArgumentNullException.ThrowIfNull(prepared);
        if (_restartAfterExitRequested) return;
        _preparedUpdateApply = prepared;
        _restartAfterExitRequested = true;
        Exit += OnRestartAfterExit;
    }

    private void OnRestartAfterExit(object? sender, ExitEventArgs e)
    {
        Exit -= OnRestartAfterExit;
        if (!_restartAfterExitRequested)
        {
            return;
        }

        _restartAfterExitRequested = false;
        try
        {
            if (_preparedUpdateApply != null)
            {
                var prepared = _preparedUpdateApply;
                _preparedUpdateApply = null;
                var updateStart = new ProcessStartInfo
                {
                    FileName = prepared.CoordinatorPath,
                    WorkingDirectory = Path.GetDirectoryName(prepared.CoordinatorPath) ?? AppContext.BaseDirectory,
                    UseShellExecute = false
                };
                updateStart.ArgumentList.Add("--apply-update");
                updateStart.ArgumentList.Add(prepared.TargetPath);
                updateStart.ArgumentList.Add(Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
                updateStart.ArgumentList.Add(prepared.Nonce);
                Process.Start(updateStart);
                return;
            }

            var executable = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(executable) || !File.Exists(executable))
            {
                return;
            }

            var restart = new ProcessStartInfo
            {
                FileName = executable,
                WorkingDirectory = AppContext.BaseDirectory,
                UseShellExecute = true
            };
            foreach (var argument in _restartArguments)
            {
                restart.ArgumentList.Add(argument);
            }
            _restartArguments = [];
            Process.Start(restart);
        }
        catch
        {
            // Restart is best-effort. The language preference is already persisted, so a normal
            // manual launch still applies it if process creation is blocked by the environment.
        }
    }
}
