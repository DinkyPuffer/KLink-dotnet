using System.IO;
using System.Windows;
using System.Windows.Controls;
using KLink.App.Services;

namespace KLink.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        // 全局异常兜底：UI 线程异常记录日志并阻止崩溃（不闪退）
        DispatcherUnhandledException += (_, args) =>
        {
            try
            {
                File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "crash.log"), args.Exception.ToString());
                LogService.Instance.Error($"未处理异常（已拦截）：{args.Exception.Message}");
            }
            catch { }
            args.Handled = true;
        };

        // 拖放修复：WPF 拖放事件只发给鼠标下 AllowDrop=true 的最近元素（不走隧道路由），
        // TextBox、部分 WPF-UI/自定义控件默认 AllowDrop=true 会吞掉文件拖放（禁止图标）。
        // 统一在加载时禁用除 RootGrid 外所有元素的 AllowDrop，让拖放唯一命中根 Grid
        EventManager.RegisterClassHandler(typeof(UIElement), FrameworkElement.LoadedEvent,
            new RoutedEventHandler((s, _) =>
            {
                if (s is UIElement el && el is not Window)
                {
                    // RootGrid 是唯一允许的拖放目标（可视树顶层，任意位置都会命中它）
                    if (Application.Current.MainWindow is MainWindow w
                        && ReferenceEquals(el, w.FindName("RootGrid")))
                        return;
                    el.AllowDrop = false;
                }
            }), true);
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            try { File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "crash.log"), args.ExceptionObject.ToString()); } catch { }
        };
        SettingsService.Instance.Load();
        // 应用保存的主题
        ApplyTheme(SettingsService.Instance.Settings.Theme);
        // fyserver 子进程的输出由 FyServerHost 直接写入 LogService（见 Services/FyServerHost.cs）
        LogService.Instance.Info($"KLink 启动器启动（版本 {Environment.Version}）");

        // 测试钩子：置 KLINK_TEST_AUTOSTART=1 时自动启动服务器。
        //
        // 模式取 KLINK_TEST_START_MODE（local / lan / remote），缺省用 config.json 里记住的 lastMode，
        // 再缺省才是 local —— 以前写死 local，导致局域网模式那条路径（绑 0.0.0.0、
        // 防火墙放通、UDP 广播）在自动化里根本测不到。
        if (Environment.GetEnvironmentVariable("KLINK_TEST_AUTOSTART") is { Length: > 0 })
        {
            var s = SettingsService.Instance.Settings;
            string mode = Environment.GetEnvironmentVariable("KLINK_TEST_START_MODE") is { Length: > 0 } forced
                ? forced
                : string.IsNullOrWhiteSpace(s.LastMode) ? "local" : s.LastMode;
            string? error = KLinkService.Instance.StartServer(
                mode, s.RoomName, s.HostName, s.RemoteAddress, s.RemotePort,
                s.AdminToken, s.PreferredPlayerName);
            LogService.Instance.Info(error is null
                ? $"[测试] 已自动启动服务器（模式 {mode}）"
                : $"[测试] 自动启动失败：{error}");
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        // 必须先停服务器：fyserver 是独立子进程，不会随启动器退出而消失。
        // 不停的话会残留进程占着 5231，导致下次启动报"端口被占用"，
        // 而且它跳过检查点落盘（最近写入会丢）。
        try
        {
            if (KLinkService.Instance.ServerRunning)
            {
                LogService.Instance.Info("启动器退出：正在停止服务器…");
                KLinkService.Instance.StopServer();
            }
        }
        catch (Exception ex)
        {
            LogService.Instance.Warn($"退出时停止服务器失败：{ex.Message}");
        }

        SettingsService.Instance.Save();

        // 测试钩子：置 KLINK_TEST_EXPORT_LOG=<路径> 时，退出前把内存日志落盘。
        // 日志平时只在内存里（退出即丢），没有它就没法自动验证"子进程输出编码是否正确"
        // 这类问题 —— 之前只能靠用户截图判断乱码，所以补上。
        if (Environment.GetEnvironmentVariable("KLINK_TEST_EXPORT_LOG") is { Length: > 0 } logPath)
        {
            try { LogService.Instance.Export(logPath); } catch { /* 测试钩子，失败不影响退出 */ }
        }

        base.OnExit(e);
    }

    /// <summary>
    /// 切换界面主题：替换覆盖色板字典（深/浅两套）+ 切换 WPF UI 主题。
    /// DynamicResource 引用会在字典替换后自动更新，无需重建窗口。
    /// </summary>
    public void ApplyTheme(string theme)
    {
        bool light = theme == "light";
        var dictionaries = Current.Resources.MergedDictionaries;

        // 替换我们自己的主题覆盖字典
        int idx = -1;
        for (int i = 0; i < dictionaries.Count; i++)
        {
            string source = dictionaries[i].Source?.OriginalString ?? "";
            if (source.Contains("ThemeDark.xaml") || source.Contains("ThemeLight.xaml"))
            {
                idx = i;
                break;
            }
        }
        var newDict = new ResourceDictionary
        {
            Source = new Uri($"Resources/Theme{(light ? "Light" : "Dark")}.xaml", UriKind.Relative),
        };
        if (idx >= 0)
            dictionaries[idx] = newDict;
        else
            dictionaries.Add(newDict);

        // WPF UI 原生主题
        Wpf.Ui.Appearance.ApplicationThemeManager.Apply(
            light ? Wpf.Ui.Appearance.ApplicationTheme.Light : Wpf.Ui.Appearance.ApplicationTheme.Dark);

        SettingsService.Instance.Settings.Theme = theme;
    }
}
