using System.Reflection;
using System.Text.Json.Nodes;

namespace KLink.Server;

/// <summary>
/// 游戏静态数据加载（AssetStore）。
/// 数据文件以 EmbeddedResource 嵌入程序集：Assets/kards-server/{library,deck_code_ids,items}.json。
/// </summary>
public sealed class AssetStore
{
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
