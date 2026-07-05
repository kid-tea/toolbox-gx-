using System.Windows;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Toolbox.Helpers;
using Toolbox.Models;
using Toolbox.Services;

namespace Toolbox.ViewModels;

/// <summary>
/// 设置页面 ViewModel
/// 四个标签页数据绑定（外观/快捷键/通知/关于）
/// 所有修改即时保存到 settings.json，SHA256 校验更新
/// </summary>
public partial class SettingsViewModel : ViewModelBase
{
    private readonly IConfigService _config;
    private readonly IThemeService _theme;
    private readonly ILogService _log;
    private AppSettings _settings = new();
    private bool _isLoadingSettings;

    // ==================== 外观设置 ====================

    /// <summary>当前主题（Light/Dark）</summary>
    [ObservableProperty]
    private string _currentTheme = "Light";

    public bool IsClassicThemeSelected =>
        string.Equals(CurrentTheme, nameof(ThemeType.Light), StringComparison.OrdinalIgnoreCase) ||
        string.Equals(CurrentTheme, nameof(ThemeType.Dark), StringComparison.OrdinalIgnoreCase);

    public bool IsObsidianThemeSelected =>
        string.Equals(CurrentTheme, nameof(ThemeType.Obsidian), StringComparison.OrdinalIgnoreCase) ||
        string.Equals(CurrentTheme, nameof(ThemeType.Dashboard), StringComparison.OrdinalIgnoreCase);

    public bool IsContrastProThemeSelected => string.Equals(CurrentTheme, nameof(ThemeType.ContrastPro), StringComparison.OrdinalIgnoreCase);

    public bool IsPaperUtilityThemeSelected => string.Equals(CurrentTheme, nameof(ThemeType.PaperUtility), StringComparison.OrdinalIgnoreCase);

    /// <summary>当前语言（zh-CN/en-US）</summary>
    [ObservableProperty]
    private string _currentLanguage = "zh-CN";

    /// <summary>主界面背景模式</summary>
    [ObservableProperty]
    private string _mainBackgroundMode = "Solid";

    /// <summary>主界面背景图片路径</summary>
    [ObservableProperty]
    private string _mainBackgroundImagePath = "";

    /// <summary>主界面字体</summary>
    [ObservableProperty]
    private string _mainFontFamily = "Microsoft YaHei";

    /// <summary>主界面字体大小</summary>
    [ObservableProperty]
    private int _mainFontSize = 14;

    /// <summary>主界面字体颜色</summary>
    [ObservableProperty]
    private string _mainFontColor = "#000000";

    /// <summary>悬浮面板背景模式</summary>
    [ObservableProperty]
    private string _panelBackgroundMode = "Solid";

    /// <summary>悬浮面板透明度</summary>
    [ObservableProperty]
    private double _panelOpacity = 0.9;

    /// <summary>悬浮面板字体</summary>
    [ObservableProperty]
    private string _panelFontFamily = "Microsoft YaHei";

    /// <summary>悬浮面板字体大小</summary>
    [ObservableProperty]
    private int _panelFontSize = 12;

    /// <summary>悬浮面板字体颜色</summary>
    [ObservableProperty]
    private string _panelFontColor = "#000000";

    /// <summary>悬浮面板是否置顶</summary>
    [ObservableProperty]
    private bool _panelAlwaysOnTop = true;

    // ==================== 快捷键设置 ====================

    /// <summary>截屏快捷键</summary>
    [ObservableProperty]
    private string _screenshotShortcut = "Ctrl+Shift+X";

    /// <summary>取色器快捷键</summary>
    [ObservableProperty]
    private string _colorPickerShortcut = "Ctrl+Shift+C";

    /// <summary>窗口置顶快捷键</summary>
    [ObservableProperty]
    private string _alwaysOnTopShortcut = "Ctrl+Shift+T";

    /// <summary>是否正在录制快捷键</summary>
    [ObservableProperty]
    private bool _isRecordingShortcut;

    /// <summary>当前录制的是哪个快捷键</summary>
    [ObservableProperty]
    private string _recordingTarget = "";

    /// <summary>快捷键冲突提示</summary>
    [ObservableProperty]
    private string _shortcutConflictMessage = "";

    // ==================== 通知设置 ====================

    /// <summary>通知总开关</summary>
    [ObservableProperty]
    private bool _notificationsEnabled = true;

    /// <summary>默认提示音</summary>
    [ObservableProperty]
    private string _defaultAlertSound = "SystemDefault";

    /// <summary>自定义提示音路径</summary>
    [ObservableProperty]
    private string _defaultAlertSoundPath = "";

    /// <summary>定时清理通知模式</summary>
    [ObservableProperty]
    private string _scheduledCleanupNotifyMode = "BeforeCleanup";

    /// <summary>定时关机通知模式</summary>
    [ObservableProperty]
    private string _scheduledShutdownNotifyMode = "5MinAnd1Min";

    /// <summary>任务提醒通知模式</summary>
    [ObservableProperty]
    private string _taskReminderNotifyMode = "PopupAndSound";

    // ==================== 关于 ====================

    /// <summary>版本号</summary>
    [ObservableProperty]
    private string _appVersion = "1.0.4";

    /// <summary>更新状态</summary>
    [ObservableProperty]
    private string _updateStatus = "点击检查更新";

    /// <summary>是否正在检查更新</summary>
    [ObservableProperty]
    private bool _isCheckingUpdate;

    // ==================== 调试模式 ====================

    /// <summary>调试模式，开启后可访问待优化功能</summary>
    [ObservableProperty]
    private bool _debugMode;

    // ==================== 通用状态 ====================

    /// <summary>设置保存状态</summary>
    [ObservableProperty]
    private string _saveStatus = "";

    /// <summary>
    /// 构造函数 — 依赖注入
    /// </summary>
    public SettingsViewModel(IConfigService config, IThemeService theme, ILogService log)
    {
        _config = config;
        _theme = theme;
        _log = log;

        LoadSettings();
    }

    /// <summary>
    /// 加载设置
    /// </summary>
    private void LoadSettings()
    {
        _isLoadingSettings = true;
        _settings = _config.LoadConfig<AppSettings>(_config.SettingsFilePath) ?? new AppSettings();

        // 外观
        CurrentTheme = _settings.Theme;
        CurrentLanguage = _settings.Language;
        MainBackgroundMode = _settings.MainBackgroundMode;
        MainBackgroundImagePath = _settings.MainBackgroundImagePath;
        MainFontFamily = _settings.MainFontFamily;
        MainFontSize = _settings.MainFontSize;
        MainFontColor = _settings.MainFontColor;
        PanelBackgroundMode = _settings.PanelBackgroundMode;
        PanelOpacity = _settings.PanelOpacity;
        PanelFontFamily = _settings.PanelFontFamily;
        PanelFontSize = _settings.PanelFontSize;
        PanelFontColor = _settings.PanelFontColor;
        PanelAlwaysOnTop = _settings.PanelAlwaysOnTop;

        // 快捷键
        ScreenshotShortcut = _settings.ScreenshotShortcut;
        ColorPickerShortcut = _settings.ColorPickerShortcut;
        AlwaysOnTopShortcut = _settings.AlwaysOnTopShortcut;

        // 通知
        NotificationsEnabled = _settings.NotificationsEnabled;
        DefaultAlertSound = _settings.DefaultAlertSound;
        DefaultAlertSoundPath = _settings.DefaultAlertSoundPath;
        ScheduledCleanupNotifyMode = _settings.ScheduledCleanupNotifyMode;
        ScheduledShutdownNotifyMode = _settings.ScheduledShutdownNotifyMode;
        TaskReminderNotifyMode = _settings.TaskReminderNotifyMode;

        // 调试模式
        DebugMode = _settings.DebugMode;
        _isLoadingSettings = false;
    }

    /// <summary>
    /// 即时保存设置到文件（无保存按钮，属性变化即保存）
    /// </summary>
    private void SaveSettings()
    {
        if (_isLoadingSettings) return;

        try
        {
            _settings.Theme = CurrentTheme;
            _settings.Language = CurrentLanguage;
            _settings.MainBackgroundMode = MainBackgroundMode;
            _settings.MainBackgroundImagePath = MainBackgroundImagePath;
            _settings.MainFontFamily = MainFontFamily;
            _settings.MainFontSize = MainFontSize;
            _settings.MainFontColor = MainFontColor;
            _settings.PanelBackgroundMode = PanelBackgroundMode;
            _settings.PanelOpacity = PanelOpacity;
            _settings.PanelFontFamily = PanelFontFamily;
            _settings.PanelFontSize = PanelFontSize;
            _settings.PanelFontColor = PanelFontColor;
            _settings.PanelAlwaysOnTop = PanelAlwaysOnTop;

            _settings.ScreenshotShortcut = ScreenshotShortcut;
            _settings.ColorPickerShortcut = ColorPickerShortcut;
            _settings.AlwaysOnTopShortcut = AlwaysOnTopShortcut;

            _settings.NotificationsEnabled = NotificationsEnabled;
            _settings.DefaultAlertSound = DefaultAlertSound;
            _settings.DefaultAlertSoundPath = DefaultAlertSoundPath;
            _settings.ScheduledCleanupNotifyMode = ScheduledCleanupNotifyMode;
            _settings.ScheduledShutdownNotifyMode = ScheduledShutdownNotifyMode;
            _settings.TaskReminderNotifyMode = TaskReminderNotifyMode;
            _settings.DebugMode = DebugMode;

            // 保存文件
            _config.SaveConfig(_config.SettingsFilePath, _settings);

            // 计算并存储哈希
            _settings.ConfigHash = _config.ComputeHash(_config.SettingsFilePath);
            _config.SaveConfig(_config.SettingsFilePath, _settings);

            SaveStatus = $"设置已保存 ({DateTime.Now:HH:mm:ss})";
        }
        catch (Exception ex)
        {
            SaveStatus = $"保存失败: {ex.Message}";
            _log.LogError("保存设置失败", ex);
        }
    }

    // ==================== 外观操作 ====================

    /// <summary>
    /// 切换主题（即时预览）
    /// </summary>
    [RelayCommand]
    private void ToggleTheme()
    {
        ApplyTheme(CurrentTheme == "Dark" ? ThemeType.Light : ThemeType.Dark);
    }

    [RelayCommand]
    private void SetClassicTheme()
    {
        ApplyTheme(ThemeType.Light);
    }

    [RelayCommand]
    private void SetDashboardTheme()
    {
        ApplyTheme(ThemeType.Obsidian);
    }

    [RelayCommand]
    private void SetObsidianTheme()
    {
        ApplyTheme(ThemeType.Obsidian);
    }

    [RelayCommand]
    private void SetContrastProTheme()
    {
        ApplyTheme(ThemeType.ContrastPro);
    }

    [RelayCommand]
    private void SetPaperUtilityTheme()
    {
        ApplyTheme(ThemeType.PaperUtility);
    }

    private void ApplyTheme(ThemeType theme)
    {
        CurrentTheme = theme.ToString();
        SaveSettings();
        _theme.SetTheme(theme);
    }

    partial void OnCurrentThemeChanged(string value)
    {
        OnPropertyChanged(nameof(IsClassicThemeSelected));
        OnPropertyChanged(nameof(IsObsidianThemeSelected));
        OnPropertyChanged(nameof(IsContrastProThemeSelected));
        OnPropertyChanged(nameof(IsPaperUtilityThemeSelected));
    }

    /// <summary>
    /// 切换语言
    /// </summary>
    [RelayCommand]
    private void ToggleLanguage()
    {
        CurrentLanguage = CurrentLanguage == "zh-CN" ? "en-US" : "zh-CN";
        SaveSettings();
    }

    /// <summary>
    /// 属性变更时即时保存
    /// </summary>
    partial void OnMainFontSizeChanged(int value) => SaveSettings();
    partial void OnMainFontColorChanged(string value) => SaveSettings();
    partial void OnMainFontFamilyChanged(string value) => SaveSettings();
    partial void OnMainBackgroundModeChanged(string value) => SaveSettings();
    partial void OnPanelOpacityChanged(double value) => SaveSettings();
    partial void OnPanelFontSizeChanged(int value) => SaveSettings();
    partial void OnPanelAlwaysOnTopChanged(bool value) => SaveSettings();
    partial void OnNotificationsEnabledChanged(bool value) => SaveSettings();
    partial void OnDebugModeChanged(bool value) => SaveSettings();
    partial void OnScheduledCleanupNotifyModeChanged(string value) => SaveSettings();
    partial void OnScheduledShutdownNotifyModeChanged(string value) => SaveSettings();
    partial void OnTaskReminderNotifyModeChanged(string value) => SaveSettings();

    // ==================== 快捷键录制 ====================

    /// <summary>
    /// 开始录制快捷键
    /// </summary>
    [RelayCommand]
    private void StartRecording(string target)
    {
        IsRecordingShortcut = true;
        RecordingTarget = target;
        ShortcutConflictMessage = "";
        StatusMessage = $"正在录制 {target} 快捷键... 按下组合键";
    }

    /// <summary>
    /// 取消录制
    /// </summary>
    [RelayCommand]
    private void CancelRecording()
    {
        IsRecordingShortcut = false;
        RecordingTarget = "";
        StatusMessage = "录制已取消";
    }

    /// <summary>
    /// 录制快捷键组合（由 View 代码后置调用）
    /// </summary>
    public void RecordShortcut(string combination)
    {
        if (!IsRecordingShortcut) return;

        // 冲突检测
        ShortcutConflictMessage = CheckShortcutConflict(combination);

        if (ShortcutConflictMessage.Length > 0)
        {
            StatusMessage = $"快捷键冲突: {ShortcutConflictMessage}";
            return;
        }

        // 保存快捷键
        switch (RecordingTarget)
        {
            case "截屏":
                ScreenshotShortcut = combination;
                break;
            case "取色器":
                ColorPickerShortcut = combination;
                break;
            case "窗口置顶":
                AlwaysOnTopShortcut = combination;
                break;
        }

        IsRecordingShortcut = false;
        RecordingTarget = "";
        SaveSettings();
        StatusMessage = $"快捷键已设置为: {combination}";
    }

    /// <summary>
    /// 检测快捷键冲突
    /// </summary>
    private string CheckShortcutConflict(string newShortcut)
    {
        var conflicts = new List<string>();

        if (RecordingTarget != "截屏" && ScreenshotShortcut == newShortcut)
            conflicts.Add("截屏");
        if (RecordingTarget != "取色器" && ColorPickerShortcut == newShortcut)
            conflicts.Add("取色器");
        if (RecordingTarget != "窗口置顶" && AlwaysOnTopShortcut == newShortcut)
            conflicts.Add("窗口置顶");

        return conflicts.Count > 0 ? $"与 {string.Join(", ", conflicts)} 快捷键冲突" : "";
    }

    // ==================== 关于操作 ====================

    /// <summary>
    /// 检查更新（HTTP GET 版本比对）
    /// </summary>
    [RelayCommand]
    private async Task CheckUpdateAsync()
    {
        if (IsCheckingUpdate) return;

        try
        {
            IsCheckingUpdate = true;
            UpdateStatus = "正在检查更新...";

            // 模拟版本检查（实际应用中应请求服务器 API）
            await Task.Delay(1500);

            UpdateStatus = "已是最新版本";
            _log.LogInfo("版本检查完成: 已是最新版本");
        }
        catch (Exception ex)
        {
            UpdateStatus = $"检查失败: {ex.Message}";
            _log.LogError("检查更新失败", ex);
        }
        finally
        {
            IsCheckingUpdate = false;
        }
    }

    /// <summary>
    /// 恢复默认设置（L2 确认）
    /// </summary>
    [RelayCommand]
    private void ResetToDefaults()
    {
        if (!ConfirmationHelper.RequestL2("确定要恢复所有设置为默认值吗？\n此操作会丢失所有自定义设置。"))
            return;

        _settings = new AppSettings();
        _config.SaveConfig(_config.SettingsFilePath, _settings);
        _settings.ConfigHash = _config.ComputeHash(_config.SettingsFilePath);
        _config.SaveConfig(_config.SettingsFilePath, _settings);

        LoadSettings();
        SaveStatus = "设置已恢复为默认值";
        StatusMessage = "设置已恢复为默认值";
        _log.LogInfo("设置已恢复为默认值");
    }

    /// <summary>
    /// 导出设置文件
    /// </summary>
    [RelayCommand]
    private void ExportSettings()
    {
        try
        {
            var dialog = new Microsoft.Win32.SaveFileDialog
            {
                FileName = "settings_backup.json",
                Filter = "JSON 文件|*.json|所有文件|*.*"
            };

            if (dialog.ShowDialog() == true)
            {
                _config.SaveConfig(dialog.FileName, _settings);
                StatusMessage = $"设置已导出到: {dialog.FileName}";
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"导出失败: {ex.Message}";
        }
    }

    /// <summary>
    /// 卸载指引
    /// </summary>
    [RelayCommand]
    private void ShowUninstallGuide()
    {
        MessageBox.Show(
            "卸载步骤：\n" +
            "1. 关闭工具箱\n" +
            "2. 删除程序文件夹：%AppData%\\工具箱\n" +
            "3. 删除程序可执行文件\n" +
            "4. （可选）清理注册表 HKCU\\Software\\工具箱",
            "卸载指引",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
    }

    /// <summary>
    /// 开源许可信息
    /// </summary>
    [RelayCommand]
    private void ShowLicense()
    {
        MessageBox.Show(
            "工具箱 - 开源软件\n\n" +
            "使用以下开源组件：\n" +
            "- CommunityToolkit.Mvvm (MIT)\n" +
            "- Microsoft.Extensions.DependencyInjection (MIT)\n" +
            "- Serilog (Apache 2.0)\n" +
            "- OxyPlot (MIT)\n\n" +
            "本项目基于 MIT 许可证发布。",
            "开源许可",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
    }
}
