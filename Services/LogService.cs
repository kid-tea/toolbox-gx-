using System.IO;
using Serilog;
using Serilog.Events;

namespace Toolbox.Services;

/// <summary>
/// 日志服务接口
/// </summary>
public interface ILogService
{
    /// <summary>记录普通信息日志</summary>
    void LogInfo(string message);

    /// <summary>记录警告日志</summary>
    void LogWarning(string message);

    /// <summary>记录错误日志</summary>
    void LogError(string message, Exception? ex = null);

    /// <summary>
    /// 记录用户可见的操作日志（粉碎日志、注册表备份历史等）
    /// 存储为 JSON 格式，与调试日志分离
    /// </summary>
    /// <param name="category">操作分类，如"shredder"、"registry"</param>
    /// <param name="action">操作动作，如"shred"、"backup"</param>
    /// <param name="details">操作详情</param>
    void LogOperation(string category, string action, string details);
}

/// <summary>
/// 日志服务实现 — 基于 Serilog
/// 输出目标：
///   - 文件日志：%AppData%\工具箱\logs\toolbox-.log（按日滚动，保留 30 天）
///   - 操作日志：文档\工具箱备份\分类\ 目录（JSON 格式，与调试日志分离）
/// </summary>
public class LogService : ILogService
{
    private readonly ILogger _logger;
    private readonly string _operationLogDir;

    public LogService()
    {
        // 确保日志目录存在
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var logDir = Path.Combine(appData, "工具箱", "logs");
        Directory.CreateDirectory(logDir);

        // 配置 Serilog：按日滚动文件，保留最近 30 天
        _logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .WriteTo.File(
                Path.Combine(logDir, "toolbox-.log"),
                rollingInterval: RollingInterval.Day,          // 按日滚动
                retainedFileCountLimit: 30,                     // 保留 30 天
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level}] {Message}{Exception}")
            .CreateLogger();

        // 操作日志目录（用户可见日志，与调试日志分离）
        _operationLogDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "工具箱备份");
        Directory.CreateDirectory(_operationLogDir);
    }

    /// <summary>记录普通信息日志</summary>
    public void LogInfo(string message) => _logger.Information(message);

    /// <summary>记录警告日志</summary>
    public void LogWarning(string message) => _logger.Warning(message);

    /// <summary>记录错误日志</summary>
    public void LogError(string message, Exception? ex = null) => _logger.Error(ex, message);

    /// <summary>
    /// 记录操作日志到 JSON 文件
    /// 文件按日期和分类命名：2026-07-02-shredder.json
    /// </summary>
    public void LogOperation(string category, string action, string details)
    {
        try
        {
            var categoryDir = Path.Combine(_operationLogDir, category);
            Directory.CreateDirectory(categoryDir);

            var entry = new
            {
                Time = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                Category = category,
                Action = action,
                Details = details
            };

            var jsonPath = Path.Combine(categoryDir, $"{DateTime.Now:yyyy-MM-dd}-{category}.json");
            var json = System.Text.Json.JsonSerializer.Serialize(entry);

            // 追加写入 JSON 行
            File.AppendAllText(jsonPath, json + Environment.NewLine);
        }
        catch
        {
            // 操作日志写入失败不影响主流程
            _logger.Warning($"Failed to write operation log: {category}/{action}");
        }
    }
}
