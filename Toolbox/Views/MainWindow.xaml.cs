using Microsoft.Extensions.DependencyInjection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
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
    private readonly INavigationService _nav;
    private readonly ILogService _log;
    private readonly IConfigService _config;

    // 全局快捷键 ID 常量
    private const int HOTKEY_ID_SCREENSHOT = 1;
    private const int HOTKEY_ID_COLORPICKER = 2;
    private const int HOTKEY_ID_ALWAYSONTOP = 3;

    // Win32 修饰键常量
    private const uint MOD_ALT = 0x0001;
    private const uint MOD_CONTROL = 0x0002;
    private const uint MOD_SHIFT = 0x0004;
    private const uint MOD_WIN = 0x0008;

    /// <summary>
    /// 构造函数，通过 DI 获取导航和日志服务
    /// </summary>
    public MainWindow(INavigationService nav, ILogService log, IConfigService config)
    {
        InitializeComponent();

        _nav = nav;
        _log = log;
        _config = config;

        // 从 DI 容器获取 MainViewModel 并绑定为 DataContext
        var vm = App.ServiceProvider.GetRequiredService<MainViewModel>();
        DataContext = vm;

        // 订阅导航变化事件，自动切换内容区
        _nav.NavigationChanged += OnNavigationChanged;

        // 窗口加载完成后注册全局快捷键
        Loaded += RegisterGlobalHotkeys;

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
                    MessageBox.Show($"「{item.Name}」功能正在优化中，暂不可用。\n\n后续版本将重新开放。\n\n提示：可在 设置 → 🔧 调试 中开启调试模式以访问待优化功能。",
                        "功能待优化", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }
            }

            _nav.SelectedItem = item;
            _log.LogInfo($"Navigated to: {item.Name}");
        }
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
    private void RegisterGlobalHotkeys(object? sender, RoutedEventArgs e)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        var hwndSource = HwndSource.FromHwnd(hwnd);
        hwndSource?.AddHook(WndProc);

        // 从配置文件加载快捷键设置
        var settings = _config.LoadConfig<AppSettings>(_config.SettingsFilePath);

        // 注册截屏快捷键 (ID=1)
        if (TryParseShortcut(settings.ScreenshotShortcut, out uint mod1, out uint vk1))
        {
            if (Native.NativeMethods.RegisterHotKey(hwnd, HOTKEY_ID_SCREENSHOT, mod1, vk1))
                _log.LogInfo($"Hotkey registered: Screenshot ({settings.ScreenshotShortcut})");
            else
                _log.LogWarning($"Failed to register hotkey: Screenshot ({settings.ScreenshotShortcut})");
        }

        // 注册取色器快捷键 (ID=2)
        if (TryParseShortcut(settings.ColorPickerShortcut, out uint mod2, out uint vk2))
        {
            if (Native.NativeMethods.RegisterHotKey(hwnd, HOTKEY_ID_COLORPICKER, mod2, vk2))
                _log.LogInfo($"Hotkey registered: ColorPicker ({settings.ColorPickerShortcut})");
            else
                _log.LogWarning($"Failed to register hotkey: ColorPicker ({settings.ColorPickerShortcut})");
        }

        // 注册窗口置顶快捷键 (ID=3)
        if (TryParseShortcut(settings.AlwaysOnTopShortcut, out uint mod3, out uint vk3))
        {
            if (Native.NativeMethods.RegisterHotKey(hwnd, HOTKEY_ID_ALWAYSONTOP, mod3, vk3))
                _log.LogInfo($"Hotkey registered: AlwaysOnTop ({settings.AlwaysOnTopShortcut})");
            else
                _log.LogWarning($"Failed to register hotkey: AlwaysOnTop ({settings.AlwaysOnTopShortcut})");
        }

        _log.LogInfo("Global hotkey registration completed");
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
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        try
                        {
                            var shotVm = App.ServiceProvider.GetRequiredService<ScreenshotViewModel>();
                            shotVm.StartRegionCaptureFromHotkeyCommand.Execute(null);
                        }
                        catch (Exception ex)
                        {
                            _log.LogError("Hotkey Screenshot failed", ex);
                        }
                    }), System.Windows.Threading.DispatcherPriority.Background);
                    break;

                case HOTKEY_ID_COLORPICKER:
                    // 直接启动取色，不再只跳转页面
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        try
                        {
                            var pickVm = App.ServiceProvider.GetRequiredService<ColorPickerViewModel>();
                            pickVm.StartPickingCommand.Execute(null);
                        }
                        catch (Exception ex)
                        {
                            _log.LogError("Hotkey ColorPicker failed", ex);
                        }
                    }), System.Windows.Threading.DispatcherPriority.Background);
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

    /// <summary>
    /// 窗口关闭时清理资源
    /// 卸载已注册的全局快捷键
    /// </summary>
    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        try
        {
            var hwnd = new WindowInteropHelper(this).Handle;

            // 卸载已注册的快捷键
            Native.NativeMethods.UnregisterHotKey(hwnd, HOTKEY_ID_SCREENSHOT);
            Native.NativeMethods.UnregisterHotKey(hwnd, HOTKEY_ID_COLORPICKER);
            Native.NativeMethods.UnregisterHotKey(hwnd, HOTKEY_ID_ALWAYSONTOP);

            _log.LogInfo("All hotkeys unregistered, window closing");
        }
        catch (Exception ex)
        {
            _log.LogError("Error during cleanup", ex);
        }
    }
}
