namespace KLink.Server;

/// <summary>
/// 服务器诊断日志钩子：KLink.App 订阅后转发到 UI 日志区。
/// 未订阅时消息丢弃（不影响服务器功能）。
/// </summary>
public static class ServerEvents
{
    public static Action<string>? OnLog;

    public static void Log(string message) => OnLog?.Invoke(message);
}
