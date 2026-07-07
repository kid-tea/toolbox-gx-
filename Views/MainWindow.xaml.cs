using Microsoft.Extensions.DependencyInjection;
using CommunityToolkit.Mvvm.Input;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using Toolbox.Models;
using Toolbox.Services;
using Toolbox.ViewModels;

namespace Toolbox.Views;

/// <summary>
/// 工具箱主窗口
/// 负责：窗口初始化、导航内容加载、全局快捷键注册、关闭时资源清理
/// </summary>
public partial class MainWindow : Window
{
    private const double ExpandedSidebarWidth = 260;
    private const double CollapsedSidebarWidth = 26;

    public static readonly DependencyProperty IsSidebarExpandedProperty =
        DependencyProperty.Register(
            nameof(IsSidebarExpanded),
            typeof(bool),
            typeof(MainWindow),
            new PropertyMetadata(true));

    private readonly INavigationService _nav;
    private readonly ILogService _log;
    private readonly IConfigService _config;
    private readonly IThemeService _theme;
    private IntPtr _hotkeyHwnd;
    private HwndSource? _hotkeySource;
    private bool _hotkeyHookRegistered;
    private bool _isScreenshotHotkeyRunning;
    private bool _isColorPickerHotkeyRunning;

    public bool IsSidebarExpanded
    {
        get => (bool)GetValue(IsSidebarExpandedProperty);
        set => SetValue(IsSidebarExpandedProperty, value);
    }

    // 全局快捷键 ID 常量
    private const int HOTKEY_ID_SCREENSHOT = 1;
    private const int HOTKEY_ID_COLORPICKER = 2;
    private const int HOTKEY_ID_ALWAYSONTOP = 3;

    // Win32 修饰键常量
    private const uint MOD_ALT = 0x0001;
    private const uint MOD_CONTROL = 0x0002;
    private const uint MOD_SHIFT = 0x0004;
    private const uint MOD_WIN = 0x0008;
    private const uint MOD_NOREPEAT = 0x4000;

    /// <summary>
    /// 构造函数，通过 DI 获取导航和日志服务
    /// </summary>
    public MainWindow(INavigationService nav, ILogService log, IConfigService config, IThemeService theme)
    {
        InitializeComponent();

        // 设置窗口图标
        Icon = System.Windows.Media.Imaging.BitmapFrame.Create(
            new Uri("pack://application:,,,/app.png", UriKind.Absolute));

        _nav = nav;
        _log = log;
        _config = config;
        _theme = theme;
        SetNormalShellResources();

        // 从 DI 容器获取 MainViewModel 并绑定为 DataContext
        var vm = App.ServiceProvider.GetRequiredService<MainViewModel>();
        DataContext = vm;
        SetSidebarExpanded(true);

        // 订阅导航变化事件，自动切换内容区
        _nav.NavigationChanged += OnNavigationChanged;
        _theme.ThemeChanged += OnThemeChanged;

        // 窗口句柄创建完成后注册全局快捷键。Loaded 有时太晚且不利于定位注册失败。
        SourceInitialized += RegisterGlobalHotkeys;

        // 恢复上次的导航状态
        if (_nav.CurrentViewType != null)
            LoadContent(_nav.CurrentViewType);

        // 窗口关闭时清理资源
        Closing += OnClosing;
    }

    /// <summary>
    /// 导航目标变化时的回调
    /// </summary>
    /// <param name="viewType">目标 View 类型，null 表示清空内容区</param>
    private void OnNavigationChanged(Type? viewType)
    {
        if (viewType != null)
            LoadContent(viewType);
        else
            ContentHost.Content = null;
    }

    private void OnThemeChanged(ThemeType theme)
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (_nav.CurrentViewType != null)
                LoadContent(_nav.CurrentViewType);
            else
                ApplyWindowChromeForView(typeof(object));
        }));
    }

    /// <summary>
    /// 加载指定类型的 View 到内容区
    /// 如果 View 未实现（如占位功能），则显示占位 TextBlock
    /// </summary>
    /// <param name="viewType">View 的 Type 类型</param>
    private void LoadContent(Type viewType)
    {
        try
        {
            // 尝试从 DI 容器获取 View 实例
            var view = App.ServiceProvider.GetService(viewType) as UserControl;
            if (view != null)
            {
                ContentHost.Content = view;
                ApplyWindowChromeForView(viewType);
            }
            else
            {
                // 占位视图：View 类型未注册或功能尚未实现时显示
                var placeholder = new TextBlock
                {
                    Text = $"功能「{viewType.Name.Replace("View", "")}」正在开发中...",
                    FontSize = 18,
                    Foreground = Application.Current.Resources["TextSecondaryBrush"] as System.Windows.Media.Brush,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                };
                ContentHost.Content = placeholder;
                ApplyWindowChromeForView(viewType);
            }
        }
        catch (Exception ex)
        {
            _log.LogError($"Failed to load view: {viewType.Name}", ex);

            // 显示加载失败的提示
            ContentHost.Content = new TextBlock
            {
                Text = $"加载页面失败: {ex.Message}",
                FontSize = 14,
                Foreground = Application.Current.Resources["DangerBrush"] as System.Windows.Media.Brush,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                TextWrapping = TextWrapping.Wrap
            };
        }
    }

    private void ApplyWindowChromeForView(Type viewType)
    {
        SetNormalShellResources();
        Background = Application.Current.Resources["BackgroundBrush"] as Brush;
        SidebarBorder.Background = Resources["ShellSidebarBrush"] as Brush;
        SidebarBorder.BorderBrush = Resources["ShellBorderBrush"] as Brush;
        ContentFrame.Background = Application.Current.Resources["ContentBrush"] as Brush;
        ContentFrame.BorderBrush = Resources["ShellBorderBrush"] as Brush;
    }

    private void SetNormalShellResources()
    {
        SetShellResource("ShellSidebarBrush", "SidebarBrush", GetBrush(0xF8, 0xFA, 0xFC));
        SetShellResource("ShellBorderBrush", "BorderBrush", GetBrush(0xE2, 0xE8, 0xF0));
        SetShellResource("ShellSidebarHoverBrush", "SidebarHoverBrush", GetBrush(0xF1, 0xF5, 0xF9));
        SetShellResource("ShellAccentLightBrush", "AccentLightBrush", GetBrush(0xDB, 0xEA, 0xFE));
        SetShellResource("ShellInputBackgroundBrush", "InputBackgroundBrush", GetBrush(0xFF, 0xFF, 0xFF));
        SetShellResource("ShellAccentBrush", "AccentBrush", GetBrush(0x3B, 0x82, 0xF6));
        SetShellResource("ShellTextPrimaryBrush", "TextPrimaryBrush", GetBrush(0x0F, 0x17, 0x2A));
        SetShellResource("ShellTextSecondaryBrush", "TextSecondaryBrush", GetBrush(0x47, 0x55, 0x69));
        SetShellResource("ShellTextMutedBrush", "TextMutedBrush", GetBrush(0x94, 0xA3, 0xB8));

        if (_theme.CurrentTheme == ThemeType.ContrastPro)
        {
            Resources["ShellSidebarBrush"] = GetBrush(0x16, 0x1B, 0x26);
            Resources["ShellSidebarHoverBrush"] = GetBrush(0x22, 0x2A, 0x38);
            Resources["ShellAccentLightBrush"] = GetBrush(0x0F, 0x76, 0x6E);
            Resources["ShellInputBackgroundBrush"] = GetBrush(0x1F, 0x29, 0x37);
            Resources["ShellBorderBrush"] = GetBrush(0x47, 0x55, 0x69);
            Resources["ShellTextPrimaryBrush"] = GetBrush(0xF8, 0xFA, 0xFC);
            Resources["ShellTextSecondaryBrush"] = GetBrush(0xCB, 0xD5, 0xE1);
            Resources["ShellTextMutedBrush"] = GetBrush(0x94, 0xA3, 0xB8);
        }
    }

    private void SetAgentInspectorShellResources()
    {
        Resources["ShellSidebarBrush"] = GetBrush(0x17, 0x1A, 0x23);
        Resources["ShellBorderBrush"] = GetBrush(0x2C, 0x33, 0x44);
        Resources["ShellSidebarHoverBrush"] = GetBrush(0x22, 0x28, 0x38);
        Resources["ShellAccentLightBrush"] = GetBrush(0x24, 0x2A, 0x3A);
        Resources["ShellInputBackgroundBrush"] = GetBrush(0x20, 0x25, 0x32);
        Resources["ShellAccentBrush"] = GetBrush(0x76, 0x67, 0xF2);
        Resources["ShellTextPrimaryBrush"] = GetBrush(0xF3, 0xF6, 0xFF);
        Resources["ShellTextSecondaryBrush"] = GetBrush(0xC4, 0xCC, 0xE0);
        Resources["ShellTextMutedBrush"] = GetBrush(0x8E, 0x99, 0xB5);
    }

    private void SetShellResource(string shellKey, string appKey, Brush fallback)
    {
        Resources[shellKey] = Application.Current.Resources[appKey] as Brush ?? fallback;
    }

    private static SolidColorBrush GetBrush(byte r, byte g, byte b)
    {
        return new SolidColorBrush(Color.FromRgb(r, g, b));
    }

    /// <summary>
    /// 侧边栏底部设置按钮点击时，直接导航到设置页面
    /// 不再只切换布尔值，避免“按钮有反应但页面不出来”的问题。
    /// </summary>
    private void OnSettingsButtonClick(object sender, RoutedEventArgs e)
    {
        _nav.Navigate(typeof(SettingsView));
        _log.LogInfo("Navigated to Settings page");
    }

    /// <summary>
    /// 侧边栏导航项点击事件
    /// 通过 DataContext 获取 NavItem，触发导航
    /// </summary>
    private void OnNavItemClicked(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement element && element.DataContext is NavItem item)
        {
            if (item.IsPendingOptimization)
            {
                var settings = _config.LoadConfig<AppSettings>(_config.SettingsFilePath) ?? new AppSettings();
                if (!settings.DebugMode)
                {
                    if (ShowPendingOptimizationMessage(item.Name))
                        return;
                    MessageBox.Show($"「{item.Name}」功能正在优化中，暂不可用。\n\n后续版本将重新开放。\n\n提示：可在 设置 → 🔧 调试 中开启调试模式以访问待优化功能。",
                        "功能待优化", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }
            }

            _nav.SelectedItem = item;
            _log.LogInfo($"Navigated to: {item.Name}");
        }
    }

    private void OnSidebarToggleClick(object sender, RoutedEventArgs e)
    {
        SetSidebarExpanded(!IsSidebarExpanded);
    }

    private static bool ShowPendingOptimizationMessage(string featureName)
    {
        MessageBox.Show(
            $"\u300C{featureName}\u300D\u6B63\u5728\u4F18\u5316\u4E2D\uFF0C\u6682\u65F6\u4E0D\u5EFA\u8BAE\u4F5C\u4E3A\u6B63\u5F0F\u529F\u80FD\u4F7F\u7528\u3002\n\n" +
            "\u540E\u7EED\u7248\u672C\u4F1A\u91CD\u65B0\u5F00\u653E\u3002\n\n" +
            "\u5982\u9700\u9884\u89C8\uFF0C\u53EF\u5728\u300C\u8BBE\u7F6E\u300D\u4E2D\u5F00\u542F\u8C03\u8BD5\u6A21\u5F0F\u3002",
            "\u529F\u80FD\u5F85\u4F18\u5316",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
        return true;
    }

    private void SetSidebarExpanded(bool expanded)
    {
        IsSidebarExpanded = expanded;
        if (SidebarColumn != null)
            SidebarColumn.Width = new GridLength(expanded ? ExpandedSidebarWidth : CollapsedSidebarWidth);
    }

    /// <summary>
    /// 分类标题点击 — 折叠/展开该分类下的导航项
    /// </summary>
    private void OnCategoryHeaderClicked(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement element && element.DataContext is NavCategoryGroup group)
        {
            var category = group.Category;
            var vm = DataContext as MainViewModel;
            if (vm == null) return;

            if (vm.CollapsedCategories.Contains(category))
                vm.CollapsedCategories.Remove(category);
            else
                vm.CollapsedCategories.Add(category);

            vm.RefreshGroupedNavItems();
        }
    }

    /// <summary>
    /// 注册全局快捷键（窗口加载完成后）
    /// 从 AppSettings 读取快捷键配置，解析后注册系统级热键
    /// 使用 WndProc 消息钩子接收 WM_HOTKEY 消息
    /// </summary>
    private void RegisterGlobalHotkeys(object? sender, EventArgs e)
    {
        _hotkeyHwnd = new WindowInteropHelper(this).Handle;
        if (_hotkeyHwnd == IntPtr.Zero)
        {
            _log.LogWarning("Global hotkey registration skipped: window handle is not ready");
            return;
        }

        _hotkeySource = HwndSource.FromHwnd(_hotkeyHwnd);
        if (_hotkeySource == null)
        {
            _log.LogWarning("Global hotkey registration skipped: HwndSource is not ready");
            return;
        }

        if (!_hotkeyHookRegistered)
        {
            _hotkeySource.AddHook(WndProc);
            _hotkeyHookRegistered = true;
        }

        ReloadGlobalHotkeys();
        _log.LogInfo("Global hotkey registration completed");
    }

    private void ReloadGlobalHotkeys()
    {
        if (_hotkeyHwnd == IntPtr.Zero)
            return;

        UnregisterGlobalHotkeys();

        var settings = _config.LoadConfig<AppSettings>(_config.SettingsFilePath);
        RegisterConfiguredHotkey(HOTKEY_ID_SCREENSHOT, "Screenshot", settings.ScreenshotShortcut, "Ctrl+Shift+X");
        RegisterConfiguredHotkey(HOTKEY_ID_COLORPICKER, "ColorPicker", settings.ColorPickerShortcut, "Ctrl+Shift+C");
        RegisterConfiguredHotkey(HOTKEY_ID_ALWAYSONTOP, "AlwaysOnTop", settings.AlwaysOnTopShortcut, "Ctrl+Shift+T");
    }

    public void RefreshGlobalHotkeysFromSettings()
    {
        ReloadGlobalHotkeys();
    }

    private void RegisterConfiguredHotkey(int id, string name, string shortcut, string fallbackShortcut)
    {
        if (!TryParseShortcut(shortcut, out uint modifiers, out uint vk))
        {
            _log.LogWarning($"Failed to parse hotkey: {name} ({shortcut})");
            shortcut = fallbackShortcut;
            if (!TryParseShortcut(shortcut, out modifiers, out vk))
                return;
        }

        if (!IsSafeGlobalHotkey(modifiers))
        {
            _log.LogWarning($"Unsafe global hotkey ignored: {name} ({shortcut}), fallback={fallbackShortcut}");
            shortcut = fallbackShortcut;
            if (!TryParseShortcut(shortcut, out modifiers, out vk))
                return;
        }

        uint flags = modifiers | MOD_NOREPEAT;
        if (Native.NativeMethods.RegisterHotKey(_hotkeyHwnd, id, flags, vk))
        {
            _log.LogInfo($"Hotkey registered: {name} ({shortcut})");
            return;
        }

        int error = Marshal.GetLastWin32Error();
        _log.LogWarning($"Failed to register hotkey: {name} ({shortcut}), Win32Error={error}");
    }

    private static bool IsSafeGlobalHotkey(uint modifiers)
    {
        return (modifiers & (MOD_CONTROL | MOD_ALT | MOD_WIN)) != 0;
    }

    /// <summary>
    /// 解析快捷键字符串为修饰键和虚拟键码
    /// 格式如 "Ctrl+Shift+X"，支持 Ctrl / Shift / Alt / Win 修饰键
    /// </summary>
    /// <param name="shortcut">快捷键字符串</param>
    /// <param name="modifiers">输出：Win32 修饰键标志位组合</param>
    /// <param name="vk">输出：虚拟键码</param>
    /// <returns>解析成功返回 true，格式无效返回 false</returns>
    private bool TryParseShortcut(string shortcut, out uint modifiers, out uint vk)
    {
        modifiers = 0;
        vk = 0;

        if (string.IsNullOrWhiteSpace(shortcut))
            return false;

        var parts = shortcut.Split('+', StringSplitOptions.TrimEntries);
        if (parts.Length < 2)
            return false;

        // 最后一个部分为按键，前面的为修饰键
        var keyPart = parts[^1];

        // 解析修饰键
        foreach (var part in parts.Take(parts.Length - 1))
        {
            switch (part.ToLowerInvariant())
            {
                case "ctrl":
                    modifiers |= MOD_CONTROL;
                    break;
                case "shift":
                    modifiers |= MOD_SHIFT;
                    break;
                case "alt":
                    modifiers |= MOD_ALT;
                    break;
                case "win":
                    modifiers |= MOD_WIN;
                    break;
                default:
                    _log.LogWarning($"Unknown modifier in shortcut: {part}");
                    return false;
            }
        }

        // 解析按键：使用 KeyInterop 将 WPF Key 转为虚拟键码
        try
        {
            if (keyPart.Length == 1 && char.IsLetterOrDigit(keyPart[0]))
            {
                // 单字母键直接使用大写
                var key = (Key)Enum.Parse(typeof(Key), keyPart.ToUpper());
                vk = (uint)KeyInterop.VirtualKeyFromKey(key);
            }
            else if (System.Enum.TryParse(keyPart, ignoreCase: true, out Key parsedKey))
            {
                // F1-F12, Escape, Space, 等特殊键
                vk = (uint)KeyInterop.VirtualKeyFromKey(parsedKey);
            }
            else
            {
                _log.LogWarning($"Unknown key in shortcut: {keyPart}");
                return false;
            }
        }
        catch
        {
            _log.LogWarning($"Failed to parse key: {keyPart}");
            return false;
        }

        return modifiers != 0 && vk != 0;
    }

    /// <summary>
    /// 窗口消息处理钩子
    /// 处理 WM_HOTKEY 消息，根据热键 ID 分派到对应功能
    /// ID=1: 截屏 | ID=2: 取色器 | ID=3: 窗口置顶
    /// </summary>
    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        const int WM_HOTKEY = 0x0312;

        if (msg == WM_HOTKEY)
        {
            // 从 wParam 获取热键 ID
            int hotkeyId = wParam.ToInt32();
            _log.LogInfo($"Hotkey triggered: ID={hotkeyId}");

            switch (hotkeyId)
            {
                case HOTKEY_ID_SCREENSHOT:
                    // 直接启动区域截图，不再只跳转页面
                    Dispatcher.BeginInvoke(new Action(async () => await HandleScreenshotHotkeyAsync()),
                        System.Windows.Threading.DispatcherPriority.Background);
                    break;

                case HOTKEY_ID_COLORPICKER:
                    // 直接启动取色，不再只跳转页面
                    Dispatcher.BeginInvoke(new Action(async () => await HandleColorPickerHotkeyAsync()),
                        System.Windows.Threading.DispatcherPriority.Background);
                    break;

                case HOTKEY_ID_ALWAYSONTOP:
                    // 直接置顶当前前台窗口，不跳转页面
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        try
                        {
                            var topVm = App.ServiceProvider.GetRequiredService<AlwaysOnTopViewModel>();
                            topVm.Initialize(new WindowInteropHelper(this).Handle);
                            topVm.TopmostForegroundWindowCommand.Execute(null);
                        }
                        catch (Exception ex)
                        {
                            _log.LogError("Hotkey AlwaysOnTop failed", ex);
                        }
                    }), System.Windows.Threading.DispatcherPriority.Background);
                    break;
            }

            // 标记消息已处理
            handled = true;
        }

        return IntPtr.Zero;
    }

    private async Task HandleScreenshotHotkeyAsync()
    {
        if (_isScreenshotHotkeyRunning)
        {
            _log.LogInfo("Hotkey Screenshot ignored: capture is already running");
            return;
        }

        try
        {
            _isScreenshotHotkeyRunning = true;
            _log.LogInfo("Hotkey Screenshot handling started");

            var shotVm = App.ServiceProvider.GetRequiredService<ScreenshotViewModel>();
            await shotVm.StartRegionCaptureFromHotkeyCommand.ExecuteAsync(null);

            if (shotVm.HasResult && ContentHost.Content is not ScreenshotView)
                _nav.Navigate(typeof(ScreenshotView));

            _log.LogInfo("Hotkey Screenshot handling completed");
        }
        catch (Exception ex)
        {
            _log.LogError("Hotkey Screenshot failed", ex);
        }
        finally
        {
            _isScreenshotHotkeyRunning = false;
        }
    }

    private async Task HandleColorPickerHotkeyAsync()
    {
        if (_isColorPickerHotkeyRunning)
        {
            _log.LogInfo("Hotkey ColorPicker ignored: picking is already running");
            return;
        }

        try
        {
            _isColorPickerHotkeyRunning = true;
            _log.LogInfo("Hotkey ColorPicker handling started");

            var pickVm = App.ServiceProvider.GetRequiredService<ColorPickerViewModel>();
            await pickVm.StartPickingCommand.ExecuteAsync(null);

            if (pickVm.HasPickedColor && ContentHost.Content is not ColorPickerView)
                _nav.Navigate(typeof(ColorPickerView));

            _log.LogInfo("Hotkey ColorPicker handling completed");
        }
        catch (Exception ex)
        {
            _log.LogError("Hotkey ColorPicker failed", ex);
        }
        finally
        {
            _isColorPickerHotkeyRunning = false;
        }
    }

    private void UnregisterGlobalHotkeys()
    {
        if (_hotkeyHwnd == IntPtr.Zero)
            return;

        Native.NativeMethods.UnregisterHotKey(_hotkeyHwnd, HOTKEY_ID_SCREENSHOT);
        Native.NativeMethods.UnregisterHotKey(_hotkeyHwnd, HOTKEY_ID_COLORPICKER);
        Native.NativeMethods.UnregisterHotKey(_hotkeyHwnd, HOTKEY_ID_ALWAYSONTOP);
    }

    /// <summary>
    /// 窗口关闭时清理资源
    /// 卸载已注册的全局快捷键
    /// </summary>
    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        try
        {
            _theme.ThemeChanged -= OnThemeChanged;
            UnregisterGlobalHotkeys();

            if (_hotkeyHookRegistered && _hotkeySource != null)
            {
                _hotkeySource.RemoveHook(WndProc);
                _hotkeyHookRegistered = false;
            }

            _log.LogInfo("All hotkeys unregistered, window closing");
        }
        catch (Exception ex)
        {
            _log.LogError("Error during cleanup", ex);
        }
    }
}
