using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Toolbox.Native;

namespace Toolbox.Services;

/// <summary>
/// 截屏服务 — 基于 GDI BitBlt 的多显示器截图引擎
/// 支持全屏/区域/窗口/延迟截图，PerMonitorV2 DPI 感知
/// 使用 CreateDC + BitBlt + GetPixel 逐显示器采样
/// </summary>
public class ScreenshotService
{
    private readonly ILogService _log;

    /// <summary>
    /// 显示器信息记录
    /// </summary>
    public record MonitorInfo(
        IntPtr Handle,
        NativeMethods.RECT Bounds,
        uint DpiX,
        uint DpiY,
        string DeviceName,
        bool IsPrimary);

    public ScreenshotService(ILogService log)
    {
        _log = log;
    }

    /// <summary>
    /// 枚举所有显示器并获取其信息（DPI、边界等）
    /// </summary>
    /// <returns>所有显示器的信息列表</returns>
    public List<MonitorInfo> EnumerateMonitors()
    {
        var monitors = new List<MonitorInfo>();

        NativeMethods.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr hMonitor, IntPtr hdcMonitor, ref NativeMethods.RECT rcMonitor, IntPtr dwData) =>
        {
            var mi = new NativeMethods.MONITORINFOEX
            {
                cbSize = (uint)Marshal.SizeOf<NativeMethods.MONITORINFOEX>()
            };

            if (NativeMethods.GetMonitorInfoW(hMonitor, ref mi))
            {
                uint dpiX = 96, dpiY = 96; // 默认 DPI
                NativeMethods.GetDpiForMonitor(hMonitor, NativeMethods.DpiType.Effective, out dpiX, out dpiY);

                monitors.Add(new MonitorInfo(
                    hMonitor,
                    mi.rcMonitor,
                    dpiX,
                    dpiY,
                    mi.szDevice,
                    (mi.dwFlags & NativeMethods.MONITORINFOF_PRIMARY) != 0
                ));
            }
            return true; // 继续枚举
        }, IntPtr.Zero);

        _log.LogInfo($"枚举到 {monitors.Count} 个显示器");
        return monitors;
    }

    /// <summary>
    /// 全屏截图 — 捕获所有显示器的完整画面
    /// 为降低兼容性问题，直接按虚拟桌面坐标一次性抓取整个桌面区域
    /// </summary>
    /// <returns>合成后的全屏截图 BitmapSource</returns>
    public BitmapSource CaptureAllMonitors()
    {
        var monitors = EnumerateMonitors();
        if (monitors.Count == 0)
            throw new InvalidOperationException("未检测到任何显示器");

        int minX = monitors.Min(m => m.Bounds.Left);
        int minY = monitors.Min(m => m.Bounds.Top);
        int maxX = monitors.Max(m => m.Bounds.Right);
        int maxY = monitors.Max(m => m.Bounds.Bottom);
        int totalWidth = maxX - minX;
        int totalHeight = maxY - minY;

        _log.LogInfo($"开始全屏截图: 虚拟桌面 {minX},{minY} {totalWidth}x{totalHeight}");
        return CaptureScreenRectangle(minX, minY, totalWidth, totalHeight);
    }

    /// <summary>
    /// 捕获单个显示器的画面
    /// 直接按显示器在虚拟桌面中的矩形区域抓取，避免设备名 DC 兼容问题
    /// </summary>
    /// <param name="monitor">显示器信息</param>
    /// <returns>该显示器的截图</returns>
    public BitmapSource CaptureMonitor(MonitorInfo monitor)
    {
        int width = monitor.Bounds.Width;
        int height = monitor.Bounds.Height;

        _log.LogInfo($"开始截取显示器: {monitor.DeviceName}, 区域 {monitor.Bounds.Left},{monitor.Bounds.Top} {width}x{height}");
        return CaptureScreenRectangle(monitor.Bounds.Left, monitor.Bounds.Top, width, height);
    }

    /// <summary>
    /// 区域截图 — 捕获屏幕指定矩形区域
    /// </summary>
    /// <param name="region">区域坐标（虚拟屏幕坐标）</param>
    /// <returns>区域截图</returns>
    public BitmapSource CaptureRegion(NativeMethods.RECT region)
    {
        return CaptureScreenRectangle(region.Left, region.Top, region.Width, region.Height);
    }

    /// <summary>
    /// 窗口截图 — 捕获指定窗口的客户区画面
    /// </summary>
    /// <param name="hWnd">窗口句柄</param>
    /// <param name="windowFriendlyName">窗口名（用于日志）</param>
    /// <returns>窗口截图</returns>
    public BitmapSource CaptureWindow(IntPtr hWnd, string? windowFriendlyName = null)
    {
        if (hWnd == IntPtr.Zero || !NativeMethods.IsWindow(hWnd))
            throw new ArgumentException("无效的窗口句柄");

        // 如果窗口最小化，尝试恢复
        if (NativeMethods.IsIconic(hWnd))
        {
            NativeMethods.ShowWindow(hWnd, NativeMethods.SW_RESTORE);
            Thread.Sleep(200); // 等待窗口恢复
            _log.LogInfo($"尝试恢复最小化窗口: {windowFriendlyName ?? hWnd.ToString("X8")}");
        }

        NativeMethods.GetWindowRect(hWnd, out NativeMethods.RECT rect);
        var name = windowFriendlyName ?? hWnd.ToString("X8");
        _log.LogInfo($"捕获窗口: {name}, 区域: {rect.Left},{rect.Top} - {rect.Right},{rect.Bottom}");

        return CaptureRegion(rect);
    }

    /// <summary>
    /// 按虚拟桌面坐标抓取一个屏幕矩形区域
    /// 使用屏幕 DC + BitBlt，兼容全屏、单显示器和区域截图
    /// </summary>
    private BitmapSource CaptureScreenRectangle(int left, int top, int width, int height)
    {
        if (width <= 0 || height <= 0)
            throw new ArgumentException("截图区域无效");

        IntPtr hdcScreen = IntPtr.Zero;
        IntPtr hdcMem = IntPtr.Zero;
        IntPtr hBitmap = IntPtr.Zero;
        IntPtr hOldBitmap = IntPtr.Zero;

        try
        {
            hdcScreen = NativeMethods.CreateDC("DISPLAY", null, null, IntPtr.Zero);
            if (hdcScreen == IntPtr.Zero)
                throw new InvalidOperationException("无法创建屏幕 DC");

            hdcMem = NativeMethods.CreateCompatibleDC(hdcScreen);
            if (hdcMem == IntPtr.Zero)
                throw new InvalidOperationException("无法创建内存 DC");

            hBitmap = NativeMethods.CreateCompatibleBitmap(hdcScreen, width, height);
            if (hBitmap == IntPtr.Zero)
                throw new InvalidOperationException("无法创建位图缓冲区");

            hOldBitmap = NativeMethods.SelectObject(hdcMem, hBitmap);
            if (hOldBitmap == IntPtr.Zero)
                throw new InvalidOperationException("无法选择位图到内存 DC");

            bool bitBltOk = NativeMethods.BitBlt(
                hdcMem, 0, 0, width, height,
                hdcScreen, left, top, NativeMethods.SRCCOPY);

            if (!bitBltOk)
                throw new InvalidOperationException("BitBlt 失败");

            var bmpInfo = new NativeMethods.BITMAPINFO();
            bmpInfo.bmiHeader.biSize = (uint)Marshal.SizeOf<NativeMethods.BITMAPINFOHEADER>();
            bmpInfo.bmiHeader.biWidth = width;
            bmpInfo.bmiHeader.biHeight = -height;
            bmpInfo.bmiHeader.biPlanes = 1;
            bmpInfo.bmiHeader.biBitCount = 32;
            bmpInfo.bmiHeader.biCompression = NativeMethods.BI_RGB;

            int stride = ((width * 32 + 31) / 32) * 4;
            byte[] pixelData = new byte[stride * height];
            int dibResult = NativeMethods.GetDIBits(
                hdcMem, hBitmap, 0, (uint)height, pixelData, ref bmpInfo, NativeMethods.DIB_RGB_COLORS);

            if (dibResult == 0)
                throw new InvalidOperationException("GetDIBits 失败");

            var bmp = new WriteableBitmap(width, height, 96, 96, PixelFormats.Bgra32, null);
            bmp.WritePixels(new Int32Rect(0, 0, width, height), pixelData, stride, 0);
            bmp.Freeze();
            return bmp;
        }
        finally
        {
            if (hdcMem != IntPtr.Zero && hOldBitmap != IntPtr.Zero)
                NativeMethods.SelectObject(hdcMem, hOldBitmap);
            if (hBitmap != IntPtr.Zero)
                NativeMethods.DeleteObject(hBitmap);
            if (hdcMem != IntPtr.Zero)
                NativeMethods.DeleteDC(hdcMem);
            if (hdcScreen != IntPtr.Zero)
                NativeMethods.DeleteDC(hdcScreen);
        }
    }

    /// <summary>
    /// 延迟截图 — 等待指定秒数后执行截图
    /// </summary>
    /// <param name="delaySeconds">延迟秒数</param>
    /// <param name="mode">截图模式：FullScreen/Region/Window</param>
    /// <param name="captor">截图执行委托</param>
    /// <param name="progress">进度报告</param>
    /// <param name="ct">取消令牌</param>
    /// <returns>截图结果</returns>
    public async Task<BitmapSource> CaptureDelayedAsync(int delaySeconds, string mode,
        Func<Task<BitmapSource>> captor, IProgress<int>? progress = null, CancellationToken ct = default)
    {
        _log.LogInfo($"延迟截图: {delaySeconds}秒, 模式: {mode}");

        for (int i = delaySeconds; i > 0; i--)
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report(i);
            await Task.Delay(1000, ct);
        }

        progress?.Report(0);
        return await captor();
    }

    /// <summary>
    /// 保存截图到文件
    /// 优先保存到"图片"文件夹，路径不可用时回退到桌面
    /// </summary>
    /// <param name="image">截图 BitmapSource</param>
    /// <param name="format">保存格式：png 或 jpg</param>
    /// <param name="quality">JPEG 质量（1-100），仅对 JPG 有效</param>
    /// <returns>保存的文件路径</returns>
    public string SaveToFile(BitmapSource image, string format = "png", int quality = 90)
    {
        // 默认文件名：截图_2026-07-02_14-30-00.png
        string timestamp = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");
        string ext = format.ToLowerInvariant() == "jpg" || format.ToLowerInvariant() == "jpeg" ? ".jpg" : ".png";

        // 保存路径优先级：图片文件夹 > 桌面 > 我的文档
        string saveDir = GetPicturesFolder() ?? Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        string fileName = $"截图_{timestamp}{ext}";
        string filePath = Path.Combine(saveDir, fileName);

        // 如果文件已存在，添加序号避免覆盖
        int counter = 1;
        while (File.Exists(filePath))
        {
            fileName = $"截图_{timestamp}_{counter}{ext}";
            filePath = Path.Combine(saveDir, fileName);
            counter++;
        }

        // 确保目录存在
        Directory.CreateDirectory(saveDir);

        BitmapEncoder encoder;
        if (ext == ".jpg")
        {
            encoder = new JpegBitmapEncoder { QualityLevel = quality };
        }
        else
        {
            encoder = new PngBitmapEncoder();
        }

        encoder.Frames.Add(BitmapFrame.Create(image));

        using (var stream = new FileStream(filePath, FileMode.Create))
        {
            encoder.Save(stream);
        }

        _log.LogInfo($"截图已保存: {filePath}");
        return filePath;
    }

    /// <summary>
    /// 复制截图到剪贴板，失败时自动重试（最多 3 次，间隔 200ms）
    /// </summary>
    /// <param name="image">截图 BitmapSource</param>
    /// <returns>复制成功返回 true</returns>
    public bool CopyToClipboard(BitmapSource image)
    {
        const int maxRetries = 3;
        const int retryDelayMs = 200;

        for (int attempt = 1; attempt <= maxRetries; attempt++)
        {
            try
            {
                Clipboard.SetImage(image);
                _log.LogInfo(attempt == 1
                    ? "截图已复制到剪贴板"
                    : $"截图已复制到剪贴板（第 {attempt} 次重试成功）");
                return true;
            }
            catch (Exception ex)
            {
                _log.LogWarning($"剪贴板复制失败（第 {attempt}/{maxRetries} 次）: {ex.Message}");
                if (attempt < maxRetries)
                    Thread.Sleep(retryDelayMs);
            }
        }

        _log.LogError("剪贴板复制失败，已达最大重试次数");
        return false;
    }

    /// <summary>
    /// 获取图片文件夹路径，不存在时返回桌面
    /// </summary>
    /// <returns>有效的图片文件夹路径，不可用时返回 null</returns>
    private static string? GetPicturesFolder()
    {
        try
        {
            string path = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
            if (!string.IsNullOrEmpty(path) && Directory.Exists(path))
                return path;

            // 回退：尝试常见路径
            string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            string[] fallbacks = {
                Path.Combine(profile, "Pictures"),
                Path.Combine(profile, "图片"),
                Path.Combine(profile, "Pictures", "Screenshots"),
                Path.Combine(profile, "图片", "屏幕截图")
            };

            foreach (var fallback in fallbacks)
            {
                if (Directory.Exists(fallback))
                    return fallback;
            }
        }
        catch { /* 获取文件夹失败，返回 null 使用桌面 */ }

        return null;
    }

    /// <summary>
    /// 缩放 BitmapSource 到指定尺寸
    /// </summary>
    /// <param name="source">源位图</param>
    /// <param name="width">目标宽度</param>
    /// <param name="height">目标高度</param>
    /// <returns>缩放后的位图</returns>
    private static BitmapSource ScaleBitmap(BitmapSource source, int width, int height)
    {
        var visual = new DrawingVisual();
        using (var ctx = visual.RenderOpen())
        {
            ctx.DrawImage(source, new Rect(0, 0, width, height));
        }

        var render = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        render.Render(visual);
        render.Freeze();
        return render;
    }

    /// <summary>
    /// 将一个位图区域拷贝到目标 WriteableBitmap 的指定位置
    /// </summary>
    private static void CopyBitmapRegion(WriteableBitmap target, BitmapSource source,
        int destX, int destY, int width, int height)
    {
        // 计算目标区域中的像素数据
        int srcStride = ((source.PixelWidth * source.Format.BitsPerPixel + 7) / 8);
        byte[] srcPixels = new byte[srcStride * source.PixelHeight];
        source.CopyPixels(srcPixels, srcStride, 0);

        // 直接写入到 WriteableBitmap 的目标区域
        int targetStride = ((target.PixelWidth * target.Format.BitsPerPixel + 7) / 8);

        try
        {
            target.Lock();
            IntPtr backBuffer = target.BackBuffer;

            for (int y = 0; y < Math.Min(height, source.PixelHeight); y++)
            {
                int targetOffset = (destY + y) * targetStride + destX * 4;
                int srcOffset = y * srcStride;

                if (targetOffset >= 0 && targetOffset + Math.Min(width, source.PixelWidth) * 4 <= targetStride * target.PixelHeight)
                {
                    int copyBytes = Math.Min(width, source.PixelWidth) * 4;
                    Marshal.Copy(srcPixels, srcOffset, backBuffer + targetOffset, copyBytes);
                }
            }

            target.AddDirtyRect(new Int32Rect(destX, destY, width, height));
        }
        finally
        {
            target.Unlock();
        }
    }

    /// <summary>
    /// 获取系统可用物理内存（MB）
    /// </summary>
    private static long GetAvailableMemoryMB()
    {
        var memStatus = new NativeMethods.MEMORYSTATUSEX();
        memStatus.dwLength = (uint)Marshal.SizeOf<NativeMethods.MEMORYSTATUSEX>();
        if (NativeMethods.GlobalMemoryStatusEx(ref memStatus))
            return (long)(memStatus.ullAvailPhys / 1024 / 1024);
        return 1024; // 无法获取时假设充足
    }

    /// <summary>
    /// 获取指定像素的颜色（用于取色器）
    /// </summary>
    /// <param name="x">屏幕 X 坐标</param>
    /// <param name="y">屏幕 Y 坐标</param>
    /// <returns>像素颜色值</returns>
    public Color GetPixelColor(int x, int y)
    {
        IntPtr hdc = NativeMethods.CreateDC("DISPLAY", null, null, IntPtr.Zero);
        if (hdc == IntPtr.Zero)
            throw new InvalidOperationException("无法创建屏幕 DC");

        try
        {
            uint pixel = NativeMethods.GetPixel(hdc, x, y);
            // Windows GDI 返回的是 GDI 格式 BGR，需要转换为 RGB
            byte r = (byte)(pixel & 0xFF);
            byte g = (byte)((pixel >> 8) & 0xFF);
            byte b = (byte)((pixel >> 16) & 0xFF);
            return Color.FromRgb(r, g, b);
        }
        finally
        {
            NativeMethods.DeleteDC(hdc);
        }
    }
}
