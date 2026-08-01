using System.Diagnostics;
using System.IO;

namespace KLink.App.Services;

/// <summary>
/// 游戏本体进程管理：启动 / 停止 / 状态检测。
/// 游戏路径由 SettingsService 相对探测得出（kds\kards\Binaries\Win64\kards-Win64-Shipping.exe）。
/// </summary>
public sealed class GameProcessService
{
    private readonly SettingsService _settings;
    private Process? _process;

    public event Action? Exited;

    public GameProcessService(SettingsService settings) => _settings = settings;

    public bool IsRunning => _process is { HasExited: false };

    public string? GameExePath => _settings.GameExePath;

    /// <summary>启动游戏。返回错误信息，成功返回 null。</summary>
    public string? Start()
    {
        try
        {
            string? exe = _settings.GameExePath;
            if (exe is null)
                return "未定位游戏本体，请在设置页指定 kds 目录";
            if (!File.Exists(exe))
                return $"游戏本体不存在：{exe}";
            if (IsRunning)
                return null; // 已在运行

            string workDir = Path.GetDirectoryName(exe)!;
            var psi = new ProcessStartInfo
            {
                FileName = exe,
                WorkingDirectory = workDir,
                UseShellExecute = true,
            };
            _process = Process.Start(psi);
            if (_process is null)
                return "进程启动失败";
            _process.EnableRaisingEvents = true;
            _process.Exited += (_, _) =>
            {
                LogService.Instance.Info("游戏进程已退出");
                Exited?.Invoke();
            };
            LogService.Instance.Info($"游戏已启动：{exe}");
            return null;
        }
        catch (Exception ex)
        {
            return $"启动游戏失败：{ex.Message}";
        }
    }

    /// <summary>关闭游戏进程（正常关闭，超时后强制结束）。</summary>
    public void Stop()
    {
        if (!IsRunning)
            return;
        try
        {
            _process!.CloseMainWindow();
            if (!_process.WaitForExit(5000))
            {
                _process.Kill(entireProcessTree: true);
                LogService.Instance.Warn("游戏未响应，已强制结束");
            }
            else
            {
                LogService.Instance.Info("游戏已关闭");
            }
        }
        catch (Exception ex)
        {
            LogService.Instance.Error($"关闭游戏失败：{ex.Message}");
        }
        _process = null;
    }
}
