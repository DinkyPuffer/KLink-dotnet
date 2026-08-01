using System.Text.Json.Nodes;

namespace KLink.Server;

/// <summary>服务器内存日志环形缓冲（ServerLog）。</summary>
public static class ServerLog
{
    private const int MaxEntries = 160;
    private static readonly object Lock = new();
    private static readonly LinkedList<Entry> Entries = new();

    public static void Add(string level, string message)
    {
        lock (Lock)
        {
            if (Entries.Count >= MaxEntries)
                Entries.RemoveFirst();
            Entries.AddLast(new Entry
            {
                Time = Util.TimeUtil.NowIso(),
                Level = string.IsNullOrEmpty(level) ? "info" : level,
                Message = message ?? "",
            });
        }
    }

    public static JsonArray Recent()
    {
        lock (Lock)
        {
            var array = new JsonArray();
            foreach (var entry in Entries)
            {
                array.Add(new JsonObject
                {
                    ["time"] = entry.Time,
                    ["level"] = entry.Level,
                    ["message"] = entry.Message,
                });
            }
            return array;
        }
    }

    private sealed class Entry
    {
        public string Time = "";
        public string Level = "";
        public string Message = "";
    }
}
