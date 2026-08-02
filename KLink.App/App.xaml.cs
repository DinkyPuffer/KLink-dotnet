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
        // 服务器诊断日志转发到 UI 日志区
        KLink.Server.ServerEvents.OnLog += message => LogService.Instance.Write("SERVER", message);
        LogService.Instance.Info($"KLink 启动器启动（版本 {Environment.Version}）");
    }

    protected override void OnExit(ExitEventArgs e)
    {
        SettingsService.Instance.Save();
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
