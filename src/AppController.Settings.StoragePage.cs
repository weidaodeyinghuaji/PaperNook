using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;

namespace PaperTodo;

public sealed partial class AppController
{
    private UIElement BuildStorageSettingsPage()
    {
        var content = new StackPanel { Margin = new Thickness(2, 4, 6, 0) };
        content.Children.Add(SettingsSectionLabel(StorageText(
            "存储位置", "Storage locations", "保存先", "저장 위치")));
        content.Children.Add(new TextBlock
        {
            Text = StorageText(
                "缓存、核心数据、导出的 Markdown 笔记和备份相互独立。更改位置会在下次启动前生效；数据迁移后原目录保留供确认。",
                "Cache, durable data, exported Markdown notes, and backups are separate. Changes take effect before the next start; migrated data retains its old folder for recovery.",
                "キャッシュ、主要データ、書き出した Markdown ノートは別々に保存されます。変更は次回起動前に移行し、元のフォルダーは復旧用に残ります。",
                "캐시, 핵심 데이터, 내보낸 Markdown 노트는 별도로 저장됩니다. 다음 시작 전에 이전하며 복구를 위해 기존 폴더를 유지합니다."),
            Foreground = TrayWeakTextBrush,
            TextWrapping = TextWrapping.Wrap,
            FontSize = AppTypography.Scale(11.5),
            Margin = new Thickness(0, 2, 0, 10)
        });

        if (_storage.StartupMigrationError is { Length: > 0 } migrationError)
        {
            content.Children.Add(StorageStatusCard(StorageText(
                $"上次存储迁移未完成：{migrationError}",
                $"The last storage migration did not complete: {migrationError}",
                $"前回の保存先移行は完了しませんでした：{migrationError}",
                $"마지막 저장소 이전이 완료되지 않았습니다: {migrationError}"), true));
        }
        else if (_storage.HasPendingChanges)
        {
            content.Children.Add(StorageStatusCard(StorageText(
                "已保存新位置。重启后将校验并迁移数据；失败时继续使用原位置。",
                "New locations are saved. Restart to verify and migrate; the old location remains active if migration fails.",
                "新しい保存先を保存しました。再起動時に検証して移行し、失敗時は元の保存先を使います。",
                "새 위치를 저장했습니다. 다시 시작할 때 검증하고 이전하며 실패하면 기존 위치를 사용합니다."), false));
        }

        content.Children.Add(BuildStorageLocationCard(
            AppStorageDirectoryKind.Data,
            StorageText("数据位置", "Data location", "データ保存先", "데이터 위치"),
            StorageText(
                "保存便签状态、图片库和插件状态。它不是缓存。",
                "Stores paper state, the image database, and plugin state. This is not cache.",
                "紙の状態、画像データベース、プラグイン状態、自動バックアップを保存します。キャッシュではありません。",
                "메모 상태, 이미지 데이터베이스, 플러그인 상태 및 자동 백업을 저장합니다. 캐시가 아닙니다.")));
        content.Children.Add(BuildStorageLocationCard(
            AppStorageDirectoryKind.Notes,
            StorageText("笔记位置", "Notes location", "ノート保存先", "노트 위치"),
            StorageText(
                "保存用户明确导出的 Markdown 和附件，可选择文档、其他磁盘或同步目录。",
                "Stores explicitly exported Markdown and attachments. You can choose Documents, another drive, or a sync folder.",
                "書き出した Markdown と添付ファイルを保存します。ドキュメント、別ドライブ、同期フォルダーを選べます。",
                "내보낸 Markdown와 첨부 파일을 저장합니다. 문서, 다른 드라이브 또는 동기화 폴더를 선택할 수 있습니다.")));
        content.Children.Add(BuildStorageLocationCard(
            AppStorageDirectoryKind.Cache,
            StorageText("缓存位置", "Cache location", "キャッシュ保存先", "캐시 위치"),
            StorageText(
                "保存 WebView2 和可重新生成的临时内容。清理缓存不会删除便签、图片原件或导出的笔记。",
                "Stores WebView2 and regenerable temporary content. Clearing it does not delete papers, original images, or exported notes.",
                "WebView2 と再生成可能な一時データを保存します。削除しても紙、元画像、書き出したノートは消えません。",
                "WebView2 및 다시 생성 가능한 임시 콘텐츠를 저장합니다. 지워도 메모, 원본 이미지 또는 내보낸 노트는 삭제되지 않습니다.")));
        content.Children.Add(BuildStorageLocationCard(
            AppStorageDirectoryKind.Backup,
            StorageText("备份位置", "Backup location", "バックアップ保存先", "백업 위치"),
            StorageText(
                "保存可独立验证的 .papernook-backup 包，并兼容旧版 .papertodo-backup。手动备份不会被自动清理。",
                "Stores independently verifiable .papernook-backup packages and accepts legacy .papertodo-backup files. Manual backups are never cleaned automatically.",
                "独立検証可能な .papernook-backup を保存し、旧 .papertodo-backup にも対応します。手動バックアップは自動削除されません。",
                "독립적으로 검증 가능한 .papernook-backup 패키지를 저장하고 기존 .papertodo-backup도 지원합니다. 수동 백업은 자동으로 정리되지 않습니다.")));

        if (_storage.HasPendingChanges)
        {
            var restart = StorageButton(StorageText(
                "立即重启并迁移", "Restart and migrate now", "今すぐ再起動して移行", "지금 다시 시작하고 이전"));
            restart.Margin = new Thickness(0, 8, 0, 0);
            restart.HorizontalAlignment = HorizontalAlignment.Right;
            restart.Click += (_, _) => RestartForStorageMigration();
            content.Children.Add(restart);
        }
        return content;
    }

    private UIElement BuildStorageLocationCard(
        AppStorageDirectoryKind kind,
        string title,
        string description)
    {
        var card = new Border
        {
            BorderBrush = TrayBorderBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Background = Theme.Tint((byte)(Theme.IsDark ? 15 : 7)),
            Padding = new Thickness(12, 10, 12, 11),
            Margin = new Thickness(0, 0, 0, 10)
        };
        var content = new StackPanel();
        content.Children.Add(new TextBlock
        {
            Text = title,
            Foreground = TrayTextBrush,
            FontWeight = FontWeights.SemiBold,
            FontSize = AppTypography.Scale(12.5)
        });
        content.Children.Add(new TextBlock
        {
            Text = description,
            Foreground = TrayWeakTextBrush,
            TextWrapping = TextWrapping.Wrap,
            FontSize = AppTypography.Scale(11),
            Margin = new Thickness(0, 3, 0, 7)
        });

        var effectivePath = _storage.EffectiveDirectory(kind);
        var pending = !string.Equals(effectivePath, ActiveStorageDirectory(kind), StringComparison.OrdinalIgnoreCase);
        var pathBox = new TextBox
        {
            Text = effectivePath,
            IsReadOnly = true,
            IsReadOnlyCaretVisible = true,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            BorderBrush = TrayBorderBrush,
            Foreground = TrayTextBrush,
            Background = Brushes.Transparent,
            Padding = new Thickness(7, 5, 7, 5),
            ToolTip = effectivePath
        };
        AutomationProperties.SetName(pathBox, title);
        content.Children.Add(pathBox);
        if (pending)
        {
            content.Children.Add(new TextBlock
            {
                Text = StorageText("等待重启后生效", "Pending restart", "再起動待ち", "다시 시작 대기 중"),
                Foreground = Theme.ActiveBrush,
                FontSize = AppTypography.Scale(10.5),
                Margin = new Thickness(1, 4, 0, 0)
            });
        }

        var actions = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
        var open = StorageButton(StorageText("打开文件夹", "Open folder", "フォルダーを開く", "폴더 열기"));
        open.Click += (_, _) => OpenStorageDirectory(effectivePath);
        var change = StorageButton(StorageText("更改…", "Change…", "変更…", "변경…"));
        change.Margin = new Thickness(7, 0, 0, 0);
        change.Click += (_, _) => ChooseStorageDirectory(kind, title, effectivePath);
        var reset = StorageButton(StorageText("恢复默认", "Restore default", "既定に戻す", "기본값 복원"));
        reset.Margin = new Thickness(7, 0, 0, 0);
        reset.Click += (_, _) => ResetStorageDirectory(kind);
        actions.Children.Add(open);
        actions.Children.Add(change);
        actions.Children.Add(reset);

        if (kind == AppStorageDirectoryKind.Cache)
        {
            var size = _storage.CacheSizeBytes();
            var clear = StorageButton(size < 0
                ? StorageText("清理缓存", "Clear cache", "キャッシュを消去", "캐시 지우기")
                : StorageText($"清理缓存（{FormatStorageSize(size)}）", $"Clear cache ({FormatStorageSize(size)})",
                    $"キャッシュを消去（{FormatStorageSize(size)}）", $"캐시 지우기 ({FormatStorageSize(size)})"));
            clear.Margin = new Thickness(7, 0, 0, 0);
            clear.Click += (_, _) => ClearStorageCache();
            actions.Children.Add(clear);
        }
        content.Children.Add(actions);
        if (kind == AppStorageDirectoryKind.Backup)
        {
            var used = _storage.BackupSizeBytes();
            long free = -1;
            try
            {
                var root = Path.GetPathRoot(effectivePath);
                if (!string.IsNullOrWhiteSpace(root)) free = new DriveInfo(root).AvailableFreeSpace;
            }
            catch { }
            content.Children.Add(new TextBlock
            {
                Text = StorageText(
                    $"占用：{(used < 0 ? "未知" : FormatStorageSize(used))} · 可用：{(free < 0 ? "未知" : FormatStorageSize(free))}",
                    $"Used: {(used < 0 ? "unknown" : FormatStorageSize(used))} · Free: {(free < 0 ? "unknown" : FormatStorageSize(free))}",
                    $"使用量：{(used < 0 ? "不明" : FormatStorageSize(used))} · 空き：{(free < 0 ? "不明" : FormatStorageSize(free))}",
                    $"사용: {(used < 0 ? "알 수 없음" : FormatStorageSize(used))} · 여유: {(free < 0 ? "알 수 없음" : FormatStorageSize(free))}"),
                Foreground = TrayWeakTextBrush,
                FontSize = AppTypography.Scale(10.5),
                Margin = new Thickness(0, 6, 0, 0)
            });
        }
        card.Child = content;
        return card;
    }

    private Border StorageStatusCard(string message, bool isWarning) => new()
    {
        Background = Theme.Tint((byte)(Theme.IsDark ? (isWarning ? 38 : 26) : (isWarning ? 22 : 14))),
        BorderBrush = isWarning ? Theme.DangerBrush : Theme.ActiveBrush,
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(7),
        Padding = new Thickness(10, 8, 10, 8),
        Margin = new Thickness(0, 0, 0, 10),
        Child = new TextBlock { Text = message, Foreground = TrayTextBrush, TextWrapping = TextWrapping.Wrap }
    };

    private Button StorageButton(string text)
    {
        var button = SettingsTextButton(text);
        button.Focusable = true;
        AutomationProperties.SetName(button, text);
        return button;
    }

    private void ChooseStorageDirectory(AppStorageDirectoryKind kind, string title, string initialDirectory)
    {
        var dialog = new OpenFolderDialog { Title = title, InitialDirectory = initialDirectory, Multiselect = false };
        if (dialog.ShowDialog(_settingsWindow) != true)
        {
            return;
        }
        if (!_storage.TryScheduleChange(kind, dialog.FolderName, out var error))
        {
            ShowStorageMessage(StorageText("无法使用该位置", "Cannot use this location", "この保存先は使用できません", "이 위치를 사용할 수 없습니다"), error);
            return;
        }
        RefreshSettingsWindowContent();
    }

    private void ResetStorageDirectory(AppStorageDirectoryKind kind)
    {
        if (!_storage.TryResetToDefault(kind, out var error))
        {
            ShowStorageMessage(StorageText("无法恢复默认位置", "Cannot restore the default location", "既定の保存先に戻せません", "기본 위치를 복원할 수 없습니다"), error);
            return;
        }
        RefreshSettingsWindowContent();
    }

    private void ClearStorageCache()
    {
        if (!_storage.TryClearCache(out var error))
        {
            ShowStorageMessage(StorageText("缓存未完全清理", "Cache was not fully cleared", "キャッシュを完全に消去できませんでした", "캐시를 완전히 지우지 못했습니다"), error);
            return;
        }
        RefreshSettingsWindowContent();
    }

    private void OpenStorageDirectory(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
            Process.Start(new ProcessStartInfo { FileName = "explorer.exe", ArgumentList = { path }, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            ShowStorageMessage(StorageText("无法打开文件夹", "Cannot open folder", "フォルダーを開けません", "폴더를 열 수 없습니다"), ex.Message);
        }
    }

    private void RestartForStorageMigration()
    {
        SaveNow();
        if (Application.Current is App app)
        {
            app.RequestRestartAfterExit();
        }
        Exit();
    }

    private void ShowStorageMessage(string title, string message) =>
        MessageBox.Show(_settingsWindow, message, title, MessageBoxButton.OK, MessageBoxImage.Warning);

    private string ActiveStorageDirectory(AppStorageDirectoryKind kind) => kind switch
    {
        AppStorageDirectoryKind.Data => _storage.DataDirectory,
        AppStorageDirectoryKind.Notes => _storage.NotesDirectory,
        AppStorageDirectoryKind.Cache => _storage.CacheDirectory,
        AppStorageDirectoryKind.Backup => _storage.BackupDirectory,
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    private static string FormatStorageSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024d:0.#} KB",
        < 1024L * 1024 * 1024 => $"{bytes / (1024d * 1024):0.#} MB",
        _ => $"{bytes / (1024d * 1024 * 1024):0.##} GB"
    };

    private static string StorageText(string chinese, string english, string japanese, string korean) =>
        SettingsSidebarLocalized(chinese, english, japanese, korean);
}
