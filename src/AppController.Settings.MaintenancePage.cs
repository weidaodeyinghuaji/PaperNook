using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using Application = System.Windows.Application;

namespace PaperTodo;

public sealed partial class AppController
{
    private DispatcherTimer? _updatePolicyTimer;
    private CancellationTokenSource? _updateOperationCancellation;
    private ReleaseAsset? _availableUpdate;
    private VerifiedUpdate? _verifiedUpdate;
    private string _updateStatus = "";
    private long _updateReceivedBytes;
    private long _updateTotalBytes;
    private DispatcherTimer? _backupPolicyTimer;
    private CancellationTokenSource? _backupOperationCancellation;
    private string _backupStatus = "";
    private string? _lastCreatedBackupPath;
    private readonly Dictionary<string, (long Length, DateTime LastWriteUtc, BackupVerification Result)>
        _backupVerificationCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _backupVerificationPending = new(StringComparer.OrdinalIgnoreCase);

    internal static IReadOnlyList<string> MaintenanceAutomationNames =>
    [
        MaintenanceText("检查更新", "Check for updates", "更新を確認", "업데이트 확인"),
        MaintenanceText("下载更新", "Download update", "更新をダウンロード", "업데이트 다운로드"),
        MaintenanceText("稍后提醒", "Remind me later", "後で通知", "나중에 알림"),
        MaintenanceText("忽略此版本", "Ignore this version", "このバージョンを無視", "이 버전 무시"),
        MaintenanceText("重启并安装", "Restart and install", "再起動してインストール", "다시 시작하여 설치"),
        MaintenanceText("立即备份", "Back up now", "今すぐバックアップ", "지금 백업"),
        MaintenanceText("取消备份", "Cancel backup", "バックアップをキャンセル", "백업 취소"),
        MaintenanceText("从备份恢复", "Restore from backup", "バックアップから復元", "백업에서 복원")
    ];

    internal void StartBackupPolicy()
    {
        if (!State.DailyAutomaticBackups || _backupPolicyTimer != null) return;
        if (State.LastAutomaticBackupUtc is { } lastAutomatic &&
            DateTimeOffset.UtcNow - lastAutomatic < TimeSpan.FromHours(24)) return;
        _backupPolicyTimer = new DispatcherTimer(DispatcherPriority.ApplicationIdle)
        {
            Interval = TimeSpan.FromSeconds(12)
        };
        _backupPolicyTimer.Tick += async (_, _) =>
        {
            _backupPolicyTimer?.Stop();
            _backupPolicyTimer = null;
            await CreateBackupAsync(BackupKind.Automatic);
        };
        _backupPolicyTimer.Start();
    }

    private void StopMaintenanceOperations()
    {
        _updatePolicyTimer?.Stop();
        _updatePolicyTimer = null;
        _backupPolicyTimer?.Stop();
        _backupPolicyTimer = null;
        try { _updateOperationCancellation?.Cancel(); } catch { }
        try { _backupOperationCancellation?.Cancel(); } catch { }
    }

    internal void StartUpdatePolicy()
    {
        if (!State.UpdateChecksEnabled || _updatePolicyTimer != null) return;
        var lastCheck = _updateStateStore.Load().LastCheckUtc;
        if (lastCheck.HasValue && DateTimeOffset.UtcNow - lastCheck.Value < TimeSpan.FromHours(24)) return;
        _updatePolicyTimer = new DispatcherTimer(DispatcherPriority.ApplicationIdle)
        {
            Interval = TimeSpan.FromSeconds(8)
        };
        _updatePolicyTimer.Tick += async (_, _) =>
        {
            _updatePolicyTimer?.Stop();
            _updatePolicyTimer = null;
            await CheckForUpdatesAsync(userInitiated: false);
        };
        _updatePolicyTimer.Start();
    }

    private UIElement BuildMaintenanceSettingsPage()
    {
        var content = new StackPanel { Margin = new Thickness(2, 4, 6, 0) };
        content.Children.Add(SettingsSectionLabel(MaintenanceText(
            "可信更新", "Trusted updates", "信頼できる更新", "신뢰할 수 있는 업데이트")));
        content.Children.Add(new TextBlock
        {
            Text = MaintenanceText(
                "默认只检查并通知；只有明确开启后才自动下载，安装始终需要点击“重启并安装”。签名信任未配置时更新功能保持关闭。",
                "By default PaperNook only checks and notifies. Automatic download is opt-in, and installation always requires Restart and install. Updates stay disabled until signing trust is configured.",
                "既定では確認と通知のみ行います。自動ダウンロードは明示的に有効化し、インストールには必ず「再起動してインストール」が必要です。署名の信頼設定がない場合、更新は無効です。",
                "기본적으로 확인 및 알림만 수행합니다. 자동 다운로드는 명시적으로 켜야 하며 설치하려면 항상 ‘다시 시작하여 설치’를 눌러야 합니다. 서명 신뢰가 구성되지 않으면 업데이트가 비활성화됩니다."),
            Foreground = TrayWeakTextBrush,
            TextWrapping = TextWrapping.Wrap,
            FontSize = AppTypography.Scale(11.5),
            Margin = new Thickness(0, 2, 0, 10)
        });

        content.Children.Add(SettingsToggle(
            MaintenanceText("每天自动检查更新", "Check for updates daily", "毎日更新を確認", "매일 업데이트 확인"),
            State.UpdateChecksEnabled,
            () =>
            {
                State.UpdateChecksEnabled = !State.UpdateChecksEnabled;
                SaveNow();
                if (State.UpdateChecksEnabled) StartUpdatePolicy();
            }));
        content.Children.Add(SettingsToggle(
            MaintenanceText("发现更新后自动下载", "Download updates automatically", "更新を自動ダウンロード", "업데이트 자동 다운로드"),
            State.AutoDownloadUpdates,
            () =>
            {
                State.AutoDownloadUpdates = !State.AutoDownloadUpdates;
                SaveNow();
            }));

        content.Children.Add(new TextBlock
        {
            Text = MaintenanceText("更新通道", "Update channel", "更新チャネル", "업데이트 채널"),
            Foreground = TrayTextBrush,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 12, 0, 4)
        });
        content.Children.Add(CreateSegmentSelector(
            [("stable", "Stable"), ("beta", "Beta / RC")],
            State.UpdateChannel is "beta" ? "beta" : "stable",
            value =>
            {
                State.UpdateChannel = value;
                SaveNow();
                _availableUpdate = null;
                _verifiedUpdate = null;
                _updateStatus = "";
                RefreshSettingsWindowContent();
            }));

        var status = string.IsNullOrWhiteSpace(_updateStatus)
            ? MaintenanceText("尚未检查更新。", "Updates have not been checked yet.", "まだ更新を確認していません。", "아직 업데이트를 확인하지 않았습니다.")
            : _updateStatus;
        content.Children.Add(new TextBlock
        {
            Text = status,
            Foreground = TrayWeakTextBrush,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 12, 0, 4)
        });
        if (_updateTotalBytes > 0)
        {
            var progress = Math.Clamp((double)_updateReceivedBytes / _updateTotalBytes, 0, 1);
            content.Children.Add(new ProgressBar { Minimum = 0, Maximum = 1, Value = progress, Height = 7 });
            content.Children.Add(new TextBlock
            {
                Text = $"{progress:P0}  ({FormatStorageSize(_updateReceivedBytes)} / {FormatStorageSize(_updateTotalBytes)})",
                Foreground = TrayWeakTextBrush,
                Margin = new Thickness(0, 3, 0, 0)
            });
        }

        var actions = new WrapPanel { Margin = new Thickness(0, 12, 0, 0) };
        var names = MaintenanceAutomationNames;
        actions.Children.Add(MaintenanceButton(names[0], async () => await CheckForUpdatesAsync(userInitiated: true), enabled: true));
        actions.Children.Add(MaintenanceButton(names[1], async () => await DownloadAvailableUpdateAsync(), _availableUpdate != null && _verifiedUpdate == null));
        actions.Children.Add(MaintenanceButton(names[2], () =>
        {
            _availableUpdate = null;
            _updateStatus = MaintenanceText("已稍后提醒。", "Reminder postponed.", "後で通知します。", "나중에 알립니다.");
            RefreshSettingsWindowContent();
            return Task.CompletedTask;
        }, _availableUpdate != null));
        actions.Children.Add(MaintenanceButton(names[3], () =>
        {
            if (_availableUpdate != null) _updateStateStore.IgnoreVersion(_availableUpdate.Version);
            _availableUpdate = null;
            _updateStatus = MaintenanceText("已忽略此版本。", "This version is ignored.", "このバージョンを無視しました。", "이 버전을 무시했습니다.");
            RefreshSettingsWindowContent();
            return Task.CompletedTask;
        }, _availableUpdate != null));
        actions.Children.Add(MaintenanceButton(names[4], RestartAndInstallUpdateAsync, _verifiedUpdate != null));
        content.Children.Add(actions);

        content.Children.Add(SettingsSectionLabel(MaintenanceText(
            "备份与恢复", "Backup & restore", "バックアップと復元", "백업 및 복원")));
        content.Children.Add(new TextBlock
        {
            Text = MaintenanceText(
                "备份包含便签、图片库和插件数据，并在完成后逐文件校验。恢复会先保留当前数据的可回滚副本，再重启切换。",
                "Backups contain papers, the image library, and plugin data, then verify every file. Restore retains a rollback copy of current data before restarting and switching.",
                "バックアップには紙、画像、プラグインデータが含まれ、ファイルごとに検証します。復元時は現在のデータをロールバック用に保持してから再起動します。",
                "백업에는 메모, 이미지 및 플러그인 데이터가 포함되며 파일별로 검증합니다. 복원 시 현재 데이터의 롤백 복사본을 유지한 뒤 다시 시작합니다."),
            Foreground = TrayWeakTextBrush,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 4, 0, 8)
        });
        content.Children.Add(SettingsToggle(
            MaintenanceText("每日自动备份", "Daily automatic backup", "毎日自動バックアップ", "매일 자동 백업"),
            State.DailyAutomaticBackups,
            () =>
            {
                State.DailyAutomaticBackups = !State.DailyAutomaticBackups;
                SaveNow();
                if (State.DailyAutomaticBackups) StartBackupPolicy();
            }));
        content.Children.Add(SettingsToggle(
            MaintenanceText("包含已导出的 Markdown 笔记", "Include exported Markdown notes", "書き出した Markdown ノートを含める", "내보낸 Markdown 노트 포함"),
            State.IncludeExportedNotesInBackups,
            () =>
            {
                State.IncludeExportedNotesInBackups = !State.IncludeExportedNotesInBackups;
                SaveNow();
            }));
        content.Children.Add(new TextBlock
        {
            Text = MaintenanceText("自动备份保留数", "Automatic backup retention", "自動バックアップ保持数", "자동 백업 보존 수"),
            Foreground = TrayTextBrush,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 10, 0, 4)
        });
        content.Children.Add(CreateSegmentSelector(
            [("3", "3"), ("10", "10"), ("20", "20"), ("30", "30")],
            Math.Clamp(State.AutomaticBackupRetention, 3, 30).ToString(),
            value =>
            {
                State.AutomaticBackupRetention = int.Parse(value);
                SaveNow();
            }));

        var backupStatus = string.IsNullOrWhiteSpace(_backupStatus)
            ? State.LastSuccessfulBackupUtc is { } last
                ? MaintenanceText($"最近成功：{last.ToLocalTime():g}", $"Last success: {last.ToLocalTime():g}", $"最終成功：{last.ToLocalTime():g}", $"최근 성공: {last.ToLocalTime():g}")
                : MaintenanceText("尚未创建备份。", "No backup has been created yet.", "まだバックアップがありません。", "아직 백업이 없습니다.")
            : _backupStatus;
        content.Children.Add(new TextBlock
        {
            Text = backupStatus,
            Foreground = TrayWeakTextBrush,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 10, 0, 6)
        });
        var backupActions = new WrapPanel();
        backupActions.Children.Add(MaintenanceButton(
            MaintenanceAutomationNames[5],
            () => CreateBackupAsync(BackupKind.Manual),
            _backupOperationCancellation == null));
        backupActions.Children.Add(MaintenanceButton(
            MaintenanceAutomationNames[6],
            () =>
            {
                _backupOperationCancellation?.Cancel();
                return Task.CompletedTask;
            },
            _backupOperationCancellation != null));
        backupActions.Children.Add(MaintenanceButton(
            MaintenanceAutomationNames[7],
            RestoreFromBackupAsync,
            _backupOperationCancellation == null));
        if (_lastCreatedBackupPath != null)
        {
            backupActions.Children.Add(MaintenanceButton(
                MaintenanceText("打开所在位置", "Open location", "保存先を開く", "위치 열기"),
                () =>
                {
                    OpenStorageDirectory(Path.GetDirectoryName(_lastCreatedBackupPath)!);
                    return Task.CompletedTask;
                },
                true));
        }
        content.Children.Add(backupActions);
        content.Children.Add(BuildBackupList());

        content.Children.Add(SettingsSectionLabel(MaintenanceText(
            "关于纸隅", "About PaperNook", "PaperNook について", "PaperNook 정보")));
        content.Children.Add(new TextBlock
        {
            Text = MaintenanceText(
                $"纸隅 PaperNook v{PaperTodoVersion.Current}\n作者：{ProductIdentity.Author}\n反馈：{ProductIdentity.FeedbackEmail}\n基于 PaperTodo 修改；原项目版权和许可声明保留在 LICENSE.md。",
                $"PaperNook v{PaperTodoVersion.Current}\nAuthor: {ProductIdentity.Author}\nFeedback: {ProductIdentity.FeedbackEmail}\nBased on PaperTodo; the original copyright and license notices remain in LICENSE.md.",
                $"PaperNook v{PaperTodoVersion.Current}\n作者：{ProductIdentity.Author}\nフィードバック：{ProductIdentity.FeedbackEmail}\nPaperTodo を基に変更し、元の著作権とライセンス表示は LICENSE.md に保持しています。",
                $"PaperNook v{PaperTodoVersion.Current}\n작성자: {ProductIdentity.Author}\n피드백: {ProductIdentity.FeedbackEmail}\nPaperTodo를 기반으로 수정했으며 원저작권 및 라이선스 고지는 LICENSE.md에 유지됩니다."),
            Foreground = TrayWeakTextBrush,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 4, 0, 8)
        });
        var aboutActions = new WrapPanel();
        aboutActions.Children.Add(MaintenanceButton("GitHub", () =>
        {
            Process.Start(new ProcessStartInfo(ProductIdentity.RepositoryUrl) { UseShellExecute = true });
            return Task.CompletedTask;
        }, true));
        aboutActions.Children.Add(MaintenanceButton(
            MaintenanceText("发送反馈", "Send feedback", "フィードバックを送信", "피드백 보내기"),
            () =>
            {
                Process.Start(new ProcessStartInfo($"mailto:{ProductIdentity.FeedbackEmail}") { UseShellExecute = true });
                return Task.CompletedTask;
            },
            true));
        content.Children.Add(aboutActions);
        return content;
    }

    private UIElement BuildBackupList()
    {
        var panel = new StackPanel { Margin = new Thickness(0, 7, 0, 0) };
        foreach (var path in BackupService.EnumerateBackupFiles(_storage.BackupDirectory)
                 .OrderByDescending(File.GetLastWriteTimeUtc)
                 .Take(10))
        {
            var info = new FileInfo(path);
            var hasVerification = _backupVerificationCache.TryGetValue(path, out var cached) &&
                                  cached.Length == info.Length &&
                                  cached.LastWriteUtc == info.LastWriteTimeUtc;
            if (!hasVerification && _backupVerificationPending.Add(path))
            {
                _ = VerifyBackupForListAsync(path, info.Length, info.LastWriteTimeUtc);
            }
            var description = !hasVerification
                ? $"{Path.GetFileName(path)}  ·  {FormatStorageSize(info.Length)}  ·  {MaintenanceText("正在验证…", "Verifying…", "検証中…", "검증 중…")}"
                : cached.Result.IsValid
                    ? $"{Path.GetFileName(path)}  ·  {FormatStorageSize(info.Length)}  ·  {cached.Result.Manifest!.Kind}  ·  OK"
                    : $"{Path.GetFileName(path)}  ·  {MaintenanceText("损坏", "Damaged", "破損", "손상됨")}: {cached.Result.Error}";
            panel.Children.Add(new TextBlock
            {
                Text = description,
                Foreground = hasVerification && !cached.Result.IsValid ? Theme.DangerBrush : TrayWeakTextBrush,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 2, 0, 2)
            });
        }
        return panel;
    }

    private async Task VerifyBackupForListAsync(string path, long length, DateTime lastWriteUtc)
    {
        var result = await Task.Run(() => BackupService.VerifyPackage(path));
        await Application.Current.Dispatcher.InvokeAsync(() =>
        {
            _backupVerificationPending.Remove(path);
            _backupVerificationCache[path] = (length, lastWriteUtc, result);
            if (!IsExiting) RefreshSettingsWindowContent();
        });
    }

    private async Task CreateBackupAsync(BackupKind kind)
    {
        if (_backupOperationCancellation != null) return;
        _backupOperationCancellation = new CancellationTokenSource();
        _backupStatus = MaintenanceText("正在创建并验证备份…", "Creating and verifying backup…", "バックアップを作成して検証しています…", "백업 생성 및 검증 중…");
        RefreshSettingsWindowContent();
        try
        {
            var progress = new Progress<BackupProgress>(value =>
            {
                _backupStatus = $"{value.Stage}  {value.CompletedFiles}/{value.TotalFiles}";
                RefreshSettingsWindowContent();
            });
            var result = await _backupService.CreateAsync(
                new BackupRequest(
                    kind,
                    State.IncludeExportedNotesInBackups,
                    State.AutomaticBackupRetention),
                progress,
                _backupOperationCancellation.Token);
            _lastCreatedBackupPath = result.BackupPath;
            State.LastSuccessfulBackupUtc = DateTimeOffset.UtcNow;
            if (kind == BackupKind.Automatic)
                State.LastAutomaticBackupUtc = State.LastSuccessfulBackupUtc;
            State.LastBackupError = "";
            _backupStatus = MaintenanceText(
                $"备份已验证：{Path.GetFileName(result.BackupPath)}",
                $"Backup verified: {Path.GetFileName(result.BackupPath)}",
                $"バックアップを検証しました：{Path.GetFileName(result.BackupPath)}",
                $"백업 검증 완료: {Path.GetFileName(result.BackupPath)}");
            SaveNow();
        }
        catch (OperationCanceledException)
        {
            _backupStatus = MaintenanceText("备份已取消。", "Backup canceled.", "バックアップをキャンセルしました。", "백업이 취소되었습니다.");
        }
        catch (Exception ex)
        {
            State.LastBackupError = ex.Message;
            _backupStatus = MaintenanceText($"备份失败：{ex.Message}", $"Backup failed: {ex.Message}", $"バックアップに失敗しました：{ex.Message}", $"백업 실패: {ex.Message}");
            SaveNow();
        }
        finally
        {
            _backupOperationCancellation.Dispose();
            _backupOperationCancellation = null;
            RefreshSettingsWindowContent();
        }
    }

    private async Task RestoreFromBackupAsync()
    {
        var dialog = new OpenFileDialog
        {
            Title = MaintenanceText("选择 PaperNook 备份", "Select a PaperNook backup", "PaperNook バックアップを選択", "PaperNook 백업 선택"),
            Filter = "PaperNook backup (*.papernook-backup)|*.papernook-backup|Legacy PaperTodo backup (*.papertodo-backup)|*.papertodo-backup",
            InitialDirectory = _storage.BackupDirectory,
            Multiselect = false
        };
        if (dialog.ShowDialog(_settingsWindow) != true) return;
        var inspection = await Task.Run(() => _backupRestoreCoordinator.Inspect(dialog.FileName));
        if (!inspection.IsCompatible)
        {
            ShowStorageMessage(MaintenanceText("无法恢复", "Cannot restore", "復元できません", "복원할 수 없음"), inspection.Error);
            return;
        }
        var summary = MaintenanceText(
            $"版本：{inspection.AppVersion}\n便签：{inspection.PaperCount}\n大小：{FormatStorageSize(inspection.TotalBytes)}",
            $"Version: {inspection.AppVersion}\nPapers: {inspection.PaperCount}\nSize: {FormatStorageSize(inspection.TotalBytes)}",
            $"バージョン：{inspection.AppVersion}\n紙：{inspection.PaperCount}\nサイズ：{FormatStorageSize(inspection.TotalBytes)}",
            $"버전: {inspection.AppVersion}\n메모: {inspection.PaperCount}\n크기: {FormatStorageSize(inspection.TotalBytes)}");
        if (MessageBox.Show(_settingsWindow, summary,
                MaintenanceText("确认备份内容", "Confirm backup contents", "バックアップ内容の確認", "백업 내용 확인"),
                MessageBoxButton.OKCancel, MessageBoxImage.Information) != MessageBoxResult.OK) return;
        var finalWarning = MaintenanceText(
            "当前数据会先保留为可回滚副本，程序随后重启并恢复此备份。是否继续？",
            "Current data will first be retained as a rollback copy. PaperNook will then restart and restore this backup. Continue?",
            "現在のデータをロールバック用に保持した後、再起動してこのバックアップを復元します。続行しますか？",
            "현재 데이터를 롤백 복사본으로 보존한 뒤 다시 시작하여 이 백업을 복원합니다. 계속할까요?");
        if (MessageBox.Show(_settingsWindow, finalWarning,
                MaintenanceText("安排恢复", "Schedule restore", "復元を予約", "복원 예약"),
                MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        await _backupRestoreCoordinator.ScheduleAsync(dialog.FileName, CancellationToken.None);
        if (Application.Current is App app) app.RequestRestartAfterExit();
        Exit();
    }

    private Button MaintenanceButton(string text, Func<Task> action, bool enabled)
    {
        var button = SettingsTextButton(text);
        button.Focusable = true;
        button.IsEnabled = enabled;
        button.Margin = new Thickness(0, 0, 7, 7);
        AutomationProperties.SetName(button, text);
        button.Click += async (_, _) => await action();
        return button;
    }

    private async Task CheckForUpdatesAsync(bool userInitiated)
    {
        if (_updateOperationCancellation != null) return;
        var downloadAfterCheck = false;
        _updateOperationCancellation = new CancellationTokenSource();
        _updateStatus = MaintenanceText("正在检查更新…", "Checking for updates…", "更新を確認しています…", "업데이트 확인 중…");
        RefreshSettingsWindowContent();
        try
        {
            var channel = State.UpdateChannel == "beta" ? UpdateChannel.Beta : UpdateChannel.Stable;
            var result = await _updateClient.CheckAsync(channel, _updateOperationCancellation.Token);
            _updateStateStore.RecordCheck(result.ETag, DateTimeOffset.UtcNow);
            _availableUpdate = result.Asset;
            _updateStatus = result.Status switch
            {
                UpdateCheckStatus.Available => MaintenanceText($"发现版本 {result.Asset!.Version}。", $"Version {result.Asset!.Version} is available.", $"バージョン {result.Asset!.Version} を利用できます。", $"버전 {result.Asset!.Version}을 사용할 수 있습니다."),
                UpdateCheckStatus.NoUpdate => MaintenanceText("当前已是最新版本。", "PaperNook is up to date.", "最新バージョンです。", "최신 버전입니다."),
                UpdateCheckStatus.Disabled => MaintenanceText("发布签名信任尚未配置，更新功能已安全关闭。", "Release signing trust is not configured; updates are safely disabled.", "リリース署名の信頼設定がないため、更新は安全に無効化されています。", "릴리스 서명 신뢰가 구성되지 않아 업데이트가 안전하게 비활성화되었습니다."),
                _ => MaintenanceText($"检查失败：{result.Message}", $"Update check failed: {result.Message}", $"更新確認に失敗しました: {result.Message}", $"업데이트 확인 실패: {result.Message}")
            };
            if (result.Status == UpdateCheckStatus.Available && State.AutoDownloadUpdates)
                downloadAfterCheck = true;
        }
        catch (OperationCanceledException)
        {
            if (userInitiated) _updateStatus = MaintenanceText("已取消检查。", "Update check canceled.", "更新確認をキャンセルしました。", "업데이트 확인이 취소되었습니다.");
        }
        finally
        {
            _updateOperationCancellation.Dispose();
            _updateOperationCancellation = null;
            RefreshSettingsWindowContent();
        }
        if (downloadAfterCheck)
            await DownloadAvailableUpdateAsync();
    }

    private async Task DownloadAvailableUpdateAsync()
    {
        if (_availableUpdate == null || _updateOperationCancellation != null) return;
        _updateOperationCancellation = new CancellationTokenSource();
        _updateReceivedBytes = 0;
        _updateTotalBytes = _availableUpdate.Length;
        _updateStatus = MaintenanceText("正在下载并验证签名…", "Downloading and verifying signature…", "ダウンロードして署名を検証しています…", "다운로드 및 서명 확인 중…");
        RefreshSettingsWindowContent();
        try
        {
            var progress = new Progress<UpdateProgress>(value =>
            {
                _updateReceivedBytes = value.BytesReceived;
                _updateTotalBytes = value.TotalBytes;
                RefreshSettingsWindowContent();
            });
            _verifiedUpdate = await _updateClient.DownloadAndVerifyAsync(_availableUpdate, progress, _updateOperationCancellation.Token);
            _updateStatus = MaintenanceText("更新已验证，等待重启安装。", "Update verified; restart to install.", "更新を検証しました。再起動してインストールしてください。", "업데이트가 확인되었습니다. 다시 시작하여 설치하세요.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _verifiedUpdate = null;
            _updateStatus = MaintenanceText($"下载失败：{ex.Message}", $"Download failed: {ex.Message}", $"ダウンロードに失敗しました: {ex.Message}", $"다운로드 실패: {ex.Message}");
        }
        finally
        {
            _updateOperationCancellation.Dispose();
            _updateOperationCancellation = null;
            RefreshSettingsWindowContent();
        }
    }

    private Task RestartAndInstallUpdateAsync()
    {
        if (_verifiedUpdate == null || string.IsNullOrWhiteSpace(Environment.ProcessPath)) return Task.CompletedTask;
        try
        {
            var prepared = new UpdateInstaller(_storage.CacheDirectory, _updateStateStore)
                .Prepare(_verifiedUpdate, Environment.ProcessPath);
            if (Application.Current is App app) app.RequestUpdateAfterExit(prepared);
            Exit();
        }
        catch (Exception ex)
        {
            _updateStatus = MaintenanceText($"无法准备安装：{ex.Message}", $"Could not prepare installation: {ex.Message}", $"インストールを準備できません: {ex.Message}", $"설치를 준비할 수 없습니다: {ex.Message}");
            RefreshSettingsWindowContent();
        }
        return Task.CompletedTask;
    }

    private static string MaintenanceText(string chinese, string english, string japanese, string korean) =>
        UiLanguages.EffectiveUiCulture.TwoLetterISOLanguageName switch
        {
            "en" => english,
            "ja" => japanese,
            "ko" => korean,
            _ => chinese
        };
}
