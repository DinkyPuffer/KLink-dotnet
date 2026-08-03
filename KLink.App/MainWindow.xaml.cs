using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using KLink.App.Services;
using KLink.App.Views;
using Microsoft.Win32;

namespace KLink.App;

/// <summary>OLE 拖放目标接口（IDropTarget）。</summary>
[System.Runtime.InteropServices.ComImport]
[System.Runtime.InteropServices.Guid("00000122-0000-0000-C000-000000000046")]
[System.Runtime.InteropServices.InterfaceType(System.Runtime.InteropServices.ComInterfaceType.InterfaceIsIUnknown)]
public interface IOleDropTarget
{
    [System.Runtime.InteropServices.PreserveSig]
    int DragEnter(IntPtr pDataObj, int grfKeyState, POINTL pt, ref int pdwEffect);

    [System.Runtime.InteropServices.PreserveSig]
    int DragOver(int grfKeyState, POINTL pt, ref int pdwEffect);

    [System.Runtime.InteropServices.PreserveSig]
    int DragLeave();

    [System.Runtime.InteropServices.PreserveSig]
    int Drop(IntPtr pDataObj, int grfKeyState, POINTL pt, ref int pdwEffect);
}

[System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
public struct POINTL
{
    public int X;
    public int Y;
}

[System.Runtime.InteropServices.ComVisible(true)]
public partial class MainWindow : Wpf.Ui.Controls.FluentWindow, IOleDropTarget
{
    private readonly ServerView _serverView = new();
    private readonly ModsView _modsView = new();
    private readonly RoomsView _roomsView = new();
    private readonly SettingsView _settingsView = new();
    private readonly CardManagerView _cardsView = new();
    private readonly ConsoleView _consoleView = new();
    private readonly GameProcessService _game = new(SettingsService.Instance);
    private readonly KLinkService _service = KLinkService.Instance;
    private readonly System.Windows.Threading.DispatcherTimer _statusTimer;

    public MainWindow()
    {
        InitializeComponent();

        _mouseProc = MouseHookProc;
        SourceInitialized += (_, _) =>
        {
            var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            // OLE 拖放注册：覆盖 WPF 内部注册，窗口级接收所有拖放（任意位置、无元素路由问题）
            try
            {
                RevokeDragDrop(hwnd);
                RegisterDragDrop(hwnd, this);
            }
            catch (Exception ex)
            {
                LogService.Instance.Warn($"OLE 拖放注册失败：{ex.Message}");
            }
            // 全局低级鼠标钩子：检测鼠标靠近窗口边缘（含窗口外），驱动边缘辉光
            _mouseHook = SetWindowsHookEx(WH_MOUSE_LL, _mouseProc, GetModuleHandle(null), 0);
        };
        Closed += (_, _) =>
        {
            try { RevokeDragDrop(new System.Windows.Interop.WindowInteropHelper(this).Handle); } catch { }
        };
        Closed += (_, _) =>
        {
            if (_mouseHook != IntPtr.Zero)
                UnhookWindowsHookEx(_mouseHook);
        };

        NavFrame.Navigate(_serverView);
        Nav_Checked(NavServer, new RoutedEventArgs());

        // 拖放兜底：即使子元素处理了 DragOver（Handled=true 截断冒泡），
        // 窗口级 handledEventsToo 注册仍能收到并统一处理文件拖放
        AddHandler(UIElement.DragOverEvent, new DragEventHandler(Window_DragOver_Handled), true);
        AddHandler(UIElement.DropEvent, new DragEventHandler(Window_Drop_Handled), true);

        _game.Exited += () => Dispatcher.BeginInvoke(RefreshSidebarStatus);
        _service.StateChanged += () => Dispatcher.BeginInvoke(RefreshSidebarStatus);

        // Logo 版本号
        var version = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
        VersionText.Text = version is null ? "" : $"v{version.Major}.{version.Minor}.{version.Build}";

        // 应用自定义背景
        ApplyBackground();

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

    // ==================== OLE 拖放（IDropTarget 窗口级注册，绕开 WPF 元素路由） ====================

    private const int DROPEFFECT_NONE = 0;
    private const int DROPEFFECT_COPY = 1;

    [System.Runtime.InteropServices.DllImport("ole32.dll")]
    private static extern int RegisterDragDrop(IntPtr hwnd, IOleDropTarget pDropTarget);

    [System.Runtime.InteropServices.DllImport("ole32.dll")]
    private static extern int RevokeDragDrop(IntPtr hwnd);

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

    private async void PageHostTo(string? tag)
    {
        // 离开设置页时若有未保存修改，询问是否保存
        if (tag != "settings" && _settingsView.IsDirty)
        {
            var box = new Wpf.Ui.Controls.MessageBox
            {
                Title = "KLink-dotnet",
                Content = "设置已修改，是否保存？",
                PrimaryButtonText = "保存",
                SecondaryButtonText = "不保存",
                CloseButtonText = "取消",
            };
            var choice = await box.ShowDialogAsync();
            if (choice == Wpf.Ui.Controls.MessageBoxResult.None)
            {
                NavSettings.IsChecked = true; // 留在设置页
                return;
            }
            if (choice == Wpf.Ui.Controls.MessageBoxResult.Primary)
                _settingsView.SaveNow();
            else
                _settingsView.Discard();
        }

        switch (tag)
        {
            case "server": NavFrame.Navigate(_serverView); break;
            case "mods": NavFrame.Navigate(_modsView); break;
            case "rooms": NavFrame.Navigate(_roomsView); break;
            case "settings": NavFrame.Navigate(_settingsView); break;
            case "cards": NavFrame.Navigate(_cardsView); break;
            case "console": NavFrame.Navigate(_consoleView); break;
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
            case "cards": NavCards.IsChecked = true; break;
            case "console": NavConsole.IsChecked = true; break;
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

    // ==================== 自定义背景 ====================

    /// <summary>按设置应用背景（图片/视频/无），设置页改动后调用。</summary>
    public void ApplyBackground()
    {
        var s = SettingsService.Instance.Settings;
        var path = s.BackgroundPath;
        bool hasFile = !string.IsNullOrWhiteSpace(path) && File.Exists(path);

        BgVideo.Stop();
        BgImage.Source = null;

        switch (s.BackgroundType)
        {
            case "image" when hasFile:
                try
                {
                    BgImage.Source = new System.Windows.Media.Imaging.BitmapImage(new Uri(path!));
                    BgImage.Visibility = Visibility.Visible;
                    BgVideo.Visibility = Visibility.Collapsed;
                    BgDim.Visibility = Visibility.Visible;
                }
                catch { ClearBackground(); }
                break;
            case "video" when hasFile:
                try
                {
                    BgVideo.Source = new Uri(path!);
                    BgVideo.Visibility = Visibility.Visible;
                    BgImage.Visibility = Visibility.Collapsed;
                    BgDim.Visibility = Visibility.Visible;
                    BgVideo.Play();
                }
                catch { ClearBackground(); }
                break;
            default:
                ClearBackground();
                break;
        }

        // 有背景时侧栏/标题栏半透明（透出背景），无背景时恢复不透明
        SetTranslucency(s.BackgroundType is "image" or "video" && hasFile);
    }

    /// <summary>
    /// 半透明切换：修改主题合并字典中的 brush（主题切换时字典整体替换，不会残留旧色），
    /// 覆盖侧栏/标题栏/卡片/输入框等整个 UI。
    /// </summary>
    private void SetTranslucency(bool translucent)
    {
        byte alpha = translucent ? (byte)190 : (byte)255;
        var keys = new[]
        {
            "Brush.BgBase", "Brush.BgElevated", "Brush.BgSurface", "Brush.BgOverlay",
            "CardBackgroundFillColorDefaultBrush", "CardBackgroundFillColorSecondaryBrush",
            "ControlFillColorDefaultBrush", "ControlFillColorSecondaryBrush",
            "LayerFillColorDefaultBrush",
        };

        // 清理之前可能残留的本地覆盖项（避免挡住主题字典的颜色）
        foreach (var key in keys)
            Application.Current.Resources.Remove(key);

        // 修改当前主题合并字典中的 brush
        foreach (var key in keys)
        {
            try
            {
                foreach (var dict in Application.Current.Resources.MergedDictionaries)
                {
                    if (dict.Contains(key) && dict[key] is SolidColorBrush b)
                    {
                        dict[key] = new SolidColorBrush(
                            Color.FromArgb(alpha, b.Color.R, b.Color.G, b.Color.B));
                        break;
                    }
                }
            }
            catch
            {
                // 个别字典（如 WPF UI 内部）可能不允许写入，忽略
            }
        }
    }

    private void ClearBackground()
    {
        BgImage.Source = null;
        BgImage.Visibility = Visibility.Collapsed;
        BgVideo.Stop();
        BgVideo.Source = null;
        BgVideo.Visibility = Visibility.Collapsed;
        BgDim.Visibility = Visibility.Collapsed;
    }

    private void BgVideo_MediaEnded(object sender, RoutedEventArgs e)
    {
        // 循环播放背景视频
        BgVideo.Position = TimeSpan.Zero;
        BgVideo.Play();
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
        // 服务器未运行或游戏已运行时按钮不可点
        BtnLaunchGame.IsEnabled = running && !gameRunning;
        BtnLaunchGame.Opacity = running && !gameRunning ? 1.0 : 0.45;
    }

    private async void LaunchGame_Click(object sender, RoutedEventArgs e)
    {
        // 未启动服务器时禁止进游戏（本地/局域网需要服务器；远程模式启动代理后 ServerRunning 为 true）
        if (!_service.ServerRunning)
        {
            await new Wpf.Ui.Controls.MessageBox
            {
                Title = "KLink-dotnet",
                Content = "请先启动服务器，再进入游戏。",
            }.ShowDialogAsync();
            return;
        }

        // 版本补丁（功能一）：进入游戏前把模板 pak 复制/改写版本号后写入 Paks 目录
        string? patchError = PrepareVersionPak();
        if (patchError is not null)
        {
            await new Wpf.Ui.Controls.MessageBox
            {
                Title = "KLink-dotnet",
                Content = patchError,
            }.ShowDialogAsync();
            // 不阻止启动，由用户自行决定
        }

        string? error = _game.Start();
        if (error is not null)
        {
            await new Wpf.Ui.Controls.MessageBox
            {
                Title = "KLink-dotnet",
                Content = error,
            }.ShowDialogAsync();
            return;
        }
        RefreshSidebarStatus();
    }

    /// <summary>
    /// 生成版本补丁 pak：模板已配置时，复制到 Paks 目录；
    /// 远程/局域网模式等长替换版本号，本地模式直接复制（不改版本）。
    /// 返回错误信息；未配置模板返回 null（跳过）。
    /// </summary>
    private string? PrepareVersionPak()
    {
        string? template = PakVersionPatcher.ResolveTemplatePath();
        if (template is null)
            return null; // 无可用模板（内置提取失败且未手动指定），跳过

        string? pakDir = SettingsService.Instance.PaksDirectory;
        if (pakDir is null)
            return "未定位游戏 Paks 目录，无法注入版本补丁（请在设置页指定 kds 目录）";

        string output = Path.Combine(pakDir, PakVersionPatcher.OutputName(template));
        var settings = SettingsService.Instance.Settings;
        if (_service.CurrentMode == "local")
        {
            // 本地模式：直接复制，不改版本号
            try
            {
                File.Copy(template, output, overwrite: true);
                LogService.Instance.Info($"版本补丁已复制：{output}");
                return null;
            }
            catch (Exception ex)
            {
                return $"复制版本补丁失败：{ex.Message}";
            }
        }
        return PakVersionPatcher.Patch(template, settings.GameVersion, output);
    }

    // ==================== 拖拽安装模组 ====================

    /// <summary>判断拖入内容是否含 pak（文件直接判断，文件夹视为可能含 pak，Drop 时再递归解析）。</summary>
    private static bool HasPakDragData(IDataObject data)
    {
        if (!data.GetDataPresent(DataFormats.FileDrop))
            return false;
        try
        {
            var files = (string[]?)data.GetData(DataFormats.FileDrop) ?? Array.Empty<string>();
            return files.Any(f => f.EndsWith(".pak", StringComparison.OrdinalIgnoreCase)
                || Directory.Exists(f));
        }
        catch
        {
            return false;
        }
    }

    /// <summary>根 Grid 拖放处理（RootGrid 为全窗口唯一拖放目标，任意位置拖入都会命中）。</summary>
    private void RootGrid_DragOver(object sender, DragEventArgs e) => Window_PreviewDragOver(sender, e);

    private async void RootGrid_Drop(object sender, DragEventArgs e) => await HandleDropCore(e);

    /// <summary>拖拽判定使用隧道事件（Preview*），避免页面内 TextBox 等控件拦截拖放。</summary>
    private void Window_PreviewDragOver(object sender, DragEventArgs e)
    {
        bool hasPak = HasPakDragData(e.Data);
        e.Effects = hasPak ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true; // 阻止子控件（TextBox 等）接收拖放
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
            // 非 pak 内容：立即隐藏提示，避免拖放结束时提示层残留
            DropOverlay.Visibility = Visibility.Collapsed;
        }
    }

    private void Window_DragLeave(object sender, DragEventArgs e)
    {
        try
        {
            var pos = e.GetPosition(this);
            if (pos.X < 0 || pos.Y < 0 || pos.X > ActualWidth || pos.Y > ActualHeight)
                DropOverlay.Visibility = Visibility.Collapsed;
        }
        catch
        {
            // 个别拖放场景 GetPosition 不可用，直接隐藏
            DropOverlay.Visibility = Visibility.Collapsed;
        }
    }

    /// <summary>全局类级拖放转发入口（App 注册的 TextBox 兜底，解决 TextBox 吞掉拖放事件）。</summary>
    internal void HandleFileDragOver(DragEventArgs e) => Window_PreviewDragOver(this, e);

    internal void HandleFileDrop(DragEventArgs e) => _ = HandleDropCore(e);

    /// <summary>handledEventsToo 兜底：冒泡 DragOver 即使被子元素 Handled 也会到达窗口。</summary>
    private void Window_DragOver_Handled(object sender, DragEventArgs e) => Window_PreviewDragOver(sender, e);

    private async void Window_Drop_Handled(object sender, DragEventArgs e) => await HandleDropCore(e);

    private async void Window_PreviewDrop(object sender, DragEventArgs e) => await HandleDropCore(e);

    // ==================== IOleDropTarget 实现（窗口级 OLE 拖放） ====================

    private System.Windows.IDataObject? _dragData;
    private bool _dragHasPak;

    int IOleDropTarget.DragEnter(IntPtr pDataObj, int grfKeyState, POINTL pt, ref int pdwEffect)
    {
        _dragHasPak = TryReadDragData(pDataObj);
        Dispatcher.BeginInvoke(UpdateDropOverlay);
        pdwEffect = _dragHasPak ? DROPEFFECT_COPY : DROPEFFECT_NONE;
        return 0; // S_OK
    }

    int IOleDropTarget.DragOver(int grfKeyState, POINTL pt, ref int pdwEffect)
    {
        pdwEffect = _dragHasPak ? DROPEFFECT_COPY : DROPEFFECT_NONE;
        return 0;
    }

    int IOleDropTarget.DragLeave()
    {
        _dragData = null;
        Dispatcher.BeginInvoke(() => DropOverlay.Visibility = Visibility.Collapsed);
        return 0;
    }

    int IOleDropTarget.Drop(IntPtr pDataObj, int grfKeyState, POINTL pt, ref int pdwEffect)
    {
        Dispatcher.BeginInvoke(() => DropOverlay.Visibility = Visibility.Collapsed);
        var files = ReadDroppedFiles(pDataObj);
        pdwEffect = files.Count > 0 ? DROPEFFECT_COPY : DROPEFFECT_NONE;
        if (files.Count > 0)
            Dispatcher.BeginInvoke(() => _ = HandleDroppedFiles(files));
        return 0;
    }

    /// <summary>解析 OLE IDataObject 为托管 DataObject 并判断是否含 pak（供 DragEnter 缓存）。</summary>
    private bool TryReadDragData(IntPtr pDataObj)
    {
        try
        {
            if (pDataObj == IntPtr.Zero)
                return false;
            var comData = (System.Runtime.InteropServices.ComTypes.IDataObject)
                System.Runtime.InteropServices.Marshal.GetObjectForIUnknown(pDataObj);
            _dragData = new System.Windows.DataObject(comData);
            return HasPakDragData(_dragData);
        }
        catch
        {
            _dragData = null;
            return false;
        }
    }

    /// <summary>从 OLE IDataObject 读取拖入的文件路径列表。</summary>
    private static List<string> ReadDroppedFiles(IntPtr pDataObj)
    {
        var files = new List<string>();
        try
        {
            if (pDataObj == IntPtr.Zero)
                return files;
            var comData = (System.Runtime.InteropServices.ComTypes.IDataObject)
                System.Runtime.InteropServices.Marshal.GetObjectForIUnknown(pDataObj);
            var data = new System.Windows.DataObject(comData);
            if (data.GetDataPresent(DataFormats.FileDrop) && data.GetData(DataFormats.FileDrop) is string[] arr)
                files.AddRange(arr);
        }
        catch (Exception ex)
        {
            LogService.Instance.Warn($"读取拖放数据失败：{ex.Message}");
        }
        return files;
    }

    /// <summary>按 DragEnter 缓存的结果更新提示层。</summary>
    private void UpdateDropOverlay()
    {
        if (_dragHasPak)
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

    /// <summary>处理拖入的文件列表（Win32 WM_DROPFILES 与 WPF Drop 共用）：收集 pak → 后台安装。</summary>
    private async Task HandleDroppedFiles(List<string> files)
    {
        // 收集 pak：文件直接加入，文件夹递归查找
        var paks = new List<string>();
        foreach (string file in files)
        {
            try
            {
                if (file.EndsWith(".pak", StringComparison.OrdinalIgnoreCase))
                    paks.Add(file);
                else if (Directory.Exists(file))
                    paks.AddRange(Directory.EnumerateFiles(file, "*.pak", SearchOption.AllDirectories));
            }
            catch (Exception ex)
            {
                LogService.Instance.Warn($"解析拖入内容失败：{file}（{ex.Message}）");
            }
        }
        if (paks.Count == 0)
        {
            await new Wpf.Ui.Controls.MessageBox
            {
                Title = "KLink-dotnet",
                Content = "未检测到 .pak 文件（支持拖入模组包或包含 pak 的文件夹）",
            }.ShowDialogAsync();
            return;
        }

        string? pakPath = SettingsService.Instance.PaksDirectory;
        if (pakPath is null)
        {
            await new Wpf.Ui.Controls.MessageBox
            {
                Title = "KLink-dotnet",
                Content = "未定位游戏目录，请先在设置页指定后重试",
            }.ShowDialogAsync();
            SelectNavItem("settings");
            return;
        }

        _modsView.InstallPaks(paks);
        SelectNavItem("mods");
    }

    private async Task HandleDropCore(DragEventArgs e)
    {
        DropOverlay.Visibility = Visibility.Collapsed;
        if (!e.Data.GetDataPresent(DataFormats.FileDrop))
            return;
        var files = (string[]?)e.Data.GetData(DataFormats.FileDrop) ?? Array.Empty<string>();
        await HandleDroppedFiles(files.ToList());
    }
}
