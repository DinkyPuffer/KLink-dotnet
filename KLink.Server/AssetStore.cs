using System.Reflection;
using System.Text.Json.Nodes;

namespace KLink.Server;

/// <summary>
/// 游戏静态数据加载（AssetStore）。
/// 数据文件以 EmbeddedResource 嵌入程序集：Assets/kards-server/{library,deck_code_ids,items}.json；
/// 若 exe 同级存在 data/kards-server 覆盖文件，则优先读取覆盖文件（卡牌/卡组 ID 管理器保存的修改在此生效，重启服务器后加载）。
/// </summary>
public sealed class AssetStore
{
    /// <summary>运行时覆盖文件目录：exe 同级 data\kards-server。保存修改后的数据会写到这里。</summary>
    public static string OverrideDirectory => Path.Combine(AppContext.BaseDirectory, "data", "kards-server");

    private readonly object _lock = new();
    private JsonNode? _library;
    private JsonNode? _deckCodeIds;
    private JsonNode? _items;

    /// <summary>返回 library 深拷贝（调用方可安全修改）。</summary>
    public JsonNode Library()
    {
        lock (_lock)
        {
            _library ??= Load("library.json");
            return _library.DeepClone();
        }
    }

    public JsonNode DeckCodeIds()
    {
        lock (_lock)
        {
            _deckCodeIds ??= Load("deck_code_ids.json");
            return _deckCodeIds;
        }
    }

    /// <summary>返回 items 深拷贝（调用方可安全修改）。</summary>
    public JsonNode Items()
    {
        lock (_lock)
        {
            _items ??= Load("items.json");
            return _items.DeepClone();
        }
    }

    private static JsonNode Load(string fileName)
    {
        // 优先读取运行时覆盖文件（exe 同级 data\kards-server），卡牌/卡组 ID 管理器保存的修改在此生效
        var overridePath = Path.Combine(OverrideDirectory, fileName);
        if (File.Exists(overridePath))
            return JsonNode.Parse(File.ReadAllText(overridePath))
                ?? throw new InvalidOperationException($"覆盖文件 {overridePath} 解析失败");

        var assembly = Assembly.GetExecutingAssembly();
        // MSBuild 会把资源路径中的 '-' 转成 '_'，因此按文件名后缀匹配（如 ".library.json"）
        var resourceName = assembly.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith("." + fileName, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"找不到嵌入资源 {fileName}");
        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"无法打开嵌入资源 {resourceName}");
        using var reader = new StreamReader(stream);
        return JsonNode.Parse(reader.ReadToEnd())
            ?? throw new InvalidOperationException($"资源 {fileName} 解析失败");
    }
}
