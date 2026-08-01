using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using KLink.App.Services;
using KLink.App.Views;
using Microsoft.Win32;

namespace KLink.App;

public partial class MainWindow : Wpf.Ui.Controls.FluentWindow
{
    private readonly ServerView _serverView = new();
    private readonly ModsView _modsView = new();
    private readonly RoomsView _roomsView = new();
    private readonly SettingsView _settingsView = new();
    private readonly GameProcessService _game = new(SettingsService.Instance);
    private readonly KLinkService _service = new(SettingsService.Instance);
    private readonly System.Windows.Threading.DispatcherTimer _statusTimer;

    public MainWindow()
    {
        InitializeComponent();

        _mouseProc = MouseHookProc;
        SourceInitialized += (_, _) =>
        {
            // 全局低级鼠标钩子：检测鼠标靠近窗口边缘（含窗口外），驱动边缘辉光
            _mouseHook = SetWindowsHookEx(WH_MOUSE_LL, _mouseProc, GetModuleHandle(null), 0);
        };
        Closed += (_, _) =>
        {
            if (_mouseHook != IntPtr.Zero)
                UnhookWindowsHookEx(_mouseHook);
        };

        NavFrame.Navigate(_serverView);
        Nav_Checked(NavServer, new RoutedEventArgs());

        _game.Exited += () => Dispatcher.BeginInvoke(RefreshSidebarStatus);
        _service.StateChanged += () => Dispatcher.BeginInvoke(RefreshSidebarStatus);

        StateChanged += (_, _) =>
        {
            try
            {
                // 最大化时禁用边框 resize：WPF UI 的边框命中测试（GetWindowBorderHitTestResult）
                // 对 CanMinimize/NoResize 返回 HTNOWHERE，顶部边框区变为客户区，
                // 让 TitleBar 的拖动逻辑覆盖整条标题栏（含最顶 1px），支持下拉还原。
                ResizeMode = WindowState == WindowState.Maximized
                    ? ResizeMode.CanMinimize
                    : ResizeMode.CanResize;
            }
            catch
            {
                // 个别 WPF 版本不允许运行时修改 ResizeMode，忽略
            }
        };

        SourceInitialized += (_, _) =>
        {
            var source = System.Windows.Interop.HwndSource.FromHwnd(
                new System.Windows.Interop.WindowInteropHelper(this).Handle);
            source?.AddHook(NcHitTestHook);
        };

        _statusTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _statusTimer.Tick += (_, _) => RefreshSidebarStatus();
        _statusTimer.Start();

        RefreshSidebarStatus();
    }

    // ==================== 标题栏 ====================

    private const int WM_NCLBUTTONDOWN = 0xA1;

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    private Point _dragStart;
    private bool _dragCandidate;

    private void Caption_MouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            ToggleMaximize();
            e.Handled = true;
            return;
        }
        if (e.LeftButton != System.Windows.Input.MouseButtonState.Pressed)
            return;
        // 点击的是标题栏按钮区域时跳过拖动
        if (FindAncestor<Button>(e.OriginalSource as DependencyObject) is not null)
            return;
        _dragStart = e.GetPosition(this);
        _dragCandidate = true;
    }

    private void Caption_MouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (!_dragCandidate || e.LeftButton != System.Windows.Input.MouseButtonState.Pressed)
            return;
        var pos = e.GetPosition(this);
        // 超过阈值才算拖动（单击/轻微抖动不触发）
        if (Math.Abs(pos.X - _dragStart.X) < 4 && Math.Abs(pos.Y - _dragStart.Y) < 4)
            return;
        _dragCandidate = false;

        var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        if (WindowState == WindowState.Maximized)
        {
            // 最大化：按鼠标位置比例还原，再交给系统拖动（保留 Aero Snap）
            double ratio = _dragStart.X / Math.Max(1, ActualWidth);
            var screen = PointToScreen(pos);
            WindowState = WindowState.Normal;
            UpdateMaximizeGlyph();
            Left = screen.X - ratio * ActualWidth;
            Top = screen.Y - 8;
        }
        // 交给系统标题栏拖动循环：原生支持下拉还原、Aero Snap、边缘停靠
        SendMessage(hwnd, WM_NCLBUTTONDOWN, (IntPtr)HTCAPTION, IntPtr.Zero);
        e.Handled = true;
    }

    private void Caption_MouseUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
        => _dragCandidate = false;

    private static T? FindAncestor<T>(DependencyObject? obj) where T : DependencyObject
    {
        while (obj is not null)
        {
            if (obj is T match)
                return match;
            obj = System.Windows.Media.VisualTreeHelper.GetParent(obj);
        }
        return null;
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void Maximize_Click(object sender, RoutedEventArgs e) => ToggleMaximize();

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void ToggleMaximize()
    {
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        UpdateMaximizeGlyph();
    }

    private void UpdateMaximizeGlyph()
        => MaximizeIcon.Symbol = WindowState == WindowState.Maximized
            ? Wpf.Ui.Controls.SymbolRegular.ArrowMinimize24
            : Wpf.Ui.Controls.SymbolRegular.Square24;

    // ==================== 最大化 / 顶部边缘处理 ====================

    private const int WM_NCHITTEST = 0x0084;
    private const int WM_GETMINMAXINFO = 0x0024;
    private const int HTCAPTION = 0x2;

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, int dwFlags);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct MINMAXINFO
    {
        public POINT ptReserved;
        public POINT ptMaxSize;
        public POINT ptMaxPosition;
        public POINT ptMinTrackSize;
        public POINT ptMaxTrackSize;
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public int dwFlags;
    }

    private IntPtr NcHitTestHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_GETMINMAXINFO)
        {
            // 最大化矩形限制为当前显示器工作区（物理像素）。
            // 修复 WindowChrome 最大化时窗口顶部/边缘偏移出屏幕，导致顶部边框区不可达。
            var mmi = System.Runtime.InteropServices.Marshal.PtrToStructure<MINMAXINFO>(lParam);
            var monitor = MonitorFromWindow(hwnd, 2 /* MONITOR_DEFAULTTONEAREST */);
            var mi = new MONITORINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<MONITORINFO>() };
            if (GetMonitorInfo(monitor, ref mi))
            {
                mmi.ptMaxPosition.X = mi.rcWork.Left;
                mmi.ptMaxPosition.Y = mi.rcWork.Top;
                mmi.ptMaxSize.X = mi.rcWork.Right - mi.rcWork.Left;
                mmi.ptMaxSize.Y = mi.rcWork.Bottom - mi.rcWork.Top;
                mmi.ptMaxTrackSize.X = mmi.ptMaxSize.X;
                mmi.ptMaxTrackSize.Y = mmi.ptMaxSize.Y;
                System.Runtime.InteropServices.Marshal.StructureToPtr(mmi, lParam, true);
                handled = true;
            }
            return IntPtr.Zero;
        }
        if (msg == WM_NCHITTEST && WindowState == WindowState.Maximized)
        {
            // 最大化时顶部 8px 改为 HTCAPTION（系统标题栏语义）：
            // 系统在最大化窗口标题栏上按下会自动还原并进入拖动（原生行为）。
            int screenX = lParam.ToInt32() & 0xFFFF;
            int screenY = (lParam.ToInt32() >> 16) & 0xFFFF;
            GetWindowRect(hwnd, out RECT rect);
            if (screenY - rect.Top < 8)
            {
                handled = true;
                return (IntPtr)HTCAPTION;
            }
        }
        return IntPtr.Zero;
    }

    private void Nav_Checked(object sender, RoutedEventArgs e)
    {
        if (NavFrame is null) return;
        PageHostTo((sender as RadioButton)?.Tag as string);
    }

    private void PageHostTo(string? tag)
    {
        switch (tag)
        {
            case "server": NavFrame.Navigate(_serverView); break;
            case "mods": NavFrame.Navigate(_modsView); break;
            case "rooms": NavFrame.Navigate(_roomsView); break;
            case "settings": NavFrame.Navigate(_settingsView); break;
        }
        // 页面切换淡入动画
        NavFrame.BeginAnimation(OpacityProperty,
            new System.Windows.Media.Animation.DoubleAnimation(0.2, 1.0, TimeSpan.FromMilliseconds(180)));
    }

    private void SelectNavItem(string tag)
    {
        switch (tag)
        {
            case "server": NavServer.IsChecked = true; break;
            case "mods": NavMods.IsChecked = true; break;
            case "rooms": NavRooms.IsChecked = true; break;
            case "settings": NavSettings.IsChecked = true; break;
        }
    }

    /// <summary>从房间列表加入远程房间：切到服务器页并自动连接。</summary>
    public void ConnectRemote(string address, int port)
    {
        SelectNavItem("server");
        _serverView.ConnectRemote(address, port);
    }

    // ==================== 窗口边缘辉光（感应式） ====================

    private const int WH_MOUSE_LL = 14;
    private const int WM_MOUSEMOVE = 0x0200;
    private const double EdgeProximityPx = 60;
    private readonly LowLevelMouseProc _mouseProc;
    private IntPtr _mouseHook;
    private DateTime _lastGlowUpdate = DateTime.MinValue;

    private delegate IntPtr LowLevelMouseProc(int nCode, IntPtr wParam, IntPtr lParam);

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelMouseProc lpfn, IntPtr hMod, uint dwThreadId);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Auto, EntryPoint = "GetModuleHandleW")]
    private static extern IntPtr GetModuleHandle(string lpModuleName);

    private IntPtr MouseHookProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && wParam == (IntPtr)WM_MOUSEMOVE)
        {
            var pos = System.Runtime.InteropServices.Marshal.PtrToStructure<POINT>(lParam);
            Dispatcher.BeginInvoke(() => UpdateEdgeGlow(pos.X, pos.Y));
        }
        return CallNextHookEx(_mouseHook, nCode, wParam, lParam);
    }

    private void UpdateEdgeGlow(int screenX, int screenY)
    {
        // 节流 30ms，避免高频重绘
        if ((DateTime.Now - _lastGlowUpdate).TotalMilliseconds < 30)
            return;
        _lastGlowUpdate = DateTime.Now;

        // 屏幕坐标（物理像素）→ 窗口坐标（DIP），统一空间计算到四条边的距离
        var p = PointFromScreen(new Point(screenX, screenY));
        double dTop = Math.Abs(p.Y);
        double dBottom = Math.Abs(ActualHeight - p.Y);
        double dLeft = Math.Abs(p.X);
        double dRight = Math.Abs(ActualWidth - p.X);

        // 以鼠标为圆心（半径 GlowRadius）：圆覆盖到的边缘段发光。
        // 光斑中心用相对各光边 Border 的本地坐标（上/下边高 6px，左/右边宽 6px）
        const double glowRadius = 250;
        UpdateGlowEdge(GlowTop, dTop <= glowRadius, new Point(p.X, 3));
        UpdateGlowEdge(GlowBottom, dBottom <= glowRadius, new Point(p.X, 3));
        UpdateGlowEdge(GlowLeft, dLeft <= glowRadius, new Point(3, p.Y));
        UpdateGlowEdge(GlowRight, dRight <= glowRadius, new Point(3, p.Y));
    }

    private void UpdateGlowEdge(System.Windows.Controls.Border border, bool on, Point center)
    {
        if (border.Background is System.Windows.Media.RadialGradientBrush brush)
        {
            brush.Center = center;
            brush.GradientOrigin = center;
        }
        double target = on ? 1.0 : 0.0;
        if (Math.Abs(border.Opacity - target) > 0.01)
        {
            border.BeginAnimation(System.Windows.UIElement.OpacityProperty,
                new System.Windows.Media.Animation.DoubleAnimation(target, TimeSpan.FromMilliseconds(180)));
        }
    }

    // ==================== 侧栏状态 ====================

    private void RefreshSidebarStatus()
    {
        bool running = _service.ServerRunning;
        bool gameRunning = _game.IsRunning;
        StatusDot.Fill = running
            ? (SolidColorBrush)FindResource("Brush.Success")
            : gameRunning
                ? (SolidColorBrush)FindResource("Brush.Emerald")
                : (SolidColorBrush)FindResource("Brush.TextTertiary");
        StatusText.Text = running ? "服务器运行中" : gameRunning ? "游戏运行中" : "未运行";
        LaunchGameText.Text = gameRunning ? "游戏运行中…" : "启动游戏";
        BtnLaunchGame.IsEnabled = !gameRunning;
    }

    private void LaunchGame_Click(object sender, RoutedEventArgs e)
    {
        string? error = _game.Start();
        if (error is not null)
        {
            new Wpf.Ui.Controls.MessageBox
            {
                Title = "启动游戏",
                Content = error,
            }.ShowDialog();
            return;
        }
        if (!_service.ServerRunning && _service.CurrentMode != "remote")
        {
            LogService.Instance.Warn("提示：本地/局域网模式下请先启动服务器再进入游戏");
        }
        RefreshSidebarStatus();
    }

    // ==================== 拖拽安装模组 ====================

    private void Window_DragOver(object sender, DragEventArgs e)
    {
        bool hasPak = e.Data.GetDataPresent(DataFormats.FileDrop)
            && ((string[]?)e.Data.GetData(DataFormats.FileDrop))
                ?.Any(f => f.EndsWith(".pak", StringComparison.OrdinalIgnoreCase)) == true;
        e.Effects = hasPak ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
        if (hasPak)
        {
            DropOverlay.Visibility = Visibility.Visible;
            string? pakPath = SettingsService.Instance.PaksDirectory;
            DropOverlayHint.Text = pakPath is null
                ? "未定位游戏目录，请先到设置页指定"
                : $"将安装到：{pakPath}";
        }
        else
        {
            DropOverlay.Visibility = Visibility.Collapsed;
        }
    }

    private void Window_DragLeave(object sender, DragEventArgs e)
    {
        var pos = e.GetPosition(this);
        if (pos.X < 0 || pos.Y < 0 || pos.X > ActualWidth || pos.Y > ActualHeight)
            DropOverlay.Visibility = Visibility.Collapsed;
    }

    private void Window_Drop(object sender, DragEventArgs e)
    {
        DropOverlay.Visibility = Visibility.Collapsed;
        if (!e.Data.GetDataPresent(DataFormats.FileDrop))
            return;
        var files = (string[]?)e.Data.GetData(DataFormats.FileDrop) ?? Array.Empty<string>();
        var paks = files.Where(f => f.EndsWith(".pak", StringComparison.OrdinalIgnoreCase)).ToList();
        if (paks.Count == 0)
        {
            new Wpf.Ui.Controls.MessageBox
            {
                Title = "KLink",
                Content = "未检测到 .pak 文件（支持拖入模组包自动安装）",
            }.ShowDialog();
            return;
        }

        string? pakPath = SettingsService.Instance.PaksDirectory;
        if (pakPath is null)
        {
            new Wpf.Ui.Controls.MessageBox
            {
                Title = "KLink",
                Content = "未定位游戏目录，请先在设置页指定后重试",
            }.ShowDialog();
            SelectNavItem("settings");
            return;
        }

        var manager = new ModManager(pakPath);
        int ok = 0, renamed = 0;
        var skipped = new List<string>();
        foreach (string file in paks)
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
            {
                new Wpf.Ui.Controls.MessageBox
                {
                    Title = "KLink",
                    Content = $"安装失败：{fileName}",
                }.ShowDialog();
            }
        }

        SelectNavItem("mods");
        _modsView.Refresh();

        var message = $"成功安装 {ok} 个模组。";
        if (renamed > 0)
            message += $"\n其中 {renamed} 个已自动补 _P 后缀（PC 端引擎只加载 _P 结尾的 pak）。";
        if (skipped.Count > 0)
            message += $"\n已跳过游戏本体包：{string.Join("、", skipped)}";
        if (ok > 0)
        {
            new Wpf.Ui.Controls.MessageBox
            {
                Title = "KLink",
                Content = message,
            }.ShowDialog();
        }
    }
}
