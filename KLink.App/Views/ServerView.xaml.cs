using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using KLink.App.Services;
using Microsoft.Win32;

namespace KLink.App.Views;

public partial class ServerView : UserControl
{
    private readonly KLinkService _service = new(SettingsService.Instance);
    private readonly DispatcherTimer _statusTimer;

    public ServerView()
    {
        InitializeComponent();

        // 恢复上次配置
        var s = SettingsService.Instance.Settings;
        TxtRoomName.Text = s.RoomName;
        TxtHostName.Text = s.HostName;
        TxtRemoteAddress.Text = s.RemoteAddress;
        TxtRemotePort.Text = s.RemotePort.ToString();
        switch (s.LastMode)
        {
            case "lan": ModeLan.IsChecked = true; break;
            case "remote": ModeRemote.IsChecked = true; break;
            default: ModeLocal.IsChecked = true; break;
        }
        UpdateModeFields();
        RefreshState();

        _statusTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _statusTimer.Tick += (_, _) => RefreshState();
        _statusTimer.Start();

        _service.StateChanged += () => Dispatcher.BeginInvoke(RefreshState);

        // 控制台日志
        LogService.Instance.HydrateSnapshot();
        LogService.Instance.LogAdded += OnLogAdded;
        Unloaded += (_, _) => LogService.Instance.LogAdded -= OnLogAdded;
    }

    // ==================== 模式 ====================

    private string CurrentMode => ModeLocal.IsChecked == true ? "local"
        : ModeLan.IsChecked == true ? "lan" : "remote";

    private void Mode_Checked(object sender, RoutedEventArgs e)
    {
        if (CardRemote is null) return; // InitializeComponent 期间的事件早触发保护
        UpdateModeFields();
    }

    private void UpdateModeFields()
    {
        bool remote = CurrentMode == "remote";
        CardRemote.Visibility = remote ? Visibility.Visible : Visibility.Collapsed;
        HeroMode.Text = CurrentMode switch
        {
            "local" => "本地",
            "lan" => "局域网",
            _ => "远程",
        };
        StartServerText.Text = remote ? "连接服务器" : "启动服务器";
    }

    // ==================== 启动 / 停止 ====================

    private void StartServer_Click(object sender, RoutedEventArgs e)
    {
        int.TryParse(TxtRemotePort.Text, out int port);
        string? error = _service.StartServer(
            CurrentMode,
            TxtRoomName.Text,
            TxtHostName.Text,
            TxtRemoteAddress.Text,
            port == 0 ? 5231 : port,
            SettingsService.Instance.Settings.AdminToken,
            SettingsService.Instance.Settings.PreferredPlayerName);
        if (error is not null)
        {
            HeroSubtitle.Text = error;
            HeroSubtitle.Foreground = (SolidColorBrush)FindResource("Brush.Danger");
        }
        RefreshState();
    }

    private async void StopServer_Click(object sender, RoutedEventArgs e)
    {
        BtnStopServer.IsEnabled = false;
        StopServerText.Text = "停止中…";
        await _service.StopServerAsync();
        StopServerText.Text = "停止服务器";
        RefreshState();
    }

    /// <summary>从房间列表加入：填入远程地址、切到远程模式并自动连接。</summary>
    public void ConnectRemote(string address, int port)
    {
        TxtRemoteAddress.Text = address;
        TxtRemotePort.Text = port.ToString();
        ModeRemote.IsChecked = true;
        UpdateModeFields();
        StartServer_Click(this, new RoutedEventArgs());
    }

    // ==================== 状态轮询 ====================

    private void RefreshState()
    {
        var status = _service.GetStatus();
        bool running = status["running"]?.GetValue<bool>() ?? false;
        string mode = status["mode"]?.GetValue<string>() ?? "none";

        if (running)
        {
            HeroTitle.Text = "服务器运行中";
            HeroSubtitle.Text = $"模式：{mode}";
            HeroSubtitle.Foreground = (SolidColorBrush)FindResource("Brush.TextTertiary");
            HeroDot.Fill = (SolidColorBrush)FindResource("Brush.Success");
            StartBreathing();
            BtnStartServer.IsEnabled = false;
            BtnStopServer.IsEnabled = true;
            int online = status["online_players"]?.GetValue<int>() ?? 0;
            int matches = status["matches"]?.GetValue<int>() ?? 0;
            HeroPlayers.Text = $"{online} 在线 / {matches} 对局";
            HeroPort.Text = mode == "remote"
                ? (status["remotePort"]?.GetValue<int>() ?? 5231).ToString()
                : "5231";
        }
        else
        {
            HeroTitle.Text = "服务器未启动";
            HeroSubtitle.Text = "选择运行模式并启动";
            HeroSubtitle.Foreground = (SolidColorBrush)FindResource("Brush.TextTertiary");
            HeroDot.Fill = (SolidColorBrush)FindResource("Brush.TextTertiary");
            StopBreathing();
            BtnStartServer.IsEnabled = true;
            BtnStopServer.IsEnabled = false;
            HeroPlayers.Text = "0";
            HeroPort.Text = "5231";
        }
    }

    /// <summary>状态点呼吸动画（运行时）。</summary>
    private void StartBreathing()
    {
        var anim = new System.Windows.Media.Animation.DoubleAnimation(0.4, 1.0, TimeSpan.FromMilliseconds(900))
        {
            AutoReverse = true,
            RepeatBehavior = System.Windows.Media.Animation.RepeatBehavior.Forever,
        };
        HeroDot.BeginAnimation(OpacityProperty, anim);
    }

    private void StopBreathing()
    {
        HeroDot.BeginAnimation(OpacityProperty, null);
        HeroDot.Opacity = 1;
    }

    // ==================== 控制台 ====================

    private void OnLogAdded(string line)
    {
        Dispatcher.BeginInvoke(() =>
        {
            var snapshot = LogService.Instance.Snapshot;
            if (snapshot.Count > 0)
                LogList.ScrollIntoView(snapshot[^1]);
        });
    }

    private void ExportLog_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            Title = "导出日志",
            Filter = "日志文件 (*.log)|*.log|文本文件 (*.txt)|*.txt",
            FileName = $"klink_log_{DateTime.Now:yyyyMMdd_HHmmss}.log",
        };
        if (dialog.ShowDialog() != true)
            return;
        try
        {
            LogService.Instance.Export(dialog.FileName);
            LogService.Instance.Info($"日志已导出：{dialog.FileName}");
            var result = new Wpf.Ui.Controls.MessageBox
            {
                Title = "KLink",
                Content = $"日志已导出到：\n{dialog.FileName}\n\n是否打开所在文件夹？",
            };
            if (result.ShowDialog() == true)
                System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{dialog.FileName}\"");
        }
        catch (Exception ex)
        {
            LogService.Instance.Error($"导出日志失败：{ex.Message}");
        }
    }

    private void ClearLog_Click(object sender, RoutedEventArgs e)
    {
        LogService.Instance.Clear();
    }
}
