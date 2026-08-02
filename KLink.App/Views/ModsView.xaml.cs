using System.IO;
using System.Windows;
using System.Windows.Controls;
using KLink.App.Services;
using Microsoft.Win32;
using WpfUiMessageBox = Wpf.Ui.Controls.MessageBox;

namespace KLink.App.Views;

public partial class ModsView : UserControl
{
    private ModManager? _manager;

    public ModsView()
    {
        InitializeComponent();
        Loaded += (_, _) => RefreshMods();
    }

    private void RefreshMods()
    {
        string? pakPath = SettingsService.Instance.PaksDirectory;
        if (pakPath is null)
        {
            TxtModsPath.Text = "⚠ 未定位游戏目录，请到设置页指定";
            ModList.ItemsSource = null;
            return;
        }
        _manager = new ModManager(pakPath);
        TxtModsPath.Text = pakPath;
        ModList.ItemsSource = _manager.ScanMods();
    }

    /// <summary>供外部（主窗口拖拽安装后）刷新列表。</summary>
    public void Refresh() => RefreshMods();

    private void RefreshMods_Click(object sender, RoutedEventArgs e) => RefreshMods();

    private async void InstallMod_Click(object sender, RoutedEventArgs e)
    {
        if (_manager is null)
        {
            await new WpfUiMessageBox
            {
                Title = "KLink-dotnet",
                Content = "请先在设置页配置游戏目录",
            }.ShowDialogAsync();
            return;
        }
        var dialog = new OpenFileDialog
        {
            Title = "选择 PAK 模组文件",
            Filter = "PAK 模组 (*.pak)|*.pak|所有文件 (*.*)|*.*",
            Multiselect = true,
        };
        if (dialog.ShowDialog() != true)
            return;

        InstallPaks(dialog.FileNames.ToList());
    }

    /// <summary>后台安装模组列表（按钮与拖拽共用），完成后刷新列表并汇总提示。</summary>
    public void InstallPaks(IReadOnlyList<string> files)
    {
        if (files.Count == 0)
            return;
        var manager = _manager ?? new ModManager(SettingsService.Instance.PaksDirectory ?? "");
        // 后台安装，避免大文件复制卡死 UI
        Task.Run(() =>
        {
            int ok = 0, renamed = 0;
            var skipped = new List<string>();
            var failed = new List<string>();
            foreach (string file in files)
            {
                try
                {
                    string fileName = Path.GetFileName(file);
                    if (ModManager.IsBuiltinPak(fileName))
                    {
                        skipped.Add(fileName); // 游戏本体包，跳过
                        continue;
                    }
                    if (ModManager.EnsurePakSuffix(fileName) != fileName)
                        renamed++;
                    if (manager.InstallMod(file) is not null)
                        ok++;
                    else
                        failed.Add(fileName);
                }
                catch (Exception ex)
                {
                    LogService.Instance.Error($"安装 {file} 异常：{ex.Message}");
                    failed.Add(Path.GetFileName(file));
                }
            }
            Dispatcher.BeginInvoke(async () =>
            {
                RefreshMods();
                var parts = new List<string>();
                if (ok > 0)
                    parts.Add($"已安装 {ok} 个模组");
                if (renamed > 0)
                    parts.Add($"其中 {renamed} 个已自动补 _P 后缀");
                if (skipped.Count > 0)
                    parts.Add($"跳过游戏本体包 {skipped.Count} 个");
                if (failed.Count > 0)
                    parts.Add($"失败 {failed.Count} 个：{string.Join("、", failed)}");
                if (parts.Count > 0)
                {
                    await new WpfUiMessageBox
                    {
                        Title = "KLink-dotnet",
                        Content = string.Join("\n", parts),
                    }.ShowDialogAsync();
                }
            });
        });
    }

    private void ToggleMod_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Wpf.Ui.Controls.Button { Tag: ModEntry entry } button || _manager is null)
            return;
        var manager = _manager;
        bool enable = !entry.Enabled;
        button.IsEnabled = false;
        Task.Run(() =>
        {
            bool ok = manager.ToggleMod(entry.Name, enable);
            Dispatcher.BeginInvoke(() =>
            {
                button.IsEnabled = true;
                if (!ok)
                    LogService.Instance.Warn($"切换状态失败：{entry.Name}");
                RefreshMods();
            });
        });
    }

    private async void UninstallMod_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Wpf.Ui.Controls.Button { Tag: ModEntry entry } || _manager is null)
            return;
        var confirm = new WpfUiMessageBox
        {
            Title = "KLink-dotnet",
            Content = $"确定卸载模组「{entry.Name}」？",
            PrimaryButtonText = "卸载",
            SecondaryButtonText = "取消",
        };
        if (await confirm.ShowDialogAsync() != Wpf.Ui.Controls.MessageBoxResult.Primary)
            return;

        var manager = _manager;
        // 后台卸载（文件被游戏占用时会等待/失败，不在 UI 线程阻塞）
        Task.Run(() =>
        {
            bool ok = manager.UninstallMod(entry.Name);
            Dispatcher.BeginInvoke(async () =>
            {
                if (ok)
                {
                    LogService.Instance.Info($"模组已卸载：{entry.Name}");
                    RefreshMods();
                }
                else
                {
                    LogService.Instance.Warn($"卸载失败：{entry.Name}");
                    await new WpfUiMessageBox
                    {
                        Title = "KLink-dotnet",
                        Content = $"卸载「{entry.Name}」失败，文件可能被占用（请先关闭游戏后重试）。",
                    }.ShowDialogAsync();
                    RefreshMods();
                }
            });
        });
    }
}
