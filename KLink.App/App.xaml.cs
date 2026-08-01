using System.IO;
using System.Windows;
using KLink.App.Services;

namespace KLink.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        // 临时：记录启动异常到 exe 目录
        DispatcherUnhandledException += (_, args) =>
        {
            try { File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "crash.log"), args.Exception.ToString()); } catch { }
        };
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
