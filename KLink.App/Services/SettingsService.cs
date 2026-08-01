using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace KLink.App.Services;

/// <summary>持久化配置模型，存储于启动器 exe 同级的 config.json。</summary>
public sealed class AppSettings
{
    public string RoomName { get; set; } = "KLink Room";
    public string HostName { get; set; } = "Host";
    public string RemoteAddress { get; set; } = "";
    public int RemotePort { get; set; } = 5231;
    public string LastMode { get; set; } = "local";
    public string PreferredPlayerName { get; set; } = "";
    public string AdminToken { get; set; } = "";

    /// <summary>kds 游戏根目录（用户手动指定时非空）；为空则自动从启动器位置相对探测。</summary>
    public string? GameRoot { get; set; }

    /// <summary>服务器 JWT 密钥（首次启动随机生成后持久化，避免硬编码）。</summary>
    public string? JwtSecret { get; set; }

    /// <summary>界面主题：dark（Deep Dark 深色）/ light（Moon Light 浅色）。</summary>
    public string Theme { get; set; } = "dark";

    /// <summary>自定义背景：none / image / video。</summary>
    public string BackgroundType { get; set; } = "none";

    /// <summary>背景图片/视频文件路径。</summary>
    public string? BackgroundPath { get; set; }

    [JsonIgnore]
    public bool HasCustomGameRoot => !string.IsNullOrWhiteSpace(GameRoot);
}

/// <summary>
/// 配置持久化 + 游戏目录定位。
/// 路径规则：不硬编码绝对路径 —— 默认从启动器 exe 所在目录逐级向上探测 kds 目录，
/// 也可在设置页手动指定。所有游戏相关路径均由 <see cref="GameRoot"/> 相对推导。
/// </summary>
public sealed class SettingsService
{
    public const string GameExeName = "kards-Win64-Shipping.exe";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static SettingsService Instance { get; } = new();

    public AppSettings Settings { get; private set; } = new();

    /// <summary>配置文件路径：启动器 exe 同级 config.json。</summary>
    public string ConfigPath => Path.Combine(AppContext.BaseDirectory, "config.json");

    private SettingsService() { }

    public void Load()
    {
        try
        {
            if (File.Exists(ConfigPath))
            {
                var json = File.ReadAllText(ConfigPath);
                Settings = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions) ?? new AppSettings();
            }
        }
        catch (Exception ex)
        {
            LogService.Instance.Warn($"读取 config.json 失败，使用默认配置：{ex.Message}");
            Settings = new AppSettings();
        }
    }

    public void Save()
    {
        try
        {
            var json = JsonSerializer.Serialize(Settings, JsonOptions);
            File.WriteAllText(ConfigPath, json);
        }
        catch (Exception ex)
        {
            LogService.Instance.Error($"保存 config.json 失败：{ex.Message}");
        }
    }

    // ==================== 游戏目录定位 ====================

    /// <summary>kds 根目录：优先手动指定，否则相对探测。</summary>
    public string? ResolveGameRoot()
    {
        if (Settings.HasCustomGameRoot)
            return Settings.GameRoot;

        var root = DetectGameRoot(AppContext.BaseDirectory);
        if (root is not null)
            LogService.Instance.Info($"自动定位 kds 目录：{root}");
        else
            LogService.Instance.Warn("未找到 kds 游戏目录，请在设置页手动指定");
        return root;
    }

    /// <summary>
    /// 从起始目录逐级向上查找包含 kds\kards\Binaries\Win64\kards-Win64-Shipping.exe 的 kds 目录。
    /// 返回最近的匹配层，找不到返回 null。
    /// </summary>
    public static string? DetectGameRoot(string startDir)
    {
        var dir = new DirectoryInfo(startDir);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "kds");
            if (Directory.Exists(candidate))
            {
                var exe = Path.Combine(candidate, "kards", "Binaries", "Win64", GameExeName);
                if (File.Exists(exe))
                    return candidate;
            }
            dir = dir.Parent;
        }
        return null;
    }

    /// <summary>游戏可执行文件完整路径（kds\kards\Binaries\Win64\kards-Win64-Shipping.exe）。</summary>
    public string? GameExePath
    {
        get
        {
            var root = ResolveGameRoot();
            return root is null ? null : Path.Combine(root, "kards", "Binaries", "Win64", GameExeName);
        }
    }

    /// <summary>游戏启动工作目录（Binaries\Win64）。</summary>
    public string? GameExeDir
    {
        get
        {
            var exe = GameExePath;
            return exe is null ? null : Path.GetDirectoryName(exe);
        }
    }

    /// <summary>模组目录（kds\kards\Content\Paks）。</summary>
    public string? PaksDirectory
    {
        get
        {
            var root = ResolveGameRoot();
            return root is null ? null : Path.Combine(root, "kards", "Content", "Paks");
        }
    }
}
