using System.Windows;
using System.Windows.Threading;

namespace Toolbox.Views.Dialogs;

/// <summary>
/// L2 级别确认弹窗 — 带 5 秒冷却倒计时
/// 用户必须等待冷却时间结束才能点击确认按钮
/// 冷却期间确认按钮显示倒计时，按钮不可点击
/// </summary>
public partial class L2ConfirmDialog : Window
{
    private readonly DispatcherTimer _timer;
    private int _cooldownSeconds = 5; // 冷却秒数

    /// <summary>
    /// 构造函数
    /// </summary>
    /// <param name="message">显示给用户的警告消息</param>
    public L2ConfirmDialog(string message)
    {
        InitializeComponent();

        // 设置消息内容
        MessageText.Text = message;

        // 设置所有者窗口
        Owner = Application.Current.MainWindow;

        // 初始化倒计时
        UpdateButtonText();

        // 创建定时器，每秒更新倒计时
        _timer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _timer.Tick += OnTimerTick;
        _timer.Start();
    }

    /// <summary>
    /// 定时器回调 — 每秒更新倒计时显示
    /// </summary>
    private void OnTimerTick(object? sender, EventArgs e)
    {
        _cooldownSeconds--;
        UpdateButtonText();

        // 冷却结束，启用确认按钮
        if (_cooldownSeconds <= 0)
        {
            _timer.Stop();
            ConfirmButton.IsEnabled = true;
        }
    }

    /// <summary>
    /// 更新确认按钮上的文字，显示剩余等待秒数
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
