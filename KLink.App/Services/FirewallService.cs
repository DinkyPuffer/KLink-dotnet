using System.Diagnostics;
using System.IO;
using System.Security.Principal;

namespace KLink.App.Services;

/// <summary>
/// Windows 防火墙入站放通。
///
/// 为什么需要：fyserver 用 Kestrel 自建 socket 监听（不走 HTTP.sys），
/// 所以 Windows 那个"是否允许此应用通过防火墙"的弹窗**根本不会出现** ——
/// 局域网模式下别的设备能收到房间广播（UDP），却连不上 5231，包被静默丢弃。
/// 本机自测一切正常（回环与出站都不受入站规则限制），问题只在别的设备上暴露。
///
/// 做法：局域网模式下检查当前 exe 的入站放行规则是否存在，不存在就提权添加一条
/// （只针对当前 exe 的绝对路径，不动端口，避免影响其它程序）。
/// </summary>
public static class FirewallService
{
    /// <summary>规则显示名。带路径便于区分多个安装位置。</summary>
    private static string RuleName(string exePath) => $"KLink 服务器 ({exePath})";

    /// <summary>
    /// 确保当前 exe 已放通入站。返回提示信息（成功或失败原因），无需放通时返回 null。
    /// </summary>
    public static string? EnsureInboundAllowed()
    {
        string? exePath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(exePath))
            return "无法确定当前程序路径，跳过防火墙放通";

        // 已放通则什么都不做（每次启动都会调，避免重复弹 UAC）
        if (RuleExists(exePath))
            return null;

        if (!IsElevated())
        {
            // 提权添加。用 netsh 而不是 PowerShell cmdlet：启动更快，且不依赖执行策略。
            bool ok = TryAddRuleElevated(exePath, out string error);
            if (!ok)
                return $"未能自动放通防火墙（{error}）。\n" +
                       "局域网内其它设备将无法连接。可手动以管理员身份执行：\n" +
                       $"netsh advfirewall firewall add rule name=\"{RuleName(exePath)}\" dir=in action=allow program=\"{exePath}\" enable=yes";
        }

        // 提权进程结束后再确认一次
        return RuleExists(exePath)
            ? null
            : "防火墙规则已提交但未生效，局域网内其它设备可能无法连接";
    }

    /// <summary>规则是否已存在（按显示名匹配当前 exe 路径）。</summary>
    private static bool RuleExists(string exePath)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "netsh",
                Arguments = "advfirewall firewall show rule name=all dir=in",
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            if (process is null) return false;
            string output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(5000);
            return output.Contains(exePath, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            // 查询失败时按"不存在"处理：最坏结果是多弹一次 UAC，不影响启动
            return false;
        }
    }

    private static bool IsElevated()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch { return false; }
    }

    /// <summary>提权执行 netsh 添加规则。用户拒绝 UAC 时返回 false。</summary>
    private static bool TryAddRuleElevated(string exePath, out string error)
    {
        error = "";
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "netsh",
                // program= 用绝对路径，只放通这一个可执行文件
                Arguments = $"advfirewall firewall add rule name=\"{RuleName(exePath)}\" " +
                            $"dir=in action=allow program=\"{exePath}\" enable=yes profile=any",
                UseShellExecute = true,
                Verb = "runas",          // 触发 UAC
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
            };
            using var process = Process.Start(psi);
            if (process is null) { error = "无法启动提权进程"; return false; }
            if (!process.WaitForExit(30000)) { error = "提权进程超时"; return false; }
            if (process.ExitCode != 0) { error = $"netsh 退出码 {process.ExitCode}"; return false; }
            return true;
        }
        catch (Exception ex)
        {
            // 用户点了"否"时是 Win32Exception（操作已被用户取消）
            error = ex.Message;
            return false;
        }
    }

    /// <summary>供日志展示：当前 exe 是否已放通。</summary>
    public static bool IsCurrentExecutableAllowed()
    {
        string? exePath = Environment.ProcessPath;
        return !string.IsNullOrWhiteSpace(exePath) && RuleExists(exePath);
    }
}
