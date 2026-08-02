using System.Globalization;
using System.IO;
using System.Windows;

namespace KLink.App.Services;

/// <summary>模组条目（UI 绑定模型）。</summary>
public sealed class ModEntry
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Size { get; set; } = "";
    public long SizeBytes { get; set; }
    public bool Enabled { get; set; }
    public int Priority { get; set; }

    public string Status => Enabled ? "已启用" : "已禁用";
    public Visibility EnableVisible => Enabled ? Visibility.Collapsed : Visibility.Visible;
    public Visibility DisableVisible => Enabled ? Visibility.Visible : Visibility.Collapsed;
}

/// <summary>
/// PAK 模组管理（ModManager + PC 端适配）。
/// PC 端 UE 只加载以 _P 结尾的 pak，因此安装时自动把 xxx.pak 重命名为 xxx_P.pak。
/// 启用/禁用 = 加/去 .disabled 后缀（引擎只加载 .pak）。
/// </summary>
public sealed class ModManager
{
    /// <summary>游戏本体包名（不含扩展名，大小写不敏感）—— 扫描时跳过，防止误操作。</summary>
    public const string BuiltinPakBaseName = "kards-Windows";

    private readonly string _pakPath;

    public ModManager(string pakPath) => _pakPath = pakPath;

    /// <summary>是否为游戏本体包（含 .disabled 变体）。</summary>
    public static bool IsBuiltinPak(string fileName)
        => ExtractBaseName(fileName).Equals(BuiltinPakBaseName, StringComparison.OrdinalIgnoreCase);

    /// <summary>扫描 PAK 目录，返回模组列表（按优先级降序，跳过游戏本体包）。</summary>
    public List<ModEntry> ScanMods()
    {
        var list = new List<ModEntry>();
        try
        {
            if (!Directory.Exists(_pakPath))
                return list;
            foreach (string file in Directory.EnumerateFiles(_pakPath))
            {
                string name = Path.GetFileName(file);
                if (name.EndsWith(".pak", StringComparison.OrdinalIgnoreCase)
                    || name.EndsWith(".pak.disabled", StringComparison.OrdinalIgnoreCase))
                {
                    if (IsBuiltinPak(name))
                        continue; // 游戏本体，跳过
                    list.Add(FromFile(file));
                }
            }
            list.Sort((a, b) => b.Priority.CompareTo(a.Priority));
            return list;
        }
        catch (Exception ex)
        {
            LogService.Instance.Error($"扫描模组失败：{ex.Message}");
            return list;
        }
    }

    /// <summary>
    /// 安装模组：复制到 PAK 目录。
    /// 文件名不以 _P 结尾时自动重命名为 xxx_P.pak（PC 端加载规则）。
    /// 返回目标文件名，失败返回 null。
    /// </summary>
    public string? InstallMod(string sourcePath)
    {
        if (string.IsNullOrEmpty(sourcePath) || !File.Exists(sourcePath))
            return null;
        try
        {
            string fileName = Path.GetFileName(sourcePath);
            string targetName = EnsurePakSuffix(fileName);
            Directory.CreateDirectory(_pakPath);
            string dest = Path.Combine(_pakPath, targetName);
            File.Copy(sourcePath, dest, overwrite: true);
            LogService.Instance.Info($"模组已安装：{fileName} → {targetName}" +
                (targetName != fileName ? "（已自动补 _P 后缀）" : ""));
            return targetName;
        }
        catch (Exception ex)
        {
            LogService.Instance.Error($"安装模组失败：{ex.Message}");
            return null;
        }
    }

    /// <summary>卸载模组：按基础名尝试 4 种后缀删除，再模糊匹配 _数字_P 格式。失败不抛异常。</summary>
    public bool UninstallMod(string modName)
    {
        if (string.IsNullOrEmpty(modName))
            return false;
        try
        {
            string baseName = modName.Replace(".pak", "", StringComparison.OrdinalIgnoreCase)
                .Replace(".pak.disabled", "", StringComparison.OrdinalIgnoreCase);
            string[] suffixes = { ".pak", ".pak.disabled", "_P.pak", "_P.pak.disabled" };
            foreach (string suffix in suffixes)
            {
                string file = Path.Combine(_pakPath, baseName + suffix);
                if (File.Exists(file))
                {
                    File.Delete(file);
                    LogService.Instance.Info($"模组已卸载：{baseName + suffix}");
                    return true;
                }
            }
            // 模糊匹配 _数字_P 格式
            if (Directory.Exists(_pakPath))
            {
                foreach (string file in Directory.EnumerateFiles(_pakPath))
                {
                    string name = Path.GetFileName(file);
                    if (name.StartsWith(baseName + "_", StringComparison.OrdinalIgnoreCase)
                        && name.Contains("_P.pak", StringComparison.OrdinalIgnoreCase))
                    {
                        File.Delete(file);
                        LogService.Instance.Info($"模组已卸载：{name}");
                        return true;
                    }
                }
            }
            return false;
        }
        catch (Exception ex)
        {
            // 文件被占用（游戏运行中）等场景：不崩溃，记日志
            LogService.Instance.Error($"卸载模组失败：{ex.Message}");
            return false;
        }
    }

    /// <summary>启用/禁用模组（重命名 .disabled 后缀）。失败不抛异常。</summary>
    public bool ToggleMod(string modName, bool enable)
    {
        if (string.IsNullOrEmpty(modName) || !Directory.Exists(_pakPath))
            return false;
        try
        {
            string baseName = modName.Replace(".pak", "", StringComparison.OrdinalIgnoreCase)
                .Replace(".pak.disabled", "", StringComparison.OrdinalIgnoreCase);
            foreach (string file in Directory.EnumerateFiles(_pakPath))
            {
                string name = Path.GetFileName(file);
                if (!name.StartsWith(baseName, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (enable && name.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase))
                {
                    string target = name[..^".disabled".Length];
                    File.Move(file, Path.Combine(_pakPath, target));
                    LogService.Instance.Info($"模组已启用：{target}");
                    return true;
                }
                if (!enable && !name.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase))
                {
                    File.Move(file, file + ".disabled");
                    LogService.Instance.Info($"模组已禁用：{name}");
                    return true;
                }
            }
            return false;
        }
        catch (Exception ex)
        {
            LogService.Instance.Error($"切换模组状态失败：{ex.Message}");
            return false;
        }
    }

    // ==================== 工具 ====================

    /// <summary>确保文件名以 _P.pak 结尾；已是 _P 结尾则原样返回，否则自动补 _P。</summary>
    public static string EnsurePakSuffix(string fileName)
    {
        string name = fileName.Trim();
        // 去掉可能的 .disabled（安装时不应有）
        if (name.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase))
            name = name[..^".disabled".Length];
        string lower = name.ToLowerInvariant();
        if (lower.EndsWith("_p.pak"))
            return name;
        if (lower.EndsWith(".pak"))
            return name[..^4] + "_P.pak";
        // 无 .pak 后缀：直接补 _P.pak
        return name + "_P.pak";
    }

    private static ModEntry FromFile(string file)
    {
        string fileName = Path.GetFileName(file);
        var entry = new ModEntry
        {
            Name = ExtractBaseName(fileName),
            SizeBytes = new FileInfo(file).Length,
            Enabled = !fileName.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase),
            Priority = ParsePriority(fileName),
        };
        entry.Id = entry.Name;
        entry.Size = FormatSize(entry.SizeBytes);
        return entry;
    }

    private static string ExtractBaseName(string fileName)
    {
        string name = fileName;
        if (name.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase))
            name = name[..^".disabled".Length];
        if (name.EndsWith(".pak", StringComparison.OrdinalIgnoreCase))
            name = name[..^".pak".Length];
        return name;
    }

    private static int ParsePriority(string fileName)
    {
        // 文件名格式: "数字_名称_P.pak" 或 "数字_名称.pak"
        int underIdx = fileName.IndexOf('_');
        if (underIdx > 0 && int.TryParse(fileName[..underIdx], out int priority))
            return priority;
        return 0;
    }

    private static string FormatSize(long bytes)
    {
        if (bytes < 1024) return bytes + "B";
        if (bytes < 1024 * 1024) return (bytes / 1024.0).ToString("0.0", CultureInfo.InvariantCulture) + "KB";
        if (bytes < 1024L * 1024 * 1024) return (bytes / (1024.0 * 1024)).ToString("0.0", CultureInfo.InvariantCulture) + "MB";
        return (bytes / (1024.0 * 1024 * 1024)).ToString("0.00", CultureInfo.InvariantCulture) + "GB";
    }
}
