using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Toolbox.Services;

/// <summary>
/// 配置服务接口
/// </summary>
public interface IConfigService
{
    /// <summary>设置文件路径：%AppData%\工具箱\settings.json</summary>
    string SettingsFilePath { get; }

    /// <summary>从指定路径加载 JSON 配置文件（文件不存在时返回新的默认实例）</summary>
    T LoadConfig<T>(string path) where T : class, new();

    /// <summary>保存配置到指定路径</summary>
    void SaveConfig<T>(string path, T config);

    /// <summary>计算文件的 SHA256 哈希值</summary>
    string ComputeHash(string filePath);

    /// <summary>验证文件哈希是否与存储的哈希一致</summary>
    bool ValidateHash(string filePath, string storedHash);
}

/// <summary>
/// 配置服务实现
/// 负责 JSON 配置文件的读写、SHA256 哈希校验
/// 存储位置：%AppData%\工具箱\settings.json
/// </summary>
public class ConfigService : IConfigService
{
    /// <summary>设置文件的完整路径</summary>
    public string SettingsFilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "工具箱",
        "settings.json");

    /// <summary>
    /// 加载 JSON 配置文件
    /// 文件不存在或读取失败时返回默认实例
    /// </summary>
    public T LoadConfig<T>(string path) where T : class, new()
    {
        try
        {
            if (!File.Exists(path)) return new T();

            var json = File.ReadAllText(path, Encoding.UTF8);
            return JsonSerializer.Deserialize<T>(json) ?? new T();
        }
        catch
        {
            // 文件损坏或格式错误，返回默认配置
            return new T();
        }
    }

    /// <summary>
    /// 保存配置到 JSON 文件
    /// 自动创建目录，使用缩进格式化输出
    /// </summary>
    public void SaveConfig<T>(string path, T config)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        var options = new JsonSerializerOptions
        {
            WriteIndented = true,                 // 缩进格式化
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping // 保留中文不转义
        };
        var json = JsonSerializer.Serialize(config, options);
        File.WriteAllText(path, json, Encoding.UTF8);
    }

    /// <summary>
    /// 计算文件的 SHA256 哈希值
    /// 用于配置文件完整性校验和备份文件验证
    /// </summary>
    /// <param name="filePath">文件路径</param>
    /// <returns>小写十六进制哈希字符串，文件不存在则返回空字符串</returns>
    public string ComputeHash(string filePath)
    {
        if (!File.Exists(filePath)) return "";

        var bytes = GetHashBytes(filePath);
        var hash = SHA256.HashData(bytes);
        return BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
    }

    private static byte[] GetHashBytes(string filePath)
    {
        var bytes = File.ReadAllBytes(filePath);
        if (!filePath.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            return bytes;

        try
        {
            var offset = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF
                ? 3
                : 0;
            var json = Encoding.UTF8.GetString(bytes, offset, bytes.Length - offset);
            var node = JsonNode.Parse(json);
            if (node is not JsonObject obj || !obj.ContainsKey(nameof(Models.AppSettings.ConfigHash)))
                return bytes;

            obj[nameof(Models.AppSettings.ConfigHash)] = "";
            var normalized = obj.ToJsonString(new JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            });
            return Encoding.UTF8.GetBytes(normalized);
        }
        catch
        {
            return bytes;
        }
    }

    /// <summary>
    /// 验证文件哈希值是否与存储的哈希一致
    /// 用于检测配置文件是否被篡改
    /// </summary>
    /// <param name="filePath">文件路径</param>
    /// <param name="storedHash">之前存储的哈希值</param>
    /// <returns>哈希一致返回 true，不一致返回 false</returns>
    public bool ValidateHash(string filePath, string storedHash)
    {
        if (string.IsNullOrEmpty(storedHash)) return true; // 未存储哈希时跳过校验

        var current = ComputeHash(filePath);
        return string.Equals(current, storedHash, StringComparison.OrdinalIgnoreCase);
    }
}
