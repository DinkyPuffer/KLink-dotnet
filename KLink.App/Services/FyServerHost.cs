using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace KLink.App.Services;

/// <summary>
/// fyserver 子进程托管（KLink.Server 的替代入口）。
///
/// 设计依据（见 docs/fyserver/fyserver-启动机制与Web管理后台分析.md）：
/// - fyserver 的 13 类路径全部相对**当前工作目录**解析，因此必须以它的 exe 目录为 WorkingDirectory 启动；
/// - 它**没有任何就绪信号**（无 health、无 PID 文件），且 `_ = httpApp.RunAsync()` 丢弃绑定异常
///   → 端口被占时进程既不退出也不报错，所以必须自己探测端口 + 超时判失败 + 强杀；
/// - 它当前**没有优雅退出路径**（FasterKvService.Dispose() 挂在正常路径触发不到的回调上）
///   → 停止时只能强杀；fyserver 侧补上退出接口后，这里改为先发信号、超时再杀。
/// </summary>
public sealed class FyServerHost
{
    private readonly object _lock = new();
    private readonly HttpClient _probe = new() { Timeout = TimeSpan.FromSeconds(2) };

    private Process? _process;
    private CancellationTokenSource? _logCts;

    /// <summary>
    /// 优雅退出信号端口（只绑回环）。fyserver 侧由 `--shutdown-port` 接收；
    /// 收到连接后走 StopAsync → ApplicationStopping → fasterKv.Dispose()（检查点落盘 + 生成 YCDR）。
    /// </summary>
    private readonly int _shutdownPort = FindFreeLoopbackPort();

    /// <summary>取一个空闲的回环端口作为关闭信号通道。</summary>
    private static int FindFreeLoopbackPort()
    {
        try
        {
            var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
            listener.Start();
            int port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            return port;
        }
        catch
        {
            return 0;   // 取不到就退回"只能强杀"，并在停止时记录告警
        }
    }

    /// <summary>服务端输出的每一行（已带 [fyserver] 前缀），供 UI 日志区订阅。</summary>
    public event Action<string>? OutputLine;

    public bool IsRunning
    {
        get
        {
            lock (_lock)
            {
                return _process is { HasExited: false };
            }
        }
    }

    /// <summary>fyserver 进程退出时触发（含崩溃），参数为退出码。</summary>
    public event Action<int>? Exited;

    /// <summary>fyserver 安装目录：exe 同级 data\fyserver。</summary>
    public static string InstallDirectory => Path.Combine(AppContext.BaseDirectory, "data", "fyserver");

    /// <summary>fyserver 可执行文件名（AOT 发布产物）。</summary>
    private const string ExeName = "fyserver.exe";

    /// <summary>启动强依赖：缺任一文件 fyserver 会在监听端口之前直接崩溃。</summary>
    private static readonly string[] RequiredLibraryFiles =
    {
        "library/deckCodeIDsTable2.json",
        "library/emojiLib.json",
        "library/cardbackLib.json",
    };

    /// <summary>
    /// 启动 fyserver。返回错误信息，成功返回 null。
    /// </summary>
    /// <param name="listenIp">
    /// 实际绑定监听的网卡地址（写入 setting.json 的 listenIp）。
    /// local 模式传 127.0.0.1（只允许本机连），lan 模式传 0.0.0.0。
    /// </param>
    /// <param name="httpPort">HTTP + WebSocket 共用端口（fyserver 单端口模型，默认 5231）。</param>
    /// <param name="publicIp">
    /// 下发给客户端的可达地址（写入 setting.json 的 ip），用于拼 server_options.websocketurl。
    /// local 传 127.0.0.1，lan 传本机局域网 IP。
    /// </param>
    /// <param name="adminApiKey">后台管理密钥（对应设置页的"管理员令牌"）。</param>
    /// <param name="preferredPlayerName">新玩家的默认显示名（写入 setting.json:preferredPlayerName）。</param>
    /// <param name="adminDisplayName">后台界面展示的管理员名（写入 setting.json:adminDisplayName）。</param>
    public string? Start(string listenIp, int httpPort, string publicIp, string adminApiKey,
        string preferredPlayerName = "", string adminDisplayName = "")
    {
        lock (_lock)
        {
            if (IsRunning)
                return null;

            string dir = InstallDirectory;
            string exe = Path.Combine(dir, ExeName);

            if (!Directory.Exists(dir))
                return $"未找到 fyserver 目录：{dir}\n请将 fyserver 发布产物放在该目录下（缺少 data\\fyserver\\{ExeName}）。";
            if (!File.Exists(exe))
                return $"未找到 {ExeName}：{exe}";

            foreach (string rel in RequiredLibraryFiles)
            {
                if (!File.Exists(Path.Combine(dir, rel.Replace('/', Path.DirectorySeparatorChar))))
                    return $"fyserver 缺少启动必需文件：{rel}\n（缺失会在监听端口前直接崩溃，请检查发布产物是否完整）";
            }

            string? writeError = WriteSettingJson(dir, listenIp, httpPort, publicIp, adminApiKey,
                preferredPlayerName, adminDisplayName);
            if (writeError is not null)
                return writeError;

            if (IsPortInUse(httpPort))
                return $"端口 {httpPort} 已被占用，请先关闭占用该端口的程序（或上一个未退出的 fyserver）。";

            var psi = new ProcessStartInfo
            {
                FileName = exe,
                WorkingDirectory = dir,      // 关键：fyserver 全部相对路径都基于此
                UseShellExecute = false,
                CreateNoWindow = true,
                // 重定向 stdin 让 fyserver 命中 Console.IsInputRedirected → 跳过控制台命令循环
                // （否则它会在主线程上 Console.ReadKey 阻塞）
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                // 固定 UTF-8，两端约定死，不依赖系统区域设置。
                //
                // 历史教训（两次踩坑）：
                //   1) fyserver 原先按控制台代码页（中文系统 936/GBK）输出，这里按 UTF-8 解码
                //      → 中文全变 U+FFFD，不可恢复；
                //   2) 改成"跟随 Console.OutputEncoding"后，启动器是无控制台的 GUI 进程，
                //      该值取到的是 ANSI 代码页（936），又变成 GBK 按单字节解 UTF-8
                //      → 新的乱码（嬛儿綢纀?? 那种）。
                // 现在 fyserver 侧已在任何输出之前显式 Console.OutputEncoding = UTF8，
                // 所以这里就确定性地用 UTF-8 解码，两边协议一致。
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };
            // --no-console：跳过交互式命令循环；--shutdown-port：优雅退出信号端口
            psi.ArgumentList.Add("--no-console");
            psi.ArgumentList.Add("--shutdown-port");
            psi.ArgumentList.Add(_shutdownPort.ToString());

            try
            {
                _process = Process.Start(psi);
            }
            catch (Exception ex)
            {
                _process = null;
                return $"启动 fyserver 失败：{ex.Message}";
            }

            if (_process is null)
                return "启动 fyserver 失败：进程未创建";

            _logCts = new CancellationTokenSource();
            var stdout = _process.StandardOutput;
            var stderr = _process.StandardError;
            _ = PumpAsync(stdout, "fyserver", _logCts.Token);
            _ = PumpAsync(stderr, "fyserver!", _logCts.Token);

            var proc = _process;
            proc.EnableRaisingEvents = true;
            proc.Exited += (_, _) =>
            {
                int code = -1;
                try { code = proc.ExitCode; } catch { /* 忽略 */ }
                LogService.Instance.Warn($"fyserver 进程已退出（退出码 {code}）");
                Exited?.Invoke(code);
            };

            LogService.Instance.Info($"fyserver 已启动：{exe}（工作目录 {dir}，端口 {httpPort}）");

            // 测试钩子：置 KLINK_TEST_KILL_FYSERVER=1 时在 N 毫秒后强杀子进程，
            // 用于自动化验证"子进程崩溃 → 启动器如实反映状态"这条路径。
            if (Environment.GetEnvironmentVariable("KLINK_TEST_KILL_FYSERVER") is { Length: > 0 } delayText
                && int.TryParse(delayText, out int delayMs) && delayMs > 0)
            {
                LogService.Instance.Warn($"[测试] {delayMs}ms 后将强杀 fyserver 以验证崩溃检测");
                _ = Task.Run(async () =>
                {
                    await Task.Delay(delayMs).ConfigureAwait(false);
                    try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); } catch { /* 忽略 */ }
                });
            }

            return null;
        }
    }

    /// <summary>
    /// 等待 fyserver 就绪：轮询后台 session 接口（匿名可达，无需密钥）。
    /// 新版 fyserver 在用户存储未配置时对游戏端点返回 503，
    /// 因此这里顺带检测 <c>serverReady</c>，未就绪则自动用本地存储（FASTER）完成初始化 ——
    /// 否则使用者会看到整片 503 而不知道要先去网页配置数据库。
    /// </summary>
    public async Task<bool> WaitUntilReadyAsync(int httpPort, TimeSpan timeout)
    {
        string url = $"http://127.0.0.1:{httpPort}/admin/api/session";
        var deadline = DateTime.UtcNow + timeout;
        bool storageChecked = false;
        while (DateTime.UtcNow < deadline)
        {
            if (!IsRunning)
                return false;   // 进程已挂，不必等满超时
            try
            {
                using var resp = await _probe.GetAsync(url).ConfigureAwait(false);
                if (resp.IsSuccessStatusCode)
                {
                    string body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                    // 已配置存储（或旧版 fyserver 没有这个字段）→ 就绪
                    if (body.Contains("\"serverReady\":true", StringComparison.Ordinal) ||
                        !body.Contains("serverReady", StringComparison.Ordinal))
                        return true;

                    // 存储未就绪：自动用本地 FASTER 完成一次性初始化
                    if (!storageChecked)
                    {
                        storageChecked = true;
                        if (await TryConfigureLocalStorageAsync(httpPort).ConfigureAwait(false))
                            continue;   // 立刻重新探测
                    }
                }
                else if ((int)resp.StatusCode < 500)
                {
                    return true;
                }
            }
            catch
            {
                // 尚未监听，继续轮询
            }
            await Task.Delay(250).ConfigureAwait(false);
        }
        return false;
    }

    /// <summary>
    /// 首次运行自动把用户存储配成 local（本地 FASTER）。
    /// 该端点匿名可达且仅本机可用；成功后游戏端点解除 503。
    /// </summary>
    private async Task<bool> TryConfigureLocalStorageAsync(int httpPort)
    {
        try
        {
            string url = $"http://127.0.0.1:{httpPort}/admin/api/database/configure";
            using var content = new StringContent("{\"provider\":\"local\"}", Encoding.UTF8, "application/json");
            using var resp = await _probe.PostAsync(url, content).ConfigureAwait(false);
            if (resp.IsSuccessStatusCode)
            {
                LogService.Instance.Info("fyserver 首次运行：已自动配置本地存储（FASTER）");
                return true;
            }
            LogService.Instance.Warn($"fyserver 自动配置本地存储失败：HTTP {(int)resp.StatusCode}");
        }
        catch (Exception ex)
        {
            LogService.Instance.Warn($"fyserver 自动配置本地存储异常：{ex.Message}");
        }
        return false;
    }

    /// <summary>
    /// 停止 fyserver：先请求优雅退出（走检查点落盘），超时或失败再强杀。
    /// fyserver 侧需支持 <c>--shutdown-port</c>；不支持时会自动回退到强杀并记录告警。
    /// </summary>
    public void Stop()
    {
        Process? proc;
        CancellationTokenSource? cts;
        lock (_lock)
        {
            proc = _process;
            cts = _logCts;
            _process = null;
            _logCts = null;
        }

        if (proc is null)
        {
            cts?.Cancel();
            cts?.Dispose();
            return;
        }

        bool exited = false;
        // 1) 优雅退出：连一下关闭信号端口，让 fyserver 走 fasterKv.Dispose()
        if (_shutdownPort > 0)
        {
            try
            {
                using var client = new System.Net.Sockets.TcpClient();
                var connect = client.BeginConnect(System.Net.IPAddress.Loopback, _shutdownPort, null, null);
                if (connect.AsyncWaitHandle.WaitOne(1000))
                    client.EndConnect(connect);
                exited = proc.WaitForExit(8000);
                if (!exited)
                    LogService.Instance.Warn("fyserver 未在 8 秒内优雅退出，改为强制结束");
            }
            catch (Exception ex)
            {
                LogService.Instance.Warn($"发送关闭信号失败（{ex.Message}），改为强制结束");
            }
        }
        else
        {
            LogService.Instance.Warn("无可用关闭信号端口，直接强制结束 fyserver（最后一次检查点可能丢失）");
        }

        // 2) 兜底强杀
        try
        {
            if (!exited && !proc.HasExited)
            {
                proc.Kill(entireProcessTree: true);
                proc.WaitForExit(5000);
            }
        }
        catch (Exception ex)
        {
            LogService.Instance.Warn($"结束 fyserver 进程失败：{ex.Message}");
        }
        finally
        {
            cts?.Cancel();
            cts?.Dispose();
            try { proc.Dispose(); } catch { /* 忽略 */ }
        }
    }

    /// <summary>异步停止（供 UI 调用，避免阻塞界面线程）。</summary>
    public Task StopAsync() => Task.Run(Stop);

    // ==================== 内部 ====================

    /// <summary>
    /// 写 setting.json。字段名必须与 fyserver 的 ServerOptions 一致。
    /// 新版 fyserver 把「监听地址」与「对外通告地址」拆开了：
    ///   listenIp        —— 实际绑定的网卡（local 绑 127.0.0.1，lan 绑 0.0.0.0）
    ///   ip              —— 下发给客户端的可达地址（lan 模式填本机局域网 IP）
    ///   publicPortHttp  —— 对外端口（填写后 websocketurl 才会带端口）
    ///   publicScheme    —— http/https（https 时 WS 自动变 wss）
    /// </summary>
    private static string? WriteSettingJson(string dir, string listenIp, int httpPort, string publicIp,
        string adminApiKey, string preferredPlayerName, string adminDisplayName)
    {
        string path = Path.Combine(dir, "setting.json");
        try
        {
            var obj = new JsonObject
            {
                ["portHttp"] = httpPort,
                ["listenIp"] = string.IsNullOrWhiteSpace(listenIp) ? "127.0.0.1" : listenIp,
                ["publicPortHttp"] = httpPort,
                ["bancheat"] = false,
                ["ip"] = string.IsNullOrWhiteSpace(publicIp) ? "127.0.0.1" : publicIp,
                ["publicScheme"] = "http",
                // 后台管理密钥：远程访问后台需要它；同时保留本机 X-Admin-Key 通道供启动器读状态
                ["adminApiKey"] = adminApiKey ?? "",
                // 为空时 fyserver 回退它自己的默认值（传空表示"用服务端默认"）
                ["preferredPlayerName"] = preferredPlayerName ?? "",
                // 后台界面展示的管理员名（本机免登录访问后台时显示这个）
                ["adminDisplayName"] = adminDisplayName ?? "",
            };
            File.WriteAllText(path, obj.ToJsonString(SettingJsonOptions), new UTF8Encoding(false));
            return null;
        }
        catch (Exception ex)
        {
            return $"写入 fyserver 配置失败：{path}\n{ex.Message}";
        }
    }

    /// <summary>配置文件写出选项：缩进 + 中文不转义（便于用户直接手工编辑 setting.json）。</summary>
    private static readonly JsonSerializerOptions SettingJsonOptions = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>端口占用检测：fyserver 端口被占时不会退出也不会报错，必须提前拦。</summary>
    private static bool IsPortInUse(int port)
    {
        try
        {
            var listeners = System.Net.NetworkInformation.IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners();
            foreach (var ep in listeners)
            {
                if (ep.Port == port)
                    return true;
            }
        }
        catch
        {
            // 检测失败时不阻断启动，交给就绪探测兜底
        }
        return false;
    }


    /// <summary>逐行转发子进程输出到 UI 日志（子进程按 UTF-8 输出，见上面的编码约定）。</summary>
    private async Task PumpAsync(StreamReader reader, string tag, CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                string? line = await reader.ReadLineAsync(token).ConfigureAwait(false);
                if (line is null)
                    break;
                if (line.Length == 0)
                    continue;
                OutputLine?.Invoke(line);
                LogService.Instance.Write(tag, line);
            }
        }
        catch (OperationCanceledException)
        {
            // 正常停止
        }
        catch (Exception ex)
        {
            LogService.Instance.Warn($"读取 fyserver 输出失败：{ex.Message}");
        }
    }
}
