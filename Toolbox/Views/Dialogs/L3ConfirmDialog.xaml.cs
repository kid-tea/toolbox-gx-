using System.Windows;
using System.Windows.Threading;

namespace Toolbox.Views.Dialogs;

/// <summary>
/// L3 级别确认弹窗 — 5秒冷却 + 手动输入确认短语
/// 用户必须等待冷却时间结束，并且正确输入指定短语后才能点击确认按钮
/// 适用于危险操作（文件粉碎、C盘清理高级项、注册表修复等）
/// </summary>
public partial class L3ConfirmDialog : Window
{
    private readonly string _confirmPhrase;
    private readonly DispatcherTimer _timer;
    private int _cooldownSeconds = 5; // 冷却秒数
    private bool _cooledDown; // 是否已完成冷却

    /// <summary>
    /// 构造函数
    /// </summary>
    /// <param name="message">警告消息</param>
    /// <param name="confirmPhrase">用户需要输入的确认短语</param>
    public L3ConfirmDialog(string message, string confirmPhrase)
    {
        InitializeComponent();

        _confirmPhrase = confirmPhrase;
        Owner = Application.Current.MainWindow;

        // 设置消息和提示
        MessageText.Text = message;
        InputPromptText.Text = $"请在下方输入「{confirmPhrase}」以确认操作：";

        // 初始化按钮状态
        UpdateButtonText();
        UpdateConfirmButtonState();

        // 创建倒计时定时器
        _timer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _timer.Tick += OnTimerTick;
        _timer.Start();
    }

    /// <summary>
    /// 定时器回调 — 每秒更新倒计时
    /// </summary>
    private void OnTimerTick(object? sender, EventArgs e)
    {
        _cooldownSeconds--;
        UpdateButtonText();

        if (_cooldownSeconds <= 0)
        {
            _timer.Stop();
            _cooledDown = true;
            UpdateConfirmButtonState();
        }
    }

    /// <summary>
    /// 更新确认按钮文字，显示剩余冷却秒数
    /// </summary>
    private void UpdateButtonText()
    {
        if (_cooldownSeconds > 0)
        {
            ConfirmButton.Content = $"确认（{_cooldownSeconds}s）";
        }
        else
        {
            ConfirmButton.Content = "确认";
        }
    }

    /// <summary>
    /// 输入框文字变化时检查确认短语是否匹配
    /// </summary>
    private void OnInputTextChanged(object sender, RoutedEventArgs e)
    {
        // 检查输入是否匹配
        if (ConfirmInput.Text == _confirmPhrase)
        {
            ErrorText.Visibility = Visibility.Collapsed;
        }
        else if (!string.IsNullOrEmpty(ConfirmInput.Text) && ConfirmInput.Text.Length >= _confirmPhrase.Length)
        {
            // 输入了足够长度但不匹配，显示错误提示
            ErrorText.Text = "输入不匹配，请检查后重新输入";
            ErrorText.Visibility = Visibility.Visible;
        }
        else
        {
            ErrorText.Visibility = Visibility.Collapsed;
        }

        UpdateConfirmButtonState();
    }

    /// <summary>
    /// 根据冷却状态和输入验证结果更新确认按钮是否可用
    /// 必须同时满足：冷却完成 + 输入正确短语
    /// </summary>
    private void UpdateConfirmButtonState()
    {
        ConfirmButton.IsEnabled = _cooledDown && ConfirmInput.Text == _confirmPhrase;
    }

    /// <summary>
    /// 用户点击确认按钮 — 关闭弹窗并返回 true
    /// </summary>
    private void OnConfirmClick(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }

    /// <summary>
    /// 用户点击取消按钮 — 关闭弹窗并返回 false
    /// </summary>
    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
