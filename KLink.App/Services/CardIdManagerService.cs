using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using KLink.Server;

namespace KLink.App.Services;

/// <summary>
/// 卡牌/卡组 ID 数据管理（复刻需求/klink_card_id_manager.py 的核心逻辑）：
/// 编辑 library.json（卡牌库）与 deck_code_ids.json（卡组代码映射），
/// 保存到 exe 同级 data\kards-server 覆盖文件（AssetStore 优先读取，重启服务器后生效）。
/// </summary>
public static class CardIdManagerService
{
    /// <summary>运行时覆盖文件目录：exe 同级 data\kards-server。</summary>
    public static string OverrideDirectory => AssetStore.OverrideDirectory;

    public static string DefaultLibraryPath => Path.Combine(OverrideDirectory, "library.json");
    public static string DefaultDeckCodesPath => Path.Combine(OverrideDirectory, "deck_code_ids.json");

    /// <summary>紧凑序列化（无缩进、中文不转义），与脚本 json.dumps(ensure_ascii=False, separators=(',',':')) 对齐。</summary>
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    // ==================== 加载 ====================

    /// <summary>
    /// 加载卡牌库：优先读指定文件；文件不存在时回退内置嵌入资源（深拷贝）。
    /// </summary>
    public static JsonNode LoadLibrary(string path)
        => LoadFileOrFallback(path, new AssetStore().Library());

    /// <summary>
    /// 加载卡组代码映射：优先读指定文件；文件不存在时回退内置嵌入资源（深拷贝，
    /// 避免直接持有 AssetStore 缓存引用而被修改污染）。
    /// </summary>
    public static JsonNode LoadDeckCodes(string path)
        => LoadFileOrFallback(path, JsonNode.Parse(new AssetStore().DeckCodeIds().ToJsonString())!);

    private static JsonNode LoadFileOrFallback(string path, JsonNode fallback)
        => File.Exists(path)
            ? JsonNode.Parse(File.ReadAllText(path))
                ?? throw new InvalidOperationException($"文件解析失败：{path}")
            : fallback;

    /// <summary>加载结果描述（用于界面提示数据来源）。</summary>
    public static string DescribeSource(string path)
        => File.Exists(path) ? $"已从文件加载：{path}" : "文件不存在，已载入内置数据（保存后将创建覆盖文件）";

    // ==================== 保存 ====================

    /// <summary>
    /// 保存 JSON：先复制原文件为带时间戳的 .bak 备份，再写临时文件后原子替换。
    /// 返回备份文件路径（无原文件时返回 null）。
    /// </summary>
    public static string? Save(string path, JsonNode data)
    {
        var target = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);

        string? backupName = null;
        if (File.Exists(target))
        {
            backupName = $"{Path.GetFileName(target)}.{DateTime.Now:yyyyMMdd_HHmmss}.bak";
            File.Copy(target, Path.Combine(Path.GetDirectoryName(target)!, backupName), overwrite: true);
        }

        var tmp = target + ".tmp";
        File.WriteAllText(tmp, data.ToJsonString(JsonOptions));
        File.Move(tmp, target, overwrite: true);
        return backupName;
    }

    // ==================== 一致性校验 ====================

    /// <summary>校验：卡牌 ID 重复、卡组代码指向不存在的卡牌。返回错误列表（空 = 通过）。</summary>
    public static List<string> Validate(JsonNode library, JsonNode deckCodes)
    {
        var errors = new List<string>();

        var cards = library["cards"] as JsonArray;
        var ids = new List<int>();
        if (cards is not null)
        {
            foreach (var c in cards)
            {
                if (c?["id"] is not null && int.TryParse(c["id"]!.ToString(), out var id))
                    ids.Add(id);
            }
        }
        foreach (var dup in ids.GroupBy(x => x).Where(g => g.Count() > 1))
            errors.Add($"卡牌 ID 重复：{dup.Key}（{dup.Count()} 次）");

        var names = new HashSet<string>();
        if (cards is not null)
        {
            foreach (var c in cards)
            {
                if (c?["card_type"]?.ToString() is { Length: > 0 } name)
                    names.Add(name);
            }
        }

        if (deckCodes is JsonObject obj)
        {
            foreach (var (code, v) in obj)
            {
                var card = v?["card"]?.ToString();
                if (!string.IsNullOrEmpty(card) && !names.Contains(card))
                    errors.Add($"{code} 指向不存在卡牌：{card}");
            }
        }

        return errors;
    }
}
