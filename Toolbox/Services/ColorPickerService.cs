using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Input;
using System.Runtime.InteropServices;
using Toolbox.Native;

namespace Toolbox.Services;

/// <summary>
/// 取色器服务
/// 负责屏幕像素采样、显示器色彩配置检测、放大镜效果生成
/// 使用 GDI GetPixel 进行逐像素颜色采样
/// </summary>
public class ColorPickerService
{
    private readonly ILogService _log;

    /// <summary>
    /// 取色器服务构造函数
    /// </summary>
    public ColorPickerService(ILogService log)
    {
        _log = log;
    }

    /// <summary>
    /// 对屏幕指定坐标进行像素采样，获取颜色
    /// 支持多显示器不同色彩配置
    /// </summary>
    /// <param name="screenX">屏幕 X 坐标</param>
    /// <param name="screenY">屏幕 Y 坐标</param>
    /// <returns>采样的颜色值</returns>
    public Color SamplePixel(int screenX, int screenY)
    {
        IntPtr hdc = IntPtr.Zero;
        try
        {
            // 获取当前坐标所在显示器的 DC
            var monitor = GetMonitorAtPoint(screenX, screenY);
            string deviceName = monitor?.DeviceName ?? "DISPLAY";

            hdc = NativeMethods.CreateDC("DISPLAY", deviceName, null, IntPtr.Zero);
            if (hdc == IntPtr.Zero)
                hdc = NativeMethods.CreateDC("DISPLAY", null, null, IntPtr.Zero);
            if (hdc == IntPtr.Zero)
                throw new InvalidOperationException("无法创建屏幕设备上下文");

            uint pixel = NativeMethods.GetPixel(hdc, screenX, screenY);

            // GDI GetPixel 返回的颜色格式为 0x00BBGGRR，需要正确解析
            byte r = (byte)(pixel & 0xFF);
            byte g = (byte)((pixel >> 8) & 0xFF);
            byte b = (byte)((pixel >> 16) & 0xFF);

            return Color.FromRgb(r, g, b);
        }
        finally
        {
            if (hdc != IntPtr.Zero)
                NativeMethods.DeleteDC(hdc);
        }
    }

    /// <summary>
    /// 生成光标周围区域的放大镜效果图像
    /// 捕获鼠标周围小范围区域并放大显示
    /// </summary>
    /// <param name="centerX">中心屏幕 X</param>
    /// <param name="centerY">中心屏幕 Y</param>
    /// <param name="zoomLevel">放大倍数（默认 4x）</param>
    /// <param name="captureRadius">捕获半径（像素，默认 20）</param>
    /// <returns>放大后的位图</returns>
    public BitmapSource? GenerateMagnifier(int centerX, int centerY, int zoomLevel = 4, int captureRadius = 20)
    {
        try
        {
            int captureSize = captureRadius * 2 + 1;
            int magnifiedSize = captureSize * zoomLevel;

            IntPtr hdcScreen = IntPtr.Zero;
            IntPtr hdcMem = IntPtr.Zero;
            IntPtr hBitmap = IntPtr.Zero;
            IntPtr hOldBitmap = IntPtr.Zero;
            IntPtr hdcScaled = IntPtr.Zero;
            IntPtr hBitmapScaled = IntPtr.Zero;
            IntPtr hOldBitmapScaled = IntPtr.Zero;

            try
            {
                hdcScreen = NativeMethods.CreateDC("DISPLAY", null, null, IntPtr.Zero);
                if (hdcScreen == IntPtr.Zero) return null;

                // 创建原始大小位图
                hdcMem = NativeMethods.CreateCompatibleDC(hdcScreen);
                hBitmap = NativeMethods.CreateCompatibleBitmap(hdcScreen, captureSize, captureSize);
                hOldBitmap = NativeMethods.SelectObject(hdcMem, hBitmap);

                int srcX = centerX - captureRadius;
                int srcY = centerY - captureRadius;

                NativeMethods.BitBlt(hdcMem, 0, 0, captureSize, captureSize,
                    hdcScreen, srcX, srcY, NativeMethods.SRCCOPY);

                // 放大到位图
                hdcScaled = NativeMethods.CreateCompatibleDC(hdcScreen);
                hBitmapScaled = NativeMethods.CreateCompatibleBitmap(hdcScreen, magnifiedSize, magnifiedSize);
                hOldBitmapScaled = NativeMethods.SelectObject(hdcScaled, hBitmapScaled);

                NativeMethods.StretchBlt(hdcScaled, 0, 0, magnifiedSize, magnifiedSize,
                    hdcMem, 0, 0, captureSize, captureSize, NativeMethods.SRCCOPY);

                // 获取放大后的像素数据
                var bmpInfo = new NativeMethods.BITMAPINFO();
                bmpInfo.bmiHeader.biSize = (uint)Marshal.SizeOf<NativeMethods.BITMAPINFOHEADER>();
                bmpInfo.bmiHeader.biWidth = magnifiedSize;
                bmpInfo.bmiHeader.biHeight = -magnifiedSize;
                bmpInfo.bmiHeader.biPlanes = 1;
                bmpInfo.bmiHeader.biBitCount = 32;
                bmpInfo.bmiHeader.biCompression = NativeMethods.BI_RGB;

                int stride = ((magnifiedSize * 32 + 31) / 32) * 4;
                byte[] pixelData = new byte[stride * magnifiedSize];
                NativeMethods.GetDIBits(hdcScaled, hBitmapScaled, 0, (uint)magnifiedSize,
                    pixelData, ref bmpInfo, NativeMethods.DIB_RGB_COLORS);

                var bmp = new WriteableBitmap(magnifiedSize, magnifiedSize, 96, 96, PixelFormats.Bgra32, null);
                bmp.WritePixels(new Int32Rect(0, 0, magnifiedSize, magnifiedSize), pixelData, stride, 0);
                bmp.Freeze();
                return bmp;
            }
            finally
            {
                if (hdcScreen != IntPtr.Zero) NativeMethods.DeleteDC(hdcScreen);
                if (hdcMem != IntPtr.Zero)
                {
                    if (hOldBitmap != IntPtr.Zero) NativeMethods.SelectObject(hdcMem, hOldBitmap);
                    NativeMethods.DeleteDC(hdcMem);
                }
                if (hBitmap != IntPtr.Zero) NativeMethods.DeleteObject(hBitmap);
                if (hdcScaled != IntPtr.Zero)
                {
                    if (hOldBitmapScaled != IntPtr.Zero) NativeMethods.SelectObject(hdcScaled, hOldBitmapScaled);
                    NativeMethods.DeleteDC(hdcScaled);
                }
                if (hBitmapScaled != IntPtr.Zero) NativeMethods.DeleteObject(hBitmapScaled);
            }
        }
        catch (Exception ex)
        {
            _log.LogError("生成放大镜效果失败", ex);
            return null;
        }
    }

    /// <summary>
    /// 获取指定坐标所在的显示器信息
    /// </summary>
    private static MonitorInfo? GetMonitorAtPoint(int x, int y)
    {
        MonitorInfo? result = null;

        NativeMethods.MonitorEnumDelegate callback = (IntPtr hMonitor, IntPtr hdcMonitor, ref NativeMethods.RECT rcMonitor, IntPtr dwData) =>
        {
            if (x >= rcMonitor.Left && x < rcMonitor.Right &&
                y >= rcMonitor.Top && y < rcMonitor.Bottom)
            {
                var mi = new NativeMethods.MONITORINFOEX
                {
                    cbSize = (uint)Marshal.SizeOf<NativeMethods.MONITORINFOEX>()
                };
                if (NativeMethods.GetMonitorInfoW(hMonitor, ref mi))
                {
                    uint dpiX = 96, dpiY = 96;
                    NativeMethods.GetDpiForMonitor(hMonitor, NativeMethods.DpiType.Effective, out dpiX, out dpiY);
                    result = new MonitorInfo(hMonitor, mi.rcMonitor, dpiX, dpiY, mi.szDevice,
                        (mi.dwFlags & NativeMethods.MONITORINFOF_PRIMARY) != 0);
                    return false; // 找到目标显示器，停止枚举
                }
            }
            return true;
        };
        NativeMethods.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, callback, IntPtr.Zero);

        return result;
    }

    /// <summary>
    /// 开始实时采样（跟踪鼠标位置，持续采样颜色）
    /// </summary>
    /// <param name="onColorSampled">颜色变化回调，返回 false 停止采样</param>
    /// <param name="onError">错误回调</param>
    /// <param name="ct">取消令牌</param>
    public async Task StartSamplingAsync(Func<Color, int, int, Task<bool>> onColorSampled,
        Action<Exception>? onError = null, CancellationToken ct = default)
    {
        _log.LogInfo("开始实时颜色采样");

        try
        {
            // 获取 Win32 光标位置（屏幕坐标）
            while (!ct.IsCancellationRequested)
            {
                if (!NativeMethods.GetCursorPos(out var pt))
                {
                    await Task.Delay(50, ct);
                    continue;
                }

                try
                {
                    var color = SamplePixel(pt.X, pt.Y);
                    bool shouldContinue = await onColorSampled(color, pt.X, pt.Y);
                    if (!shouldContinue) break;
                }
                catch (Exception ex)
                {
                    onError?.Invoke(ex);
                }

                await Task.Delay(30, ct); // ~33fps 采样率
            }
        }
        catch (OperationCanceledException)
        {
            _log.LogInfo("颜色采样已取消");
        }
    }

    /// <summary>
    /// 显示器信息记录（与 ScreenshotService 中相同）
    /// </summary>
    public record MonitorInfo(
        IntPtr Handle,
        NativeMethods.RECT Bounds,
        uint DpiX,
        uint DpiY,
        string DeviceName,
        bool IsPrimary);
}
