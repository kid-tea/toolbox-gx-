using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Toolbox.Helpers;
using Toolbox.Native;
using Toolbox.Services;

namespace Toolbox.ViewModels;

/// <summary>
/// 窗口置顶 ViewModel
/// 管理置顶窗口列表、选择窗口模式、一键取消全部
/// 使用 SetWindowPos(HWND_TOPMOST) 实现置顶
/// 每 10 秒轮询检测外部修改
/// </summary>
public partial class AlwaysOnTopViewModel : ViewModelBase
{
    private readonly ILogService _log;
    private DispatcherTimer? _pollTimer;
    private IntPtr _toolboxHwnd;

    // ==================== 置顶窗口列表 ====================

    /// <summary>已置顶的窗口列表</summary>
    [ObservableProperty]
    private ObservableCollection<TopmostWindowItem> _topmostWindows = new();

    /// <summary>置顶窗口数量</summary>
    [ObservableProperty]
    private int _topmostCount;

    /// <summary>是否超过 50 个窗口的警告</summary>
    [ObservableProperty]
    private bool _isTooManyWindows;

    /// <summary>是否有已置顶的窗口</summary>
    [ObservableProperty]
    private bool _hasTopmostWindows;

    // ==================== 窗口选择 ====================

    /// <summary>当前前台窗口信息</summary>
    [ObservableProperty]
    private string _foregroundWindowInfo = "点击「置顶前台窗口」获取当前前台窗口";

    /// <summary>当前前台窗口标题</summary>
    [ObservableProperty]
    private string _foregroundWindowTitle = "";

    /// <summary>当前前台窗口句柄（十六进制）</summary>
    [ObservableProperty]
    private string _foregroundWindowHandle = "";

    /// <summary>
    /// 置顶窗口项
    /// </summary>
    public partial class TopmostWindowItem : ObservableObject
    {
        /// <summary>窗口句柄</summary>
        public IntPtr Handle { get; set; }

        /// <summary>窗口句柄显示（前 8 位十六进制）</summary>
        [ObservableProperty]
        private string _handleDisplay = "";

        /// <summary>窗口标题</summary>
        [ObservableProperty]
        private string _title = "";

        /// <summary>显示文本（含句柄前缀）</summary>
        [ObservableProperty]
        private string _displayText = "";

        /// <summary>进程名称</summary>
        [ObservableProperty]
        private string _processName = "";

        /// <summary>置顶时间</summary>
        [ObservableProperty]
        private string _topmostTime = "";

        /// <summary>
        /// 更新显示文本（标题+句柄前缀）
        /// </summary>
        public void UpdateDisplayText()
        {
            DisplayText = $"[{HandleDisplay}] {Title}";
        }
    }

    /// <summary>
    /// 构造函数 — 依赖注入
    /// </summary>
    public AlwaysOnTopViewModel(ILogService log)
    {
        _log = log;
        StatusMessage = "点击「置顶前台窗口」将当前窗口置顶，或使用快捷键 Ctrl+Shift+T";
    }

    /// <summary>
    /// 当 View 加载完成后调用，初始化工具箱窗口句柄和轮询定时器
    /// </summary>
    public void Initialize(IntPtr toolboxHwnd)
    {
        _toolboxHwnd = toolboxHwnd;

        if (_pollTimer != null)
            return;

        _pollTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(10)
        };
        _pollTimer.Tick += OnPollTimerTick;
        _pollTimer.Start();

        _log.LogInfo("窗口置顶轮询已启动（10秒间隔）");
    }

    /// <summary>
    /// 定时器回调 — 检测置顶窗口的外部修改
    /// </summary>
    private void OnPollTimerTick(object? sender, EventArgs e)
    {
        CheckExternalChanges();
    }

    // ==================== 置顶操作 ====================

    /// <summary>
    /// 获取当前前台窗口并置顶
    /// 排除工具箱自身窗口
    /// </summary>
    [RelayCommand]
    private void TopmostForegroundWindow()
    {
        try
        {
            IntPtr hWnd = NativeMethods.GetForegroundWindow();

            if (hWnd == IntPtr.Zero)
            {
                StatusMessage = "无法获取前台窗口";
                return;
            }

            // 排除工具箱自身
            if (hWnd == _toolboxHwnd)
            {
                StatusMessage = "不能置顶工具箱自身，请切换到其他窗口";
                return;
            }

            // 检测窗口有效性
            if (!NativeMethods.IsWindow(hWnd))
            {
                StatusMessage = "窗口句柄无效";
                return;
            }

            // 检测最大化窗口，弹出 L1 确认弹窗
            if (NativeMethods.IsZoomed(hWnd))
            {
                _log.LogInfo("检测到最大化窗口，弹出确认弹窗");
                bool continueOp = ConfirmationHelper.RequestL1(
                    "此窗口当前处于最大化状态，置顶后任务栏可能被遮挡。是否继续？");
                if (!continueOp)
                {
                    StatusMessage = "用户取消了最大化窗口的置顶操作";
                    return;
                }
            }

            // 获取窗口标题
            int titleLen = NativeMethods.GetWindowTextLength(hWnd);
            var sb = new System.Text.StringBuilder(titleLen + 1);
            NativeMethods.GetWindowText(hWnd, sb, sb.Capacity);
            string title = sb.ToString();

            // 获取进程信息
            NativeMethods.GetWindowThreadProcessId(hWnd, out uint pid);
            string processName = "";
            try
            {
                var process = Process.GetProcessById((int)pid);
                processName = process.ProcessName;
            }
            catch { processName = "未知进程"; }

            // 检测 UWP 应用（ApplicationFrameHost 是 UWP 的宿主）不支持
            if (processName.Equals("ApplicationFrameHost", StringComparison.OrdinalIgnoreCase))
            {
                StatusMessage = "UWP 应用不支持窗口置顶，请选择普通桌面应用";
                _log.LogWarning($"尝试置顶 UWP 应用窗口: {title}");
                return;
            }

            // 检查是否已在列表中
            if (TopmostWindows.Any(w => w.Handle == hWnd))
            {
                StatusMessage = $"窗口已在置顶列表中: {title}";
                ForegroundWindowTitle = title;
                ForegroundWindowHandle = hWnd.ToString("X8");
                ForegroundWindowInfo = $"[{hWnd:X8}] {title} - 已在列表中";
                return;
            }

            // 置顶窗口
            bool success = NativeMethods.SetWindowPos(hWnd, NativeMethods.HWND_TOPMOST,
                0, 0, 0, 0, NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_SHOWWINDOW);

            if (!success)
            {
                // 尝试提权操作
                var result = MessageBox.Show(
                    "置顶操作可能需要管理员权限。\n是否以管理员身份重新启动工具箱？",
                    "权限不足",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning);

                if (result == MessageBoxResult.Yes)
                {
                    Helpers.ConfirmationHelper.RequestAdmin();
                }
                StatusMessage = "置顶失败：权限不足";
                return;
            }

            // 添加到列表
            var item = new TopmostWindowItem
            {
                Handle = hWnd,
                HandleDisplay = hWnd.ToString("X8"),
                Title = title,
                ProcessName = processName,
                TopmostTime = DateTime.Now.ToString("HH:mm:ss")
            };
            item.UpdateDisplayText();
            TopmostWindows.Add(item);
            TopmostCount = TopmostWindows.Count;

            // 数量超过 50 时警告
            if (TopmostCount > 50)
            {
                IsTooManyWindows = true;
                StatusMessage = $"已置顶 {TopmostCount} 个窗口。置顶过多窗口可能影响系统性能";
            }
            else
            {
                IsTooManyWindows = false;
                StatusMessage = $"窗口已置顶: {title}";
            }

            ForegroundWindowTitle = title;
            ForegroundWindowHandle = hWnd.ToString("X8");
            ForegroundWindowInfo = $"[{hWnd:X8}] {title} ({processName})";

            _log.LogInfo($"窗口置顶: [{hWnd:X8}] {title} ({processName})");
        }
        catch (Exception ex)
        {
            StatusMessage = $"置顶操作失败: {ex.Message}";
            _log.LogError("置顶前台窗口失败", ex);
        }
    }

    /// <summary>
    /// 取消指定窗口的置顶
    /// </summary>
    /// <param name="item">要取消置顶的窗口项</param>
    [RelayCommand]
    private void RemoveTopmost(TopmostWindowItem? item)
    {
        if (item == null) return;

        try
        {
            // 检查窗口是否仍然有效
            if (!NativeMethods.IsWindow(item.Handle))
            {
                TopmostWindows.Remove(item);
                TopmostCount = TopmostWindows.Count;
                StatusMessage = $"窗口 [{item.HandleDisplay}] {item.Title} 已销毁，已从列表中移除";
                _log.LogInfo($"置顶窗口已销毁，自动移除: [{item.HandleDisplay}] {item.Title}");
                return;
            }

            // 取消置顶
            NativeMethods.SetWindowPos(item.Handle, NativeMethods.HWND_NOTOPMOST,
                0, 0, 0, 0, NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE);

            TopmostWindows.Remove(item);
            TopmostCount = TopmostWindows.Count;
            IsTooManyWindows = TopmostCount > 50;
            StatusMessage = $"已取消置顶: {item.Title}";
            _log.LogInfo($"取消窗口置顶: [{item.HandleDisplay}] {item.Title}");
        }
        catch (Exception ex)
        {
            StatusMessage = $"取消置顶失败: {ex.Message}";
            _log.LogError("取消置顶失败", ex);
        }
    }

    /// <summary>
    /// 一键取消全部窗口置顶
    /// </summary>
    [RelayCommand]
    private void RemoveAllTopmost()
    {
        if (TopmostWindows.Count == 0) return;

        int successCount = 0;
        int failCount = 0;
        var toRemove = new List<TopmostWindowItem>();

        foreach (var item in TopmostWindows)
        {
            try
            {
                if (NativeMethods.IsWindow(item.Handle))
                {
                    NativeMethods.SetWindowPos(item.Handle, NativeMethods.HWND_NOTOPMOST,
                        0, 0, 0, 0, NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE);
                }
                toRemove.Add(item);
                successCount++;
            }
            catch
            {
                failCount++;
            }
        }

        foreach (var item in toRemove)
        {
            TopmostWindows.Remove(item);
        }

        TopmostCount = 0;
        IsTooManyWindows = false;
        StatusMessage = $"已取消全部置顶（成功: {successCount}, 失败: {failCount}）";
        _log.LogInfo($"一键取消全部置顶: 成功{successCount}, 失败{failCount}");
    }

    /// <summary>
    /// 刷新前台窗口信息显示
    /// </summary>
    [RelayCommand]
    private void RefreshForegroundWindow()
    {
        try
        {
            IntPtr hWnd = NativeMethods.GetForegroundWindow();
            if (hWnd == IntPtr.Zero || hWnd == _toolboxHwnd)
            {
                ForegroundWindowInfo = "未检测到其他活动窗口";
                ForegroundWindowTitle = "";
                ForegroundWindowHandle = "";
                return;
            }

            int titleLen = NativeMethods.GetWindowTextLength(hWnd);
            var sb = new System.Text.StringBuilder(titleLen + 1);
            NativeMethods.GetWindowText(hWnd, sb, sb.Capacity);
            string title = sb.ToString();
            string handle = hWnd.ToString("X8");

            NativeMethods.GetWindowThreadProcessId(hWnd, out uint pid);
            string processName = "";
            try { processName = Process.GetProcessById((int)pid).ProcessName; }
            catch { processName = "未知"; }

            ForegroundWindowTitle = title;
            ForegroundWindowHandle = handle;
            ForegroundWindowInfo = $"[{handle}] {title} ({processName})";
        }
        catch (Exception ex)
        {
            ForegroundWindowInfo = $"获取失败: {ex.Message}";
        }
    }

    // ==================== 外部修改检测 ====================

    /// <summary>
    /// 检测置顶窗口是否被外部修改
    /// 窗口已销毁则自动移除并提示
    /// </summary>
    private void CheckExternalChanges()
    {
        var destroyed = new List<TopmostWindowItem>();

        foreach (var item in TopmostWindows)
        {
            if (!NativeMethods.IsWindow(item.Handle))
            {
                destroyed.Add(item);
                _log.LogWarning($"置顶窗口已销毁: [{item.HandleDisplay}] {item.Title}");
            }
        }

        if (destroyed.Count > 0)
        {
            foreach (var item in destroyed)
            {
                TopmostWindows.Remove(item);
            }
            TopmostCount = TopmostWindows.Count;
            StatusMessage = $"{destroyed.Count} 个置顶窗口已销毁，已自动从列表中移除";
        }
    }

    /// <summary>
    /// 置顶窗口数量变化时更新 HasTopmostWindows
    /// </summary>
    partial void OnTopmostCountChanged(int value)
    {
        HasTopmostWindows = value > 0;
    }

    /// <summary>
    /// 清理资源，停止定时器
    /// </summary>
    public void Cleanup()
    {
        _pollTimer?.Stop();
        _pollTimer = null;
    }
}
