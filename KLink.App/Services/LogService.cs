using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;

namespace KLink.App.Services;

/// <summary>
/// 内存日志环形缓冲。后台线程安全写入，UI 订阅 <see cref="LogAdded"/> 后自行调度到界面线程。
/// </summary>
public sealed class LogService : INotifyPropertyChanged
{
    public const int MaxLines = 500;

    private readonly object _lock = new();
    private readonly List<string> _buffer = new();
    private readonly ObservableCollection<string> _snapshot = new();

    public event Action<string>? LogAdded;

    public ObservableCollection<string> Snapshot => _snapshot;

    public static LogService Instance { get; } = new();

    private LogService() { }

    public void Info(string message) => Write("INFO", message);
    public void Warn(string message) => Write("WARN", message);
    public void Error(string message) => Write("ERROR", message);

    public void Write(string level, string message)
    {
        var line = $"[{DateTime.Now:HH:mm:ss}] [{level}] {message}";
        lock (_lock)
        {
            _buffer.Add(line);
            if (_buffer.Count > MaxLines)
                _buffer.RemoveAt(0);
        }
        LogAdded?.Invoke(line);
    }

    /// <summary>将缓冲内的历史日志灌入 UI 集合（首次订阅时调用一次）。</summary>
    public void HydrateSnapshot()
    {
        lock (_lock)
        {
            _snapshot.Clear();
            foreach (var line in _buffer)
                _snapshot.Add(line);
        }
    }

    /// <summary>清空全部日志（缓冲 + UI 集合）。</summary>
    public void Clear()
    {
        lock (_lock)
        {
            _buffer.Clear();
            _snapshot.Clear();
        }
    }

    /// <summary>导出全部日志到文件（含时间/版本头）。</summary>
    public void Export(string filePath)
    {
        lock (_lock)
        {
            var lines = new List<string>
            {
                $"KLink 日志导出 {DateTime.Now:yyyy-MM-dd HH:mm:ss}",
                $"运行环境：.NET {Environment.Version}",
                $"工作目录：{AppContext.BaseDirectory}",
                new string('=', 60),
            };
            lines.AddRange(_buffer);
            File.WriteAllLines(filePath, lines);
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
