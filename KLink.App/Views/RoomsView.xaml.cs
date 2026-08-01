using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using KLink.App.Services;

namespace KLink.App.Views;

/// <summary>房间列表条目。</summary>
public sealed class RoomItem
{
    public string RoomName { get; set; } = "";
    public string HostName { get; set; } = "";
    public string Address { get; set; } = "";
    public int Port { get; set; }
    public int Players { get; set; }
}

public partial class RoomsView : UserControl
{
    private readonly KLinkService _service = KLinkService.Instance;
    private readonly DispatcherTimer _scanTimer;

    public RoomsView()
    {
        InitializeComponent();
        _service.RoomFound += OnRoomFound;

        // 10 秒自动停止扫描
        _scanTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
        _scanTimer.Tick += (_, _) => StopScan();
    }

    private void StartScan_Click(object sender, RoutedEventArgs e) => StartScan();

    private void StopScan_Click(object sender, RoutedEventArgs e) => StopScan();

    private void StartScan()
    {
        RoomList.ItemsSource = null;
        _service.StartScanRooms();
        TxtScanState.Text = "正在扫描…（10 秒后自动停止）";
        BtnScanRooms.IsEnabled = false;
        BtnStopScan.IsEnabled = true;
        _scanTimer.Start();
    }

    private void StopScan()
    {
        _service.StopScanRooms();
        _scanTimer.Stop();
        TxtScanState.Text = "扫描已停止";
        BtnScanRooms.IsEnabled = true;
        BtnStopScan.IsEnabled = false;
    }

    private void OnRoomFound(JsonObject room)
    {
        Dispatcher.BeginInvoke(() =>
        {
            var item = new RoomItem
            {
                RoomName = room["roomName"]?.GetValue<string>() ?? "",
                HostName = room["hostName"]?.GetValue<string>() ?? "",
                Address = room["address"]?.GetValue<string>() ?? "",
                Port = room["httpPort"]?.GetValue<int>() ?? 5231,
                Players = room["players"]?.GetValue<int>() ?? 0,
            };
            // 按 address:port 去重
            if (RoomList.ItemsSource is not List<RoomItem> list)
            {
                list = new List<RoomItem>();
                RoomList.ItemsSource = list;
            }
            if (!list.Any(r => r.Address == item.Address && r.Port == item.Port))
                list.Add(item);
        });
    }

    private void JoinRoom_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Wpf.Ui.Controls.Button { Tag: RoomItem room })
        {
            // 切换到服务器页并填入远程地址（由主窗口处理）
            var window = Window.GetWindow(this) as MainWindow;
            window?.ConnectRemote(room.Address, room.Port);
        }
    }
}
