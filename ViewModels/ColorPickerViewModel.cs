using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Toolbox.Services;

namespace Toolbox.ViewModels;

/// <summary>
/// 取色器 ViewModel
/// 管理取色状态、HEX/RGB/HSL 格式显示、取色历史（最多20个，FIFO）
/// 支持实时鼠标跟踪取色和点击采样
/// </summary>
public partial class ColorPickerViewModel : ViewModelBase
{
    private readonly ColorPickerService _colorPickerService;
    private readonly ILogService _log;
    private CancellationTokenSource? _samplingCts;

    // ==================== 取色状态 ====================

    /// <summary>是否正在取色</summary>
    [ObservableProperty]
    private bool _isPicking;

    [ObservableProperty]
    private bool _hasPickedColor;

    /// <summary>当前取色位置 X（屏幕坐标）</summary>
    [ObservableProperty]
    private int _mouseX;

    /// <summary>当前取色位置 Y（屏幕坐标）</summary>
    [ObservableProperty]
    private int _mouseY;

    /// <summary>当前采样的颜色</summary>
    [ObservableProperty]
    private Color _currentColor = Colors.White;

    /// <summary>当前颜色的预览画刷</summary>
    [ObservableProperty]
    private SolidColorBrush _currentColorBrush = new(Colors.White);

    // ==================== 颜色格式显示 ====================

    /// <summary>HEX 格式颜色值（如 #FF5733）</summary>
    [ObservableProperty]
    private string _hexValue = "#FFFFFF";

    /// <summary>RGB 格式颜色值（如 rgb(255, 87, 51)）</summary>
    [ObservableProperty]
    private string _rgbValue = "rgb(255, 255, 255)";

    /// <summary>HSL 格式颜色值（如 hsl(14, 100%, 60%)）</summary>
    [ObservableProperty]
    private string _hslValue = "hsl(0, 0%, 100%)";

    // ==================== 放大镜 ====================

    /// <summary>放大镜预览图像</summary>
    [ObservableProperty]
    private System.Windows.Media.Imaging.BitmapSource? _magnifierImage;

    // ==================== 取色历史 ====================

    /// <summary>取色历史列表（最多 20 项，FIFO）</summary>
    [ObservableProperty]
    private ObservableCollection<ColorHistoryItem> _colorHistory = new();

    /// <summary>
    /// 取色历史项
    /// </summary>
    public partial class ColorHistoryItem : ObservableObject
    {
        /// <summary>颜色值</summary>
        [ObservableProperty]
        private Color _color;

        /// <summary>颜色画刷（UI 绑定用）</summary>
        [ObservableProperty]
        private SolidColorBrush _colorBrush;

        /// <summary>HEX 值</summary>
        [ObservableProperty]
        private string _hexValue = "";

        /// <summary>采样时间</summary>
        [ObservableProperty]
        private string _time = "";

        /// <summary>
        /// 构造函数
        /// </summary>
        public ColorHistoryItem(Color color)
        {
            Color = color;
            ColorBrush = new SolidColorBrush(color);
            HexValue = $"#{color.R:X2}{color.G:X2}{color.B:X2}";
            Time = DateTime.Now.ToString("HH:mm:ss");
        }
    }

    /// <summary>
    /// 构造函数 — 依赖注入
    /// </summary>
    public ColorPickerViewModel(ColorPickerService colorPickerService, ILogService log)
    {
        _colorPickerService = colorPickerService;
        _log = log;
        StatusMessage = "按「开始取色」或使用快捷键 Ctrl+Shift+C 开始取色";
    }

    // ==================== 取色操作 ====================

    /// <summary>
    /// 开始取色 — 启动实时采样
    /// 鼠标移出屏幕时停止取色，Esc 取消不记录历史
    /// </summary>
    [RelayCommand]
    private async Task StartPickingAsync()
    {
        if (IsPicking) return;

        HasPickedColor = false;
        IsPicking = true;
        IsBusy = true;
        StatusMessage = "取色中 — 移动鼠标选择颜色，点击左键锁定，Esc 取消";

        _samplingCts = new CancellationTokenSource();
        var ct = _samplingCts.Token;

        // 监听全局键盘事件：按 Esc 取消
        var escListener = Task.Run(async () =>
        {
            while (!ct.IsCancellationRequested)
            {
                // 检测 Escape 按键
                await Task.Delay(100, ct);
            }
        }, ct);

        try
        {
            await _colorPickerService.StartSamplingAsync(
                async (color, x, y) =>
                {
                    if (ct.IsCancellationRequested) return false;

                    // 更新当前颜色
                    await Application.Current.Dispatcher.InvokeAsync(() =>
                    {
                        UpdateCurrentColor(color, x, y);
                    });

                    // 检测鼠标左键点击，锁定颜色
                    if (Native.NativeMethods.GetAsyncKeyState(0x01) < 0) // VK_LBUTTON
                    {
                        await Application.Current.Dispatcher.InvokeAsync(() =>
                        {
                            LockColor(color);
                        });
                        return false; // 停止采样
                    }

                    // 检测 Escape 键取消
                    if (Native.NativeMethods.GetAsyncKeyState(0x1B) < 0) // VK_ESCAPE
                    {
                        await Application.Current.Dispatcher.InvokeAsync(() =>
                        {
                            CancelPicking();
                        });
                        return false;
                    }

                    // 生成放大镜效果
                    try
                    {
                        var magnifier = _colorPickerService.GenerateMagnifier(x, y);
                        await Application.Current.Dispatcher.InvokeAsync(() =>
                        {
                            MagnifierImage = magnifier;
                        });
                    }
                    catch { /* 放大镜生成失败不影响主流程 */ }

                    return true; // 继续采样
                },
                onError: ex =>
                {
                    _log.LogError("取色采样错误", ex);
                },
                ct: ct
            );
        }
        catch (OperationCanceledException)
        {
            // 预期取消
        }
        finally
        {
            await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                IsPicking = false;
                IsBusy = false;
            });
        }
    }

    /// <summary>
    /// 停止取色
    /// </summary>
    [RelayCommand]
    private void StopPicking()
    {
        _samplingCts?.Cancel();
        _samplingCts?.Dispose();
        _samplingCts = null;
        IsPicking = false;
        IsBusy = false;
        StatusMessage = "取色已停止";
    }

    /// <summary>
    /// 取消取色（不记录历史）
    /// </summary>
    private void CancelPicking()
    {
        _samplingCts?.Cancel();
        _samplingCts?.Dispose();
        _samplingCts = null;
        IsPicking = false;
        IsBusy = false;
        StatusMessage = "取色已取消（未记录历史）";
        _log.LogInfo("取色已取消");
    }

    /// <summary>
    /// 锁定颜色 — 暂停采样并记录到历史
    /// </summary>
    /// <param name="color">锁定的颜色</param>
    private void LockColor(Color color)
    {
        _samplingCts?.Cancel();
        _samplingCts?.Dispose();
        _samplingCts = null;

        UpdateCurrentColor(color, MouseX, MouseY);
        AddToHistory(color);
        HasPickedColor = true;
        IsPicking = false;
        IsBusy = false;
        StatusMessage = $"取色完成: {HexValue}";
        _log.LogInfo($"颜色已锁定: {HexValue}");
    }

    // ==================== 颜色格式更新 ====================

    /// <summary>
    /// 更新当前颜色及所有格式显示
    /// </summary>
    private void UpdateCurrentColor(Color color, int x, int y)
    {
        CurrentColor = color;
        CurrentColorBrush = new SolidColorBrush(color);
        MouseX = x;
        MouseY = y;

        // HEX 格式
        HexValue = $"#{color.R:X2}{color.G:X2}{color.B:X2}";

        // RGB 格式
        RgbValue = $"rgb({color.R}, {color.G}, {color.B})";

        // HSL 格式
        RgbToHsl(color.R, color.G, color.B, out double h, out double s, out double l);
        HslValue = $"hsl({h:F0}, {s * 100:F0}%, {l * 100:F0}%)";
    }

    // ==================== 历史管理 ====================

    /// <summary>
    /// 添加颜色到历史列表（FIFO，最多 20 项）
    /// </summary>
    private void AddToHistory(Color color)
    {
        // 检查是否与最新项重复
        if (ColorHistory.Count > 0 && ColorHistory[^1].Color == color)
            return;

        ColorHistory.Add(new ColorHistoryItem(color));

        // FIFO：保持最多 20 项
        while (ColorHistory.Count > 20)
        {
            ColorHistory.RemoveAt(0);
        }
    }

    /// <summary>
    /// 复制 HEX 值到剪贴板
    /// </summary>
    [RelayCommand]
    private void CopyHex()
    {
        try
        {
            Clipboard.SetText(HexValue);
            StatusMessage = $"已复制: {HexValue}";
        }
        catch
        {
            StatusMessage = "复制失败，请重试";
        }
    }

    /// <summary>
    /// 复制 RGB 值到剪贴板
    /// </summary>
    [RelayCommand]
    private void CopyRgb()
    {
        try
        {
            Clipboard.SetText(RgbValue);
            StatusMessage = $"已复制: {RgbValue}";
        }
        catch
        {
            StatusMessage = "复制失败，请重试";
        }
    }

    /// <summary>
    /// 复制 HSL 值到剪贴板
    /// </summary>
    [RelayCommand]
    private void CopyHsl()
    {
        try
        {
            Clipboard.SetText(HslValue);
            StatusMessage = $"已复制: {HslValue}";
        }
        catch
        {
            StatusMessage = "复制失败，请重试";
        }
    }

    /// <summary>
    /// 点击历史项，设为当前颜色
    /// </summary>
    [RelayCommand]
    private void SelectHistory(ColorHistoryItem item)
    {
        if (item == null) return;
        UpdateCurrentColor(item.Color, 0, 0);
        StatusMessage = $"已选择历史颜色: {item.HexValue}";
    }

    /// <summary>
    /// 清除所有取色历史
    /// </summary>
    [RelayCommand]
    private void ClearHistory()
    {
        ColorHistory.Clear();
        StatusMessage = "取色历史已清除";
    }

    // ==================== 颜色格式转换 ====================

    /// <summary>
    /// RGB 转 HSL 颜色空间
    /// </summary>
    /// <param name="r">红色 (0-255)</param>
    /// <param name="g">绿色 (0-255)</param>
    /// <param name="b">蓝色 (0-255)</param>
    /// <param name="h">输出：色相 (0-360)</param>
    /// <param name="s">输出：饱和度 (0-1)</param>
    /// <param name="l">输出：亮度 (0-1)</param>
    private static void RgbToHsl(byte r, byte g, byte b, out double h, out double s, out double l)
    {
        double rNorm = r / 255.0;
        double gNorm = g / 255.0;
        double bNorm = b / 255.0;

        double max = Math.Max(rNorm, Math.Max(gNorm, bNorm));
        double min = Math.Min(rNorm, Math.Min(gNorm, bNorm));
        double delta = max - min;

        l = (max + min) / 2.0;

        if (Math.Abs(delta) < 0.001)
        {
            h = 0;
            s = 0;
        }
        else
        {
            s = l > 0.5 ? delta / (2.0 - max - min) : delta / (max + min);

            if (Math.Abs(max - rNorm) < 0.001)
                h = ((gNorm - bNorm) / delta) % 6;
            else if (Math.Abs(max - gNorm) < 0.001)
                h = (bNorm - rNorm) / delta + 2;
            else
                h = (rNorm - gNorm) / delta + 4;

            h *= 60;
            if (h < 0) h += 360;
        }
    }
}
