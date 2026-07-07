using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Toolbox.ViewModels;

namespace Toolbox.Views;

/// <summary>
/// 设置视图代码后置
/// 处理快捷键录制的键盘事件和按钮点击
/// </summary>
public partial class SettingsView : UserControl
{
    private SettingsViewModel VM => (SettingsViewModel)DataContext;
    private bool _isRecording;

    /// <summary>
    /// 构造函数 — 注入 ViewModel，并注册键盘事件监听
    /// </summary>
    public SettingsView(SettingsViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        KeyDown += OnSettingsKeyDown;
        Focusable = true;
    }

    /// <summary>
    /// 录制截屏快捷键
    /// </summary>
    private void OnRecordScreenshotShortcut(object sender, RoutedEventArgs e)
    {
        VM.StartRecordingCommand.Execute("截屏");
        StartKeyboardCapture();
    }

    /// <summary>
    /// 录制取色器快捷键
    /// </summary>
    private void OnRecordColorPickerShortcut(object sender, RoutedEventArgs e)
    {
        VM.StartRecordingCommand.Execute("取色器");
        StartKeyboardCapture();
    }

    /// <summary>
    /// 录制窗口置顶快捷键
    /// </summary>
    private void OnRecordAlwaysOnTopShortcut(object sender, RoutedEventArgs e)
    {
        VM.StartRecordingCommand.Execute("窗口置顶");
        StartKeyboardCapture();
    }

    /// <summary>
    /// 开始全局键盘捕获
    /// </summary>
    private void StartKeyboardCapture()
    {
        _isRecording = true;
        Focus();
        Keyboard.Focus(this);
    }

    /// <summary>
    /// 键盘按下事件 — 录制快捷键组合
    /// </summary>
    private void OnSettingsKeyDown(object sender, KeyEventArgs e)
    {
        if (!_isRecording) return;

        if (e.Key == Key.LeftCtrl || e.Key == Key.RightCtrl ||
            e.Key == Key.LeftAlt || e.Key == Key.RightAlt ||
            e.Key == Key.LeftShift || e.Key == Key.RightShift ||
            e.Key == Key.LWin || e.Key == Key.RWin ||
            e.Key == Key.System)
            return;

        var parts = new List<string>();

        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
            parts.Add("Ctrl");
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Alt))
            parts.Add("Alt");
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
            parts.Add("Shift");
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Windows))
            parts.Add("Win");

        parts.Add(e.Key.ToString());

        string combination = string.Join("+", parts);
        VM.RecordShortcut(combination);
        (Window.GetWindow(this) as MainWindow)?.RefreshGlobalHotkeysFromSettings();
        _isRecording = false;
        e.Handled = true;
    }
}
