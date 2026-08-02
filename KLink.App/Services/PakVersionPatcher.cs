using System.IO;
using System.Text;

namespace KLink.App.Services;

/// <summary>
/// 版本补丁 pak 生成（功能一，可行性已验证）：
/// 模板 pak 内含明文的 ProjectVersion= 版本号（如 "KLink 29452.29452"），
/// 进入游戏前复制模板并等长替换版本号（不足用空格右填充，保证字节数不变）后写入游戏 Paks 目录。
/// 本地模式直接复制不改版本；远程/局域网模式写入配置的版本号。
/// PC 端 UE 引擎只加载 _P 结尾的 pak，输出名自动补 _P。
/// </summary>
public static class PakVersionPatcher
{
    private const string Needle = "ProjectVersion=";

    /// <summary>内置模板自动提取到的位置：exe 同级 data\version.pak（可被用户直接替换更新）。</summary>
    public static string DefaultTemplatePath => Path.Combine(AppContext.BaseDirectory, "data", "version.pak");

    /// <summary>
    /// 解析模板 pak 路径：设置页手动指定优先；否则用内置模板（首次自动从 DLL 嵌入资源提取到 data\version.pak）。
    /// 返回 null 表示无可用模板（跳过版本补丁）。
    /// </summary>
    public static string? ResolveTemplatePath()
    {
        var configured = SettingsService.Instance.Settings.VersionPakTemplate;
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured))
            return configured;

        EnsureBuiltinTemplate();
        return File.Exists(DefaultTemplatePath) ? DefaultTemplatePath : null;
    }

    /// <summary>把嵌入资源里的内置模板提取到 data\version.pak（已存在则跳过，允许用户替换更新模板）。</summary>
    public static void EnsureBuiltinTemplate()
    {
        var path = DefaultTemplatePath;
        if (File.Exists(path))
            return;
        try
        {
            var assembly = typeof(KLink.Server.AssetStore).Assembly;
            var resourceName = assembly.GetManifestResourceNames()
                .FirstOrDefault(n => n.EndsWith(".version.pak", StringComparison.OrdinalIgnoreCase));
            if (resourceName is null)
                return;
            using var stream = assembly.GetManifestResourceStream(resourceName);
            if (stream is null)
                return;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using var file = File.Create(path);
            stream.CopyTo(file);
            LogService.Instance.Info($"已提取内置版本补丁模板：{path}");
        }
        catch (Exception ex)
        {
            LogService.Instance.Warn($"提取内置版本补丁模板失败：{ex.Message}");
        }
    }

    /// <summary>模板信息：当前版本号与容量（版本号值最多可占字符数）。解析失败返回 null。</summary>
    public static (string Current, int Capacity)? Probe(string templatePath)
    {
        try
        {
            var raw = File.ReadAllBytes(templatePath);
            int i = IndexOf(raw, Encoding.ASCII.GetBytes(Needle));
            if (i < 0)
                return null;
            int start = i + Needle.Length;
            int end = start;
            while (end < raw.Length && raw[end] != (byte)'\r' && raw[end] != (byte)'\n')
                end++;
            string current = Encoding.ASCII.GetString(raw, start, end - start).TrimEnd();
            return (current, end - start);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>生成版本补丁 pak 到指定路径。成功返回 null，失败返回错误信息。</summary>
    public static string? Patch(string templatePath, string version, string outputPath)
    {
        try
        {
            if (!File.Exists(templatePath))
                return $"模板 pak 不存在：{templatePath}";

            byte[] raw = File.ReadAllBytes(templatePath);
            int i = IndexOf(raw, Encoding.ASCII.GetBytes(Needle));
            if (i < 0)
                return "模板 pak 中未找到 ProjectVersion= 版本号字符串";
            int start = i + Needle.Length;
            int end = start;
            while (end < raw.Length && raw[end] != (byte)'\r' && raw[end] != (byte)'\n')
                end++;
            int capacity = end - start;
            if (version.Length > capacity)
                return $"版本号过长：{version.Length} 字符，模板容量 {capacity} 字符";
            foreach (char c in version)
            {
                if (c > 127)
                    return "版本号只能包含 ASCII 字符";
            }

            // 等长替换：不足部分空格右填充，文件字节数不变（其余字节原样保留）
            byte[] newValue = Encoding.ASCII.GetBytes(version.PadRight(capacity, ' '));
            Array.Copy(newValue, 0, raw, start, capacity);

            string? dir = Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
            File.WriteAllBytes(outputPath, raw);
            LogService.Instance.Info($"版本补丁已生成：{outputPath}（版本 {version}）");
            return null;
        }
        catch (Exception ex)
        {
            return $"生成版本补丁失败：{ex.Message}";
        }
    }

    /// <summary>输出文件名：模板 version.pak → version_P.pak（PC 端引擎只加载 _P 结尾的 pak）。</summary>
    public static string OutputName(string templatePath)
    {
        string name = Path.GetFileNameWithoutExtension(templatePath);
        if (!name.EndsWith("_P", StringComparison.OrdinalIgnoreCase))
            name += "_P";
        return name + ".pak";
    }

    private static int IndexOf(byte[] haystack, byte[] needle)
    {
        for (int i = 0; i <= haystack.Length - needle.Length; i++)
        {
            bool match = true;
            for (int j = 0; j < needle.Length; j++)
            {
                if (haystack[i + j] != needle[j])
                {
                    match = false;
                    break;
                }
            }
            if (match)
                return i;
        }
        return -1;
    }
}
