using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;

namespace KLink.App.Services;

/// <summary>
/// 局域网房间发现（LanDiscovery）。
/// UDP 5233 广播房间信息（房主端）+ 监听扫描（客户端端）。
/// 协议：JSON { type:"kards_room", roomName, hostName, httpPort, wsPort, players, address }
/// </summary>
public sealed class LanDiscovery
{
    public const int DiscoveryPort = 5233;
    private const int BroadcastIntervalMs = 2000;
    private const string MessageType = "kards_room";

    private volatile bool _broadcasting;
    private volatile bool _scanning;
    private UdpClient? _socket;
    private CancellationTokenSource? _broadcastCts;
    private CancellationTokenSource? _scanCts;

    // 当前广播的房间信息
    private string _roomName = "KLink Room";
    private string _hostName = "Host";
    private int _httpPort = 5231;
    private int _wsPort = 5232;
    private int _players;

    public event Action<JsonObject>? RoomFound;

    public void SetRoomInfo(string? roomName, string? hostName, int httpPort, int wsPort, int players)
    {
        _roomName = string.IsNullOrEmpty(roomName) ? "KLink Room" : roomName;
        _hostName = string.IsNullOrEmpty(hostName) ? "Host" : hostName;
        _httpPort = httpPort > 0 ? httpPort : 5231;
        _wsPort = wsPort > 0 ? wsPort : 5232;
        _players = players;
    }

    // ==================== 广播（房主端） ====================

    public void StartBroadcast()
    {
        if (_broadcasting)
            return;
        EnsureSocket();
        _broadcasting = true;
        _broadcastCts = new CancellationTokenSource();
        _ = Task.Run(() => BroadcastLoop(_broadcastCts.Token));
        LogService.Instance.Info("开始广播房间信息（UDP 5233）");
    }

    public void StopBroadcast()
    {
        _broadcasting = false;
        _broadcastCts?.Cancel();
        _broadcastCts = null;
    }

    private async Task BroadcastLoop(CancellationToken token)
    {
        // 枚举所有网卡的子网定向广播地址(如 192.168.1.255、26.x.255),确保广播到达正确网段
        var targets = GetSubnetBroadcastAddresses();
        targets.Add(IPAddress.Broadcast); // 255.255.255.255 兜底
        while (_broadcasting && !token.IsCancellationRequested)
        {
            try
            {
                var msg = new JsonObject
                {
                    ["type"] = MessageType,
                    ["roomName"] = _roomName,
                    ["hostName"] = _hostName,
                    ["httpPort"] = _httpPort,
                    ["wsPort"] = _wsPort,
                    ["players"] = _players,
                    ["address"] = GetLocalIpAddress(),
                };
                byte[] data = Encoding.UTF8.GetBytes(msg.ToJsonString());
                foreach (var target in targets)
                {
                    try
                    {
                        await _socket!.SendAsync(data, data.Length, new IPEndPoint(target, DiscoveryPort));
                    }
                    catch
                    {
                        // 单个目标失败不影响其他网卡
                    }
                }
            }
            catch
            {
                // 广播失败继续
            }
            try
            {
                await Task.Delay(BroadcastIntervalMs, token);
            }
            catch (TaskCanceledException)
            {
                break;
            }
        }
    }

    /// <summary>枚举所有网卡的子网定向广播地址(IPv4, 由 IP &amp; 掩码 计算, 如 192.168.1.255)。</summary>
    private static List<IPAddress> GetSubnetBroadcastAddresses()
    {
        var result = new List<IPAddress>();
        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up)
                    continue;
                if (ni.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
                    continue;
                foreach (var addr in ni.GetIPProperties().UnicastAddresses)
                {
                    if (addr.Address.AddressFamily != AddressFamily.InterNetwork)
                        continue;
                    var ip = addr.Address;
                    var mask = addr.IPv4Mask;
                    byte[] ipB = ip.GetAddressBytes();
                    byte[] maskB = mask.GetAddressBytes();
                    for (int i = 0; i < 4; i++)
                        ipB[i] |= (byte)~maskB[i];
                    result.Add(new IPAddress(ipB));
                }
            }
        }
        catch
        {
            // 忽略
        }
        return result;
    }

    // ==================== 扫描（客户端） ====================

    public void StartScan()
    {
        if (_scanning)
            return;
        EnsureSocket();
        _scanning = true;
        _scanCts = new CancellationTokenSource();
        _ = Task.Run(() => ScanLoop(_scanCts.Token));
        LogService.Instance.Info("开始扫描局域网房间");
    }

    public void StopScan()
    {
        _scanning = false;
        _scanCts?.Cancel();
        _scanCts = null;
    }

    private async Task ScanLoop(CancellationToken token)
    {
        var buffer = new byte[2048];
        while (_scanning && !token.IsCancellationRequested)
        {
            try
            {
                var result = await _socket!.ReceiveAsync(token);
                string json = Encoding.UTF8.GetString(result.Buffer, 0, result.Buffer.Length);
                var msg = JsonNode.Parse(json) as JsonObject;
                if (msg is null || msg["type"]?.GetValue<string>() != MessageType)
                    continue;

                // 过滤掉自己的广播
                string senderIp = result.RemoteEndPoint.Address.ToString();
                if (senderIp == GetLocalIpAddress())
                    continue;

                // 使用实际来源地址
                msg["address"] = senderIp;
                if (msg["httpPort"] is null)
                    msg["httpPort"] = (msg["wsPort"]?.GetValue<int>() ?? 5231) - 1;

                RoomFound?.Invoke(msg);
            }
            catch
            {
                if (!_scanning)
                    break;
            }
        }
    }

    // ==================== 生命周期 ====================

    public void Destroy()
    {
        StopBroadcast();
        StopScan();
        try { _socket?.Dispose(); } catch { /* 忽略 */ }
        _socket = null;
    }

    private void EnsureSocket()
    {
        if (_socket is not null)
            return;
        try
        {
            _socket = new UdpClient();
            _socket.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            // 发送 255.255.255.255 广播必须启用 Broadcast 选项，否则发送抛异常被吞(房间永远广播不出去)
            _socket.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.Broadcast, true);
            _socket.Client.Bind(new IPEndPoint(IPAddress.Any, DiscoveryPort));
            _socket.Client.ReceiveTimeout = 3000;
        }
        catch (Exception ex)
        {
            LogService.Instance.Error($"创建 UDP socket 失败：{ex.Message}");
            throw new InvalidOperationException($"无法创建 UDP socket: {ex.Message}", ex);
        }
    }

    /// <summary>获取本机局域网 IPv4 地址（第一个非回环地址）。</summary>
    public static string GetLocalIpAddress()
    {
        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up)
                    continue;
                if (ni.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
                    continue;
                foreach (var addr in ni.GetIPProperties().UnicastAddresses)
                {
                    if (addr.Address.AddressFamily == AddressFamily.InterNetwork)
                        return addr.Address.ToString();
                }
            }
        }
        catch
        {
            // 忽略
        }
        return "0.0.0.0";
    }
}
