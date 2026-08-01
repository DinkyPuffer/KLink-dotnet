using System.IO;
using System.Windows;
using System.Windows.Controls;
using KLink.App.Services;
using Microsoft.Win32;

namespace KLink.App.Views;

public partial class SettingsView : UserControl
{
    private readonly SettingsService _settings = SettingsService.Instance;

    public SettingsView()
    {
        InitializeComponent();
        LoadSettings();
    }

    private void LoadSettings()
    {
        var s = _settings.Settings;
        TxtRoomName.Text = s.RoomName;
        TxtHostName.Text = s.HostName;
        TxtPlayerName.Text = s.PreferredPlayerName;
        TxtAdminToken.Text = s.AdminToken;
        TxtRemoteAddress.Text = s.RemoteAddress;
        TxtRemotePort.Text = s.RemotePort.ToString();
        TxtGameRoot.Text = s.GameRoot ?? "";

        // 主题
        if (s.Theme == "light")
        {
            if (ThemeLight is not null) ThemeLight.IsChecked = true;
        }
        else
        {
            if (ThemeDark is not null) ThemeDark.IsChecked = true;
        }

        var root = _settings.ResolveGameRoot();
        TxtDetectedRoot.Text = root is null
            ? "⚠ 未定位到 kds 目录（请手动指定，或将启动器放在 kds 同级/上级目录）"
            : $"✔ 已定位：{root}";
    }

    private void Theme_Changed(object sender, RoutedEventArgs e)
    {
        if (ThemeDark is null || ThemeLight is null)
            return; // InitializeComponent 期间的事件早触发保护
        string theme = ThemeLight.IsChecked == true ? "light" : "dark";
        if (System.Windows.Application.Current is App app)
            app.ApplyTheme(theme);
        LogService.Instance.Info($"主题已切换：{(theme == "light" ? "Moon Light（浅色）" : "Deep Dark（深色）")}");
    }

    private void SaveSettings_Click(object sender, RoutedEventArgs e)
    {
        var s = _settings.Settings;
        s.RoomName = TxtRoomName.Text.Trim();
        s.HostName = TxtHostName.Text.Trim();
        s.PreferredPlayerName = TxtPlayerName.Text.Trim();
        s.AdminToken = TxtAdminToken.Text.Trim();
        s.RemoteAddress = TxtRemoteAddress.Text.Trim();
        s.RemotePort = int.TryParse(TxtRemotePort.Text, out var p) ? p : 5231;
        s.GameRoot = string.IsNullOrWhiteSpace(TxtGameRoot.Text) ? null : TxtGameRoot.Text.Trim();
        _settings.Save();

        TxtSaveHint.Text = "已保存 ✓";
        LogService.Instance.Info("设置已保存");
        LoadSettings();
    }

    private void BrowseRoot_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "选择 kds 游戏根目录（包含 kards 子目录的目录）",
            Multiselect = false,
        };
        if (dialog.ShowDialog() == true)
        {
            TxtGameRoot.Text = dialog.FolderName;
        }
    }

    private void ResetRoot_Click(object sender, RoutedEventArgs e)
    {
        TxtGameRoot.Text = "";
        TxtSaveHint.Text = "将恢复自动探测（保存后生效）";
    }
}
