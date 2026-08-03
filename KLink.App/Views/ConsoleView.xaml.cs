using System.IO;
using System.Windows;
using System.Windows.Controls;
using KLink.App.Services;
using Microsoft.Win32;
using WpfUiMessageBox = Wpf.Ui.Controls.MessageBox;

namespace KLink.App.Views;

/// <summary>
/// 控制台：左日志（实时滚动）+ 右终端（命令输入调试）。
/// </summary>
public partial class ConsoleView : UserControl
{
    private readonly KLinkService _service = KLinkService.Instance;

    public ConsoleView()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            LogService.Instance.HydrateSnapshot();
            UpdateLogCount();
            LogService.Instance.LogAdded += OnLogAdded;
            TermOutput.AppendText("KLink 控制台 — 输入 help 查看可用指令\n");
            TermInput.Focus();
        };
        Unloaded += (_, _) => LogService.Instance.LogAdded -= OnLogAdded;
    }

    // ==================== 日志 ====================

    private void OnLogAdded(string line)
    {
        Dispatcher.BeginInvoke(() =>
        {
            var snapshot = LogService.Instance.Snapshot;
            snapshot.Add(line);
            while (snapshot.Count > LogService.MaxLines)
                snapshot.RemoveAt(0);
            UpdateLogCount();
            LogList.ScrollIntoView(line);
        });
    }

    private void UpdateLogCount()
    {
        if (LogCountText is not null)
            LogCountText.Text = $"{LogService.Instance.Snapshot.Count} 行";
    }

    private void ClearLog_Click(object sender, RoutedEventArgs e)
    {
        LogService.Instance.Clear();
        UpdateLogCount();
        AppendTerm("日志已清空");
    }

    private async void ExportLog_Click(object sender, RoutedEventArgs e)
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
            AppendTerm($"日志已导出：{dialog.FileName}");
            var result = new WpfUiMessageBox
            {
                Title = "KLink-dotnet",
                Content = $"日志已导出到：\n{dialog.FileName}\n\n是否打开所在文件夹？",
                PrimaryButtonText = "打开文件夹",
                SecondaryButtonText = "关闭",
            };
            if (await result.ShowDialogAsync() == Wpf.Ui.Controls.MessageBoxResult.Primary)
                System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{dialog.FileName}\"");
        }
        catch (Exception ex)
        {
            LogService.Instance.Error($"导出日志失败：{ex.Message}");
            AppendTerm($"导出失败：{ex.Message}");
        }
    }

    // ==================== 终端 ====================

    private void AppendTerm(string line)
    {
        TermOutput.AppendText(line + "\n");
        TermOutput.ScrollToEnd();
    }

    private void TermInput_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != System.Windows.Input.Key.Enter)
            return;
        string cmd = TermInput.Text.Trim();
        TermInput.Text = "";
        if (cmd.Length == 0)
        {
            AppendTerm("> ");
            return;
        }
        AppendTerm($"> {cmd}");
        ExecuteCommand(cmd);
        TermInput.Focus();
    }

    private void ExecuteCommand(string cmd)
    {
        var parts = cmd.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        string name = parts[0].ToLowerInvariant();
        string arg = parts.Length > 1 ? string.Join(" ", parts[1..]) : "";
        switch (name)
        {
            case "help":
                AppendTerm("可用指令：");
                AppendTerm("  help              - 显示本帮助");
                AppendTerm("  clear             - 清空终端");
                AppendTerm("  status            - 服务器状态（模式/在线/对局）");
                AppendTerm("  version           - 启动器版本");
                AppendTerm("  mods              - 列出已安装模组");
                AppendTerm("  echo <text>       - 回显文本");
                AppendTerm("  export            - 导出日志文件");
                break;
            case "clear":
                TermOutput.Clear();
                break;
            case "status":
                AppendTerm(_service.GetStatus().ToJsonString());
                break;
            case "version":
                AppendTerm($"KLink-dotnet（.NET {Environment.Version}）");
                AppendTerm("KARDS 私服启动器：本地 / 局域网 / 远程转发");
                break;
            case "mods":
                var pakDir = SettingsService.Instance.PaksDirectory;
                if (pakDir is null)
                {
                    AppendTerm("未定位游戏目录，无法扫描模组");
                    break;
                }
                var mods = new ModManager(pakDir).ScanMods();
                if (mods.Count == 0)
                    AppendTerm("未安装模组");
                else
                {
                    AppendTerm($"共 {mods.Count} 个模组：");
                    foreach (var m in mods)
                        AppendTerm($"  {m.Name}  {m.Size}  {(m.Enabled ? "已启用" : "已禁用")}");
                }
                break;
            case "echo":
                AppendTerm(arg);
                break;
            case "export":
                ExportLog_Click(this, new RoutedEventArgs());
                break;
            default:
                AppendTerm($"未知指令：{name}（输入 help 查看可用指令）");
                break;
        }
    }
}
