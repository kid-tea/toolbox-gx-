using System.Text.Json.Serialization;

namespace Toolbox.Models;

/// <summary>
/// 应用程序设置模型
/// 存储所有配置字段，包括主界面/悬浮面板外观、快捷键、通知等设置
/// 持久化到 %AppData%\工具箱\settings.json
/// </summary>
public class AppSettings
{
    /// <summary>配置版本号，用于升级兼容性检测</summary>
    public int Version { get; set; } = 1;

    /// <summary>界面语言：zh-CN / en-US</summary>
    public string Language { get; set; } = "zh-CN";

    /// <summary>主题：Light / Dark / Dashboard</summary>
    public string Theme { get; set; } = "Light";

    // ==================== 主界面外观设置 ====================

    /// <summary>主界面背景模式：Solid / CustomImage</summary>
    public string MainBackgroundMode { get; set; } = "Solid";

    /// <summary>主界面背景图片路径</summary>
    public string MainBackgroundImagePath { get; set; } = "";

    /// <summary>主界面背景图片显示模式：Stretch / Tile / Center</summary>
    public string MainBackgroundImageMode { get; set; } = "Stretch";

    /// <summary>主界面字体类型</summary>
    public string MainFontFamily { get; set; } = "Microsoft YaHei";

    /// <summary>主界面字体大小</summary>
    public int MainFontSize { get; set; } = 14;

    /// <summary>主界面字体颜色（HEX 格式）</summary>
    public string MainFontColor { get; set; } = "#000000";

    /// <summary>主界面窗口宽度</summary>
    public int MainWindowWidth { get; set; } = 1200;

    /// <summary>主界面窗口高度</summary>
    public int MainWindowHeight { get; set; } = 800;

    // ==================== 悬浮面板外观设置 ====================

    /// <summary>悬浮面板背景模式：Solid / CustomImage</summary>
    public string PanelBackgroundMode { get; set; } = "Solid";

    /// <summary>悬浮面板背景图片路径</summary>
    public string PanelBackgroundImagePath { get; set; } = "";

    /// <summary>悬浮面板背景图片显示模式：Stretch / Tile / Center</summary>
    public string PanelBackgroundImageMode { get; set; } = "Stretch";

    /// <summary>悬浮面板透明度（0.1 ~ 1.0）</summary>
    public double PanelOpacity { get; set; } = 0.9;

    /// <summary>悬浮面板字体类型</summary>
    public string PanelFontFamily { get; set; } = "Microsoft YaHei";

    /// <summary>悬浮面板字体大小</summary>
    public int PanelFontSize { get; set; } = 12;

    /// <summary>悬浮面板字体颜色（HEX 格式）</summary>
    public string PanelFontColor { get; set; } = "#000000";

    /// <summary>悬浮面板窗口宽度</summary>
    public int PanelWidth { get; set; } = 360;

    /// <summary>悬浮面板窗口高度</summary>
    public int PanelHeight { get; set; } = 480;

    /// <summary>悬浮面板是否始终置顶</summary>
    public bool PanelAlwaysOnTop { get; set; } = true;

    // ==================== 快捷键设置 ====================

    /// <summary>截屏快捷键（默认 Ctrl+Shift+X）</summary>
    public string ScreenshotShortcut { get; set; } = "Ctrl+Shift+X";

    /// <summary>取色器快捷键（默认 Ctrl+Shift+C）</summary>
    public string ColorPickerShortcut { get; set; } = "Ctrl+Shift+C";

    /// <summary>窗口置顶快捷键（默认 Ctrl+Shift+T）</summary>
    public string AlwaysOnTopShortcut { get; set; } = "Ctrl+Shift+T";

    // ==================== 通知设置 ====================

    /// <summary>是否启用所有通知</summary>
    public bool NotificationsEnabled { get; set; } = true;

    /// <summary>默认提示音：SystemDefault / Silent / CustomWav</summary>
    public string DefaultAlertSound { get; set; } = "SystemDefault";

    /// <summary>自定义提示音 WAV 文件路径</summary>
    public string DefaultAlertSoundPath { get; set; } = "";

    /// <summary>C盘定时清理通知模式：BeforeCleanup / AfterOnly / Silent</summary>
    public string ScheduledCleanupNotifyMode { get; set; } = "BeforeCleanup";

    /// <summary>定时关机通知模式：5MinAnd1Min / 1MinOnly / NoNotification</summary>
    public string ScheduledShutdownNotifyMode { get; set; } = "5MinAnd1Min";

    /// <summary>任务提醒通知模式：PopupAndSound / PopupOnly / SoundOnly</summary>
    public string TaskReminderNotifyMode { get; set; } = "PopupAndSound";

    // ==================== 调试模式 ====================

    /// <summary>调试模式，开启后可访问待优化功能（截屏、磁盘空间分析、任务、AI Agent 检验）</summary>
    public bool DebugMode { get; set; }

    // ==================== AI Agent Token 数据源 ====================

    public string AgentTokenDataSource { get; set; } = "Local";

    public string AgentTokenApiProvider { get; set; } = "OpenAI";

    public string AgentTokenApiModel { get; set; } = "gpt-5.5";

    public string AgentTokenApiKey { get; set; } = "";

    // ==================== 配置文件完整性 ====================

    /// <summary>配置文件 SHA256 哈希值，用于完整性校验</summary>
    public string ConfigHash { get; set; } = "";
}
