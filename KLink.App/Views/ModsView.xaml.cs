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

    private void InstallMod_Click(object sender, RoutedEventArgs e)
    {
        if (_manager is null)
        {
            new WpfUiMessageBox
            {
                Title = "KLink",
                Content = "请先在设置页配置游戏目录",
            }.ShowDialog();
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

        int ok = 0, renamed = 0;
        foreach (string file in dialog.FileNames)
        {
            string fileName = Path.GetFileName(file);
            string targetName = ModManager.EnsurePakSuffix(fileName);
            if (targetName != fileName)
                renamed++;
            string? result = _manager.InstallMod(file);
            if (result is not null)
                ok++;
            else
            {
                new WpfUiMessageBox
                {
                    Title = "KLink",
                    Content = $"安装失败：{fileName}",
                }.ShowDialog();
            }
        }
        if (ok > 0)
        {
            var message = $"成功安装 {ok} 个模组。";
            if (renamed > 0)
                message += $"\n\n其中有 {renamed} 个已自动补 _P 后缀（PC 端引擎只加载 _P 结尾的 pak）。";
            new WpfUiMessageBox
            {
                Title = "KLink",
                Content = message,
            }.ShowDialog();
        }
        RefreshMods();
    }

    private void ToggleMod_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Wpf.Ui.Controls.Button { Tag: ModEntry entry } && _manager is not null)
        {
            _manager.ToggleMod(entry.Name, !entry.Enabled);
            RefreshMods();
        }
    }

    private void UninstallMod_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Wpf.Ui.Controls.Button { Tag: ModEntry entry } && _manager is not null)
        {
            var confirm = new WpfUiMessageBox
            {
                Title = "KLink",
                Content = $"确定卸载模组「{entry.Name}」？",
                PrimaryButtonText = "卸载",
                SecondaryButtonText = "取消",
            };
            if (confirm.ShowDialog() != true)
                return;
            if (_manager.UninstallMod(entry.Name))
                LogService.Instance.Info($"模组已卸载：{entry.Name}");
            else
                LogService.Instance.Warn($"卸载失败：{entry.Name}");
            RefreshMods();
        }
    }
}
