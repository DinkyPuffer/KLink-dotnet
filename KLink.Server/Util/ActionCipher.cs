using System.Text;
using System.Text.Json.Nodes;

namespace KLink.Server.Util;

/// <summary>
/// 游戏动作加解密（ActionCipher）。
/// 包格式 %02d%06d%s%s%s = 2 位密钥表索引 + 6 位明文长度 + 4 字符 header + key + body。
/// XOR 流密码 + 双 Base64 变形，与官方客户端字节级兼容。
/// </summary>
public sealed class ActionCipher
{
    private static readonly int[] KeyLengths =
    {
        47, 53, 73, 55, 61, 103, 47, 103, 33, 45, 73, 37, 97, 71, 39, 71,
        31, 61, 83, 101, 53, 97, 79, 75, 37, 31, 33, 69, 43, 63, 39, 43,
        79, 55, 49, 73, 83, 67, 59, 69, 103, 39, 47, 37, 41, 71, 89, 55,
        49, 45, 33, 45, 69, 49, 43, 53, 59, 31, 59, 101, 61, 41, 79, 75,
        83, 89, 75, 67, 41, 89, 63, 101, 67, 63, 97
    };

    private readonly Random _random = new();

    public Decoded Decode(string packet)
    {
        int index = int.Parse(packet.Substring(0, 2));
        int length = int.Parse(packet.Substring(2, 6));
        int keyLength = KeyLengths[index];
        string header = packet.Substring(8, 4);
        string key = packet.Substring(12, keyLength);
        string body = packet[(12 + keyLength)..];
        byte[] encrypted = LooseBase64Decode(body);
        byte[] plain = new byte[Math.Min(length, encrypted.Length)];
        for (int i = 0; i < plain.Length; i++)
            plain[i] = (byte)(encrypted[i] ^ key[i % key.Length]);
        byte[] actionHeader = LooseBase64Decode(header + "==");
        int actionId = ((actionHeader[0] ^ key[0]) & 0xff) << 16;
        actionId |= ((actionHeader[1] ^ key[1 % key.Length]) & 0xff) << 8;
        actionId |= (actionHeader[2] ^ key[2 % key.Length]) & 0xff;
        string text = Encoding.UTF8.GetString(plain);
        return new Decoded(actionId, (JsonObject)(JsonNode.Parse(text) ?? new JsonObject()));
    }

    public string Encode(int actionId, JsonObject payload)
    {
        int keyIndex = _random.Next(KeyLengths.Length);
        string key = RandomKey(KeyLengths[keyIndex]);
        byte[] actionBytes =
        {
            (byte)((actionId >> 16) & 0xff),
            (byte)((actionId >> 8) & 0xff),
            (byte)(actionId & 0xff),
        };
        byte[] headerBytes = new byte[3];
        for (int i = 0; i < 3; i++)
            headerBytes[i] = (byte)(actionBytes[i] ^ key[i % key.Length]);
        string header = Convert.ToBase64String(headerBytes)[..4];
        byte[] plain = Encoding.UTF8.GetBytes(payload.ToJsonString());
        byte[] encrypted = new byte[plain.Length];
        for (int i = 0; i < plain.Length; i++)
            encrypted[i] = (byte)(plain[i] ^ key[i % key.Length]);
        string body = Convert.ToBase64String(encrypted).Replace("=", "");
        return $"{keyIndex:00}{plain.Length:000000}{header}{key}{body}";
    }

    private static string RandomKey(int length)
    {
        var sb = new StringBuilder(length);
        var random = new Random();
        while (sb.Length < length)
        {
            int c = 33 + random.Next(94);
            if (c != '"' && c != '\'' && c != '\\')
                sb.Append((char)c);
        }
        return sb.ToString();
    }

    /// <summary>宽松 Base64 解码：忽略非法字符（'=' 及非法字母），按 4 字符组展开。</summary>
    private static byte[] LooseBase64Decode(string value)
    {
        string input = value is null ? "" : value.TrimEnd('=');
        using var outStream = new MemoryStream();
        int offset = 0;
        while (offset < input.Length)
        {
            int packed = 0;
            int count = 0;
            for (int i = 0; i < 4; i++)
            {
                if (offset < input.Length)
                {
                    int decoded = DecodeBase64Char(input[offset]);
                    offset++;
                    if (decoded < 0)
                    {
                        i--;
                        continue;
                    }
                    packed = (packed << 6) | decoded;
                    count++;
                }
            }
            for (int i = count; i < 4; i++)
                packed <<= 6;
            if (count >= 2) outStream.WriteByte((byte)((packed >> 16) & 0xff));
            if (count >= 3) outStream.WriteByte((byte)((packed >> 8) & 0xff));
            if (count >= 4) outStream.WriteByte((byte)(packed & 0xff));
        }
        return outStream.ToArray();
    }

    private static int DecodeBase64Char(char c)
    {
        if (c >= 'A' && c <= 'Z') return c - 'A';
        if (c >= 'a' && c <= 'z') return c - 'a' + 26;
        if (c >= '0' && c <= '9') return c - '0' + 52;
        if (c == '+') return 62;
        if (c == '/') return 63;
        return -1;
    }

    public sealed record Decoded(int ActionId, JsonObject Payload);
}
