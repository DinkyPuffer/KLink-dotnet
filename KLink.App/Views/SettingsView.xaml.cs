using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using KLink.App.Services;
using Microsoft.Win32;

namespace KLink.App.Views;

public partial class SettingsView : UserControl
{
    private readonly SettingsService _settings = SettingsService.Instance;

    /// <summary>是否有未保存的修改（离开设置页时提示）。</summary>
    public bool IsDirty { get; private set; }

    private bool _loading;

    public SettingsView()
    {
        InitializeComponent();
        // 跟踪所有输入控件的修改
        foreach (var tb in new[] { TxtRoomName, TxtHostName, TxtPlayerName, TxtAdminToken, TxtAdminDisplayName,
                                   TxtRemoteAddress, TxtRemotePort, TxtGameRoot,
                                   TxtVersionPak, TxtGameVersion })
            tb.TextChanged += (_, _) => MarkDirty();
        LoadSettings();
    }

    private void MarkDirty()
    {
        if (!_loading)
            IsDirty = true;
    }

    /// <summary>保存当前设置（离开设置页时“保存”按钮路径）。</summary>
    public void SaveNow() => SaveSettings_Click(this, new RoutedEventArgs());

    /// <summary>放弃未保存的修改，重新载入磁盘配置。</summary>
    public void Discard()
    {
        LoadSettings();
        IsDirty = false;
    }

    private void LoadSettings()
    {
        _loading = true;
        try
        {
            var s = _settings.Settings;
            TxtRoomName.Text = s.RoomName;
            TxtHostName.Text = s.HostName;
            TxtPlayerName.Text = s.PreferredPlayerName;
            TxtAdminToken.Text = s.AdminToken;
            TxtAdminDisplayName.Text = s.AdminDisplayName;
            TxtRemoteAddress.Text = s.RemoteAddress;
            TxtRemotePort.Text = s.RemotePort.ToString();
            TxtRemoteWsPort.Text = s.RemoteWsPort.ToString();
            TxtGameRoot.Text = s.GameRoot ?? "";
            TxtVersionPak.Text = string.IsNullOrWhiteSpace(s.VersionPakTemplate)
                ? PakVersionPatcher.DefaultTemplatePath
                : s.VersionPakTemplate;
            TxtGameVersion.Text = s.GameVersion;

            // 主题
            if (s.Theme == "light")
            {
                if (ThemeLight is not null) ThemeLight.IsChecked = true;
            }
            else
            {
                if (ThemeDark is not null) ThemeDark.IsChecked = true;
            }

            // 背景
            if (BgNone is not null && BgImage is not null && BgVideo is not null)
            {
                switch (s.BackgroundType)
                {
                    case "image": BgImage.IsChecked = true; break;
                    case "video": BgVideo.IsChecked = true; break;
                    default: BgNone.IsChecked = true; break;
                }
                TxtBgPath.Text = string.IsNullOrEmpty(s.BackgroundPath) ? "" : s.BackgroundPath;
            }

            var root = _settings.ResolveGameRoot();
            TxtDetectedRoot.Text = root is null
                ? "⚠ 未定位到 kds 目录（请手动指定，或将启动器放在 kds 同级/上级目录）"
                : $"✔ 已定位：{root}";
        }
        finally
        {
            _loading = false;
        }
    }

    private void Theme_Changed(object sender, RoutedEventArgs e)
    {
        if (ThemeDark is null || ThemeLight is null)
            return; // InitializeComponent 期间的事件早触发保护
        MarkDirty();
        string theme = ThemeLight.IsChecked == true ? "light" : "dark";
        if (System.Windows.Application.Current is App app)
            app.ApplyTheme(theme);
        // 主题变化后刷新侧栏/标题栏透明度色（背景层色随主题）
        ApplyBackgroundNow();
        LogService.Instance.Info($"主题已切换：{(theme == "light" ? "Moon Light（浅色）" : "Deep Dark（深色）")}");
    }

    // ==================== 背景 ====================

    private void Bg_Changed(object sender, RoutedEventArgs e)
    {
        if (BgNone is null || BgImage is null || BgVideo is null)
            return; // InitializeComponent 期间的事件早触发保护
        MarkDirty();
        var s = _settings.Settings;
        s.BackgroundType = BgImage.IsChecked == true ? "image"
            : BgVideo.IsChecked == true ? "video" : "none";
        ApplyBackgroundNow();
    }

    private void PickBg_Click(object sender, RoutedEventArgs e)
    {
        if (BgNone is null || BgImage is null || BgVideo is null)
            return;
        bool video = BgVideo.IsChecked == true;
        var dialog = new OpenFileDialog
        {
            Title = video ? "选择背景视频" : "选择背景图片",
            Filter = video
                ? "视频文件 (*.mp4;*.wmv;*.avi;*.mkv)|*.mp4;*.wmv;*.avi;*.mkv|所有文件 (*.*)|*.*"
                : "图片文件 (*.png;*.jpg;*.jpeg;*.bmp;*.webp)|*.png;*.jpg;*.jpeg;*.bmp;*.webp|所有文件 (*.*)|*.*",
        };
        if (dialog.ShowDialog() != true)
            return;
        _settings.Settings.BackgroundPath = dialog.FileName;
        if (BgImage.IsChecked == true || BgVideo.IsChecked == true)
            _settings.Settings.BackgroundType = BgVideo.IsChecked == true ? "video" : "image";
        TxtBgPath.Text = dialog.FileName;
        ApplyBackgroundNow();
        MarkDirty();
    }

    private void ApplyBackgroundNow()
    {
        if (System.Windows.Application.Current.MainWindow is MainWindow window)
            window.ApplyBackground();
    }

    private void SaveSettings_Click(object sender, RoutedEventArgs e)
    {
        var s = _settings.Settings;
        s.RoomName = TxtRoomName.Text.Trim();
        s.HostName = TxtHostName.Text.Trim();
        s.PreferredPlayerName = TxtPlayerName.Text.Trim();
        s.AdminToken = TxtAdminToken.Text.Trim();
        s.AdminDisplayName = TxtAdminDisplayName.Text.Trim();
        s.RemoteAddress = TxtRemoteAddress.Text.Trim();
        s.RemotePort = int.TryParse(TxtRemotePort.Text, out var p) ? p : 5231;
        s.RemoteWsPort = int.TryParse(TxtRemoteWsPort.Text, out var wp) ? wp : 5232;
        s.GameRoot = string.IsNullOrWhiteSpace(TxtGameRoot.Text) ? null : TxtGameRoot.Text.Trim();
        // 手动指定与默认路径一致时视为未指定（走内置模板自动提取）
        var versionPak = TxtVersionPak.Text.Trim();
        s.VersionPakTemplate = string.IsNullOrWhiteSpace(versionPak)
            || string.Equals(versionPak, PakVersionPatcher.DefaultTemplatePath, StringComparison.OrdinalIgnoreCase)
                ? null : versionPak;
        s.GameVersion = string.IsNullOrWhiteSpace(TxtGameVersion.Text) ? "KLink 29452.29452" : TxtGameVersion.Text.Trim();
        _settings.Save();

        IsDirty = false;
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

    // ==================== 版本补丁（功能一） ====================

    private void BrowseVersionPak_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "选择版本补丁模板 pak",
            Filter = "PAK 文件 (*.pak)|*.pak|所有文件 (*.*)|*.*",
        };
        if (dialog.ShowDialog() == true)
        {
            TxtVersionPak.Text = dialog.FileName;
        }
    }

    // ==================== 管理员界面 ====================

    // ==================== 关于 ====================

    /// <summary>打开超链接（外部浏览器跳转）。</summary>
    private void OpenUrl_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Hyperlink { Tag: string url } && Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                LogService.Instance.Error($"打开链接失败：{ex.Message}");
            }
        }
    }
}
