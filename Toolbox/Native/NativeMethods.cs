using System.Runtime.InteropServices;
using System.Text;

namespace Toolbox.Native;

/// <summary>
/// Windows 原生 API P/Invoke 声明
/// 封装所有工具箱所需的 Win32 API 调用
/// </summary>
public static class NativeMethods
{
    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool ChangeWindowMessageFilter(uint message, uint dwFlag);

    // ==================== 全局快捷键 ====================

    /// <summary>注册全局快捷键</summary>
    /// <param name="hWnd">接收快捷键消息的窗口句柄</param>
    /// <param name="id">快捷键标识符</param>
    /// <param name="fsModifiers">修饰键（MOD_ALT=0x0001, MOD_CONTROL=0x0002, MOD_SHIFT=0x0004, MOD_WIN=0x0008）</param>
    /// <param name="vk">虚拟键码</param>
    [DllImport("user32.dll")]
    public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    /// <summary>卸载全局快捷键</summary>
    /// <param name="hWnd">窗口句柄</param>
    /// <param name="id">快捷键标识符</param>
    [DllImport("user32.dll")]
    public static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    // ==================== 窗口操作 ====================

    /// <summary>设置窗口位置和 Z 序</summary>
    [DllImport("user32.dll")]
    public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    /// <summary>置顶窗口时使用的 HWND 值</summary>
    public static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);

    /// <summary>取消置顶窗口时使用的 HWND 值</summary>
    public static readonly IntPtr HWND_NOTOPMOST = new IntPtr(-2);

    /// <summary>SetWindowPos 标志：不移动窗口</summary>
    public const uint SWP_NOMOVE = 0x0002;

    /// <summary>SetWindowPos 标志：不改变窗口大小</summary>
    public const uint SWP_NOSIZE = 0x0001;

    /// <summary>SetWindowPos 标志：显示窗口</summary>
    public const uint SWP_SHOWWINDOW = 0x0040;

    /// <summary>获取前台窗口句柄</summary>
    [DllImport("user32.dll")]
    public static extern IntPtr GetForegroundWindow();

    /// <summary>将指定窗口设为前台窗口</summary>
    [DllImport("user32.dll")]
    public static extern bool SetForegroundWindow(IntPtr hWnd);

    /// <summary>判断窗口是否最小化</summary>
    [DllImport("user32.dll")]
    public static extern bool IsIconic(IntPtr hWnd);

    /// <summary>显示窗口</summary>
    /// <param name="hWnd">窗口句柄</param>
    /// <param name="nCmdShow">显示方式（SW_RESTORE=9 等）</param>
    [DllImport("user32.dll")]
    public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    // ==================== 用户输入状态检测 ====================

    /// <summary>获取最后一次用户输入的时间（用于空闲检测）</summary>
    [DllImport("user32.dll")]
    public static extern bool GetLastInputInfo(ref LASTINPUTINFO plii);

    /// <summary>LASTINPUTINFO 结构体</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct LASTINPUTINFO
    {
        /// <summary>结构体大小，使用前需设置 cbSize = Marshal.SizeOf(typeof(LASTINPUTINFO))</summary>
        public uint cbSize;

        /// <summary>距离系统启动的滴答计数</summary>
        public uint dwTime;
    }

    // ==================== 内存信息 ====================

    /// <summary>获取全局内存状态信息</summary>
    [DllImport("kernel32.dll")]
    public static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

    /// <summary>MEMORYSTATUSEX 结构体 — 系统内存状态</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct MEMORYSTATUSEX
    {
        public uint dwLength;
        /// <summary>内存使用率百分比 (0-100)</summary>
        public uint dwMemoryLoad;
        /// <summary>物理内存总量（字节）</summary>
        public ulong ullTotalPhys;
        /// <summary>可用物理内存（字节）</summary>
        public ulong ullAvailPhys;
        /// <summary>页面文件总量（字节）</summary>
        public ulong ullTotalPageFile;
        /// <summary>可用页面文件（字节）</summary>
        public ulong ullAvailPageFile;
        /// <summary>虚拟内存总量（字节）</summary>
        public ulong ullTotalVirtual;
        /// <summary>可用虚拟内存（字节）</summary>
        public ulong ullAvailVirtual;
        /// <summary>可用扩展虚拟内存（字节）</summary>
        public ulong ullAvailExtendedVirtual;
    }

    // ==================== 进程内存操作 ====================

    /// <summary>清空指定进程的工作集（释放物理内存到页面文件）</summary>
    [DllImport("psapi.dll")]
    public static extern bool EmptyWorkingSet(IntPtr hProcess);

    /// <summary>获取指定进程的内存使用信息</summary>
    [DllImport("psapi.dll")]
    public static extern bool GetProcessMemoryInfo(IntPtr hProcess, out PROCESS_MEMORY_COUNTERS counters, uint cb);

    /// <summary>PROCESS_MEMORY_COUNTERS 结构体</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct PROCESS_MEMORY_COUNTERS
    {
        public uint cb;
        public uint PageFaultCount;
        /// <summary>峰值工作集大小</summary>
        public UIntPtr PeakWorkingSetSize;
        /// <summary>当前工作集大小</summary>
        public UIntPtr WorkingSetSize;
        public UIntPtr QuotaPeakPagedPoolUsage;
        public UIntPtr QuotaPagedPoolUsage;
        public UIntPtr QuotaPeakNonPagedPoolUsage;
        public UIntPtr QuotaNonPagedPoolUsage;
        /// <summary>页面文件使用量</summary>
        public UIntPtr PagefileUsage;
        /// <summary>峰值页面文件使用量</summary>
        public UIntPtr PeakPagefileUsage;
    }

    // ==================== 磁盘信息 ====================

    /// <summary>获取卷信息</summary>
    [DllImport("kernel32.dll")]
    public static extern bool GetVolumeInformationW(
        string rootPathName,
        System.Text.StringBuilder volumeName,
        int volumeNameSize,
        out uint volumeSerialNumber,
        out uint maxComponentLength,
        out uint fileSystemFlags,
        System.Text.StringBuilder fileSystemName,
        int fileSystemNameSize);

    /// <summary>获取驱动器类型</summary>
    /// <returns>DRIVE_FIXED=3 表示本地固定磁盘</returns>
    [DllImport("kernel32.dll")]
    public static extern uint GetDriveTypeW(string lpRootPathName);

    /// <summary>本地固定磁盘驱动器类型值</summary>
    public const int DRIVE_FIXED = 3;

    // ==================== 屏幕信息 ====================

    /// <summary>获取系统度量信息（如屏幕尺寸）</summary>
    [DllImport("user32.dll")]
    public static extern int GetSystemMetrics(int nIndex);

    /// <summary>屏幕宽度索引</summary>
    public const int SM_CXSCREEN = 0;

    /// <summary>屏幕高度索引</summary>
    public const int SM_CYSCREEN = 1;

    // ==================== Restart Manager API — 文件占用枚举 ====================

    /// <summary>启动 Restart Manager 会话</summary>
    /// <param name="pSessionHandle">返回会话句柄</param>
    /// <param name="dwSessionFlags">会话标志，通常为0</param>
    /// <param name="strSessionKey">会话密钥，可为null</param>
    /// <returns>ERROR_SUCCESS(0)表示成功</returns>
    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
    public static extern uint RmStartSession(out uint pSessionHandle, int dwSessionFlags, StringBuilder? strSessionKey);

    /// <summary>向 RM 会话注册要检查的资源（文件）</summary>
    /// <param name="dwSessionHandle">会话句柄</param>
    /// <param name="nFiles">文件数量</param>
    /// <param name="rgsFileNames">文件路径数组</param>
    /// <param name="nApplications">应用程序数量，通常为0</param>
    /// <param name="rgApplications">应用程序数组，可为null</param>
    /// <param name="nServices">服务数量，通常为0</param>
    /// <param name="rgsServiceNames">服务名数组，可为null</param>
    /// <returns>ERROR_SUCCESS(0)表示成功</returns>
    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
    public static extern uint RmRegisterResources(
        uint dwSessionHandle, uint nFiles, string[] rgsFileNames,
        uint nApplications, RM_UNIQUE_PROCESS[]? rgApplications,
        uint nServices, string[]? rgsServiceNames);

    /// <summary>获取占用注册资源的进程列表</summary>
    /// <param name="dwSessionHandle">会话句柄</param>
    /// <param name="pnProcInfoNeeded">输出：需要的进程信息条目数</param>
    /// <param name="pnProcInfo">输入输出：rgAffectedApps数组大小，输出实际条目数</param>
    /// <param name="rgAffectedApps">进程信息数组</param>
    /// <param name="lpdwRebootReasons">重启原因标志</param>
    /// <returns>ERROR_SUCCESS或ERROR_MORE_DATA(234)</returns>
    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
    public static extern uint RmGetList(
        uint dwSessionHandle, out uint pnProcInfoNeeded,
        ref uint pnProcInfo, [In, Out] RM_PROCESS_INFO[]? rgAffectedApps,
        ref uint lpdwRebootReasons);

    /// <summary>结束 Restart Manager 会话</summary>
    /// <param name="dwSessionHandle">会话句柄</param>
    [DllImport("rstrtmgr.dll")]
    public static extern uint RmEndSession(uint dwSessionHandle);

    /// <summary>RM_UNIQUE_PROCESS 结构体 — 唯一标识一个进程（PID + 启动时间）</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct RM_UNIQUE_PROCESS
    {
        /// <summary>进程 ID</summary>
        public uint dwProcessId;
        /// <summary>进程启动时间的 FILETIME 结构</summary>
        public long ProcessStartTime;
    }

    /// <summary>RM_PROCESS_INFO 结构体 — 占用文件的进程详细信息</summary>
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct RM_PROCESS_INFO
    {
        /// <summary>唯一进程标识</summary>
        public RM_UNIQUE_PROCESS Process;
        /// <summary>应用程序名称（最多 CCH_RM_MAX_APP_NAME+1=256 个字符）</summary>
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string strAppName;
        /// <summary>服务短名称（最多 CCH_RM_MAX_SVC_NAME+1=64 个字符）</summary>
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string strServiceShortName;
        /// <summary>应用程序类型：RmUnknownApp=0, RmMainWindow=1, RmOtherWindow=2, RmService=3, RmExplorer=4, RmConsole=5, RmCritical=1000</summary>
        public uint ApplicationType;
        /// <summary>应用状态</summary>
        public uint AppStatus;
        /// <summary>终端服务会话 ID</summary>
        public uint TSSessionId;
        /// <summary>是否可以重启</summary>
        public bool bRestartable;
    }

    // RM API 错误码
    /// <summary>操作成功</summary>
    public const uint RM_ERROR_SUCCESS = 0;
    /// <summary>需要更大的缓冲区</summary>
    public const uint RM_ERROR_MORE_DATA = 234;

    // ==================== 硬链接检测 ====================

    /// <summary>通过文件句柄获取文件信息（用于检测硬链接数量）</summary>
    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool GetFileInformationByHandle(
        IntPtr hFile, out BY_HANDLE_FILE_INFORMATION lpFileInformation);

    /// <summary>BY_HANDLE_FILE_INFORMATION 结构体 — 包含硬链接数等文件信息</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct BY_HANDLE_FILE_INFORMATION
    {
        public uint dwFileAttributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME ftCreationTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME ftLastAccessTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME ftLastWriteTime;
        public uint dwVolumeSerialNumber;
        public uint nFileSizeHigh;
        public uint nFileSizeLow;
        /// <summary>硬链接数量（NTFS上>1表示有多个硬链接指向同一数据）</summary>
        public uint nNumberOfLinks;
        public uint nFileIndexHigh;
        public uint nFileIndexLow;
    }

    // ==================== DeviceIoControl — SSD/Trim 检测 ====================

    /// <summary>向设备发送 IO 控制命令（用于检测 SSD Trim 支持）</summary>
    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool DeviceIoControl(
        IntPtr hDevice, uint dwIoControlCode,
        IntPtr lpInBuffer, uint nInBufferSize,
        IntPtr lpOutBuffer, uint nOutBufferSize,
        out uint lpBytesReturned, IntPtr lpOverlapped);

    /// <summary>IOCTL_STORAGE_QUERY_PROPERTY — 查询存储设备属性</summary>
    public const uint IOCTL_STORAGE_QUERY_PROPERTY = 0x002D1400;

    /// <summary>STORAGE_PROPERTY_QUERY 结构体</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct STORAGE_PROPERTY_QUERY
    {
        public uint PropertyId;
        public uint QueryType;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 1)]
        public byte[] AdditionalParameters;
    }

    /// <summary>STORAGE_DEVICE_DESCRIPTOR 结构体 — 包含设备类型和总线类型</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct STORAGE_DEVICE_DESCRIPTOR
    {
        public uint Version;
        public uint Size;
        public byte DeviceType;
        public byte DeviceTypeModifier;
        public byte RemovableMedia;
        public byte CommandQueueing;
        public uint VendorIdOffset;
        public uint ProductIdOffset;
        public uint ProductRevisionOffset;
        public uint SerialNumberOffset;
        public uint BusType;
        public uint RawPropertiesLength;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 1)]
        public byte[] RawDeviceProperties;
    }

    /// <summary>StorageDeviceSeekPenaltyProperty — 查询设备是否有寻道延迟（HDD有，SSD无）</summary>
    public const uint StorageDeviceSeekPenaltyProperty = 7;
    /// <summary>PropertyStandardQuery</summary>
    public const uint PropertyStandardQuery = 0;

    /// <summary>DEVICE_SEEK_PENALTY_DESCRIPTOR — 寻道延迟描述符</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct DEVICE_SEEK_PENALTY_DESCRIPTOR
    {
        public uint Version;
        public uint Size;
        /// <summary>TRUE表示有寻道延迟（HDD），FALSE表示无（SSD）</summary>
        public byte IncursSeekPenalty;
    }

    /// <summary>创建文件句柄（用于 DeviceIoControl）</summary>
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern IntPtr CreateFileW(
        string lpFileName, uint dwDesiredAccess, uint dwShareMode,
        IntPtr lpSecurityAttributes, uint dwCreationDisposition,
        uint dwFlagsAndAttributes, IntPtr hTemplateFile);

    /// <summary>关闭文件句柄</summary>
    [DllImport("kernel32.dll")]
    public static extern bool CloseHandle(IntPtr hObject);

    /// <summary>文件读取数据访问权限</summary>
    public const uint FILE_READ_DATA = 0x0001;
    /// <summary>文件读取属性访问权限</summary>
    public const uint FILE_READ_ATTRIBUTES = 0x0080;
    /// <summary>打开已存在的文件</summary>
    public const uint OPEN_EXISTING = 3;
    /// <summary>文件属性：不缓冲（直接IO）</summary>
    public const uint FILE_FLAG_NO_BUFFERING = 0x20000000;

    /// <summary>获取磁盘空间信息（用于覆写前检测可用空间）</summary>
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern bool GetDiskFreeSpaceExW(
        string lpDirectoryName, out ulong lpFreeBytesAvailable,
        out ulong lpTotalNumberOfBytes, out ulong lpTotalNumberOfFreeBytes);

    // ==================== 多显示器枚举 ====================

    /// <summary>枚举所有显示器</summary>
    [DllImport("user32.dll")]
    public static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr lprcClip, MonitorEnumDelegate lpfnEnum, IntPtr dwData);

    /// <summary>显示器枚举回调委托</summary>
    /// <param name="hMonitor">显示器句柄</param>
    /// <param name="hdcMonitor">显示器 DC</param>
    /// <param name="lprcMonitor">显示器矩形（虚拟屏幕坐标）</param>
    /// <param name="dwData">用户自定义数据</param>
    /// <returns>继续枚举返回 true，停止返回 false</returns>
    public delegate bool MonitorEnumDelegate(IntPtr hMonitor, IntPtr hdcMonitor, ref RECT lprcMonitor, IntPtr dwData);

    /// <summary>获取显示器信息</summary>
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern bool GetMonitorInfoW(IntPtr hMonitor, ref MONITORINFOEX lpmi);

    /// <summary>DPI 类型枚举</summary>
    public enum DpiType
    {
        /// <summary>有效 DPI（推荐使用）</summary>
        Effective = 0,
        /// <summary>角度 DPI</summary>
        Angular = 1,
        /// <summary>原始 DPI</summary>
        Raw = 2
    }

    /// <summary>获取显示器 DPI（Win8.1+）</summary>
    /// <param name="hmonitor">显示器句柄</param>
    /// <param name="dpiType">DPI 类型</param>
    /// <param name="dpiX">输出：水平 DPI</param>
    /// <param name="dpiY">输出：垂直 DPI</param>
    /// <returns>S_OK=0 表示成功</returns>
    [DllImport("shcore.dll")]
    public static extern int GetDpiForMonitor(IntPtr hmonitor, DpiType dpiType, out uint dpiX, out uint dpiY);

    /// <summary>矩形结构体</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        /// <summary>左边界</summary>
        public int Left;
        /// <summary>上边界</summary>
        public int Top;
        /// <summary>右边界</summary>
        public int Right;
        /// <summary>下边界</summary>
        public int Bottom;

        /// <summary>宽度</summary>
        public readonly int Width => Right - Left;

        /// <summary>高度</summary>
        public readonly int Height => Bottom - Top;

        /// <summary>转换为 System.Windows.Rect</summary>
        public readonly System.Windows.Rect ToRect() => new(Left, Top, Width, Height);
    }

    /// <summary>显示器信息扩展结构体</summary>
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct MONITORINFOEX
    {
        /// <summary>结构体大小，使用前必须设置</summary>
        public uint cbSize;
        /// <summary>显示器矩形（虚拟屏幕坐标）</summary>
        public RECT rcMonitor;
        /// <summary>工作区矩形（不含任务栏）</summary>
        public RECT rcWork;
        /// <summary>标志位</summary>
        public uint dwFlags;
        /// <summary>显示器设备名称，如 \\.\DISPLAY1</summary>
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string szDevice;
    }

    /// <summary>显示器为主显示器的标志</summary>
    public const uint MONITORINFOF_PRIMARY = 0x00000001;

    // ==================== GDI 设备上下文和位图 ====================

    /// <summary>创建设备上下文（DC）</summary>
    /// <param name="lpszDriver">驱动名，屏幕用 "DISPLAY"</param>
    /// <param name="lpszDevice">设备名，如 \\.\DISPLAY1</param>
    /// <param name="lpszOutput">输出名，屏幕为 null</param>
    /// <param name="lpInitData">初始化数据，通常为 IntPtr.Zero</param>
    /// <returns>DC 句柄，失败返回 IntPtr.Zero</returns>
    [DllImport("gdi32.dll")]
    public static extern IntPtr CreateDC(string lpszDriver, string? lpszDevice, string? lpszOutput, IntPtr lpInitData);

    /// <summary>创建兼容 DC（内存 DC）</summary>
    [DllImport("gdi32.dll")]
    public static extern IntPtr CreateCompatibleDC(IntPtr hdc);

    /// <summary>删除 DC</summary>
    [DllImport("gdi32.dll")]
    public static extern bool DeleteDC(IntPtr hdc);

    /// <summary>创建兼容位图</summary>
    [DllImport("gdi32.dll")]
    public static extern IntPtr CreateCompatibleBitmap(IntPtr hdc, int cx, int cy);

    /// <summary>选择 GDI 对象到 DC</summary>
    [DllImport("gdi32.dll")]
    public static extern IntPtr SelectObject(IntPtr hdc, IntPtr h);

    /// <summary>删除 GDI 对象</summary>
    [DllImport("gdi32.dll")]
    public static extern bool DeleteObject(IntPtr ho);

    /// <summary>位块传输（拷贝像素）</summary>
    /// <param name="hdcDest">目标 DC</param>
    /// <param name="nXDest">目标左上角 X</param>
    /// <param name="nYDest">目标左上角 Y</param>
    /// <param name="nWidth">宽度</param>
    /// <param name="nHeight">高度</param>
    /// <param name="hdcSrc">源 DC</param>
    /// <param name="nXSrc">源左上角 X</param>
    /// <param name="nYSrc">源左上角 Y</param>
    /// <param name="dwRop">光栅操作码</param>
    [DllImport("gdi32.dll")]
    public static extern bool BitBlt(IntPtr hdcDest, int nXDest, int nYDest, int nWidth, int nHeight, IntPtr hdcSrc, int nXSrc, int nYSrc, uint dwRop);

    /// <summary>BitBlt 光栅操作码：直接拷贝</summary>
    public const uint SRCCOPY = 0x00CC0020;

    /// <summary>获取指定像素的颜色值</summary>
    [DllImport("gdi32.dll")]
    public static extern uint GetPixel(IntPtr hdc, int x, int y);

    /// <summary>获取位图数据到缓冲区</summary>
    [DllImport("gdi32.dll")]
    public static extern int GetDIBits(IntPtr hdc, IntPtr hbmp, uint uStartScan, uint cScanLines, byte[]? lpvBits, ref BITMAPINFO lpbi, uint uUsage);

    /// <summary>缩放位块传输</summary>
    [DllImport("gdi32.dll")]
    public static extern bool StretchBlt(IntPtr hdcDest, int nXOriginDest, int nYOriginDest, int nWidthDest, int nHeightDest, IntPtr hdcSrc, int nXOriginSrc, int nYOriginSrc, int nWidthSrc, int nHeightSrc, uint dwRop);

    /// <summary>位图信息头结构体</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct BITMAPINFOHEADER
    {
        /// <summary>结构体大小</summary>
        public uint biSize;
        /// <summary>位图宽度</summary>
        public int biWidth;
        /// <summary>位图高度（正值=bottom-up）</summary>
        public int biHeight;
        /// <summary>色彩平面数，必须为 1</summary>
        public ushort biPlanes;
        /// <summary>每像素位数</summary>
        public ushort biBitCount;
        /// <summary>压缩方式</summary>
        public uint biCompression;
        /// <summary>图像大小（字节），BI_RGB 时可为 0</summary>
        public uint biSizeImage;
        /// <summary>水平分辨率（像素/米）</summary>
        public int biXPelsPerMeter;
        /// <summary>垂直分辨率（像素/米）</summary>
        public int biYPelsPerMeter;
        /// <summary>使用的颜色数</summary>
        public uint biClrUsed;
        /// <summary>重要颜色数</summary>
        public uint biClrImportant;
    }

    /// <summary>位图信息结构体</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct BITMAPINFO
    {
        /// <summary>位图信息头</summary>
        public BITMAPINFOHEADER bmiHeader;
    }

    /// <summary>BI_RGB — 无压缩</summary>
    public const uint BI_RGB = 0;

    /// <summary>DIB_RGB_COLORS — 颜色表为 RGB 值</summary>
    public const uint DIB_RGB_COLORS = 0;

    // ==================== 窗口相关扩展 ====================

    /// <summary>获取窗口 DC（包含非客户区的整个窗口）</summary>
    [DllImport("user32.dll")]
    public static extern IntPtr GetWindowDC(IntPtr hWnd);

    /// <summary>释放 DC</summary>
    [DllImport("user32.dll")]
    public static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

    /// <summary>获取窗口矩形（屏幕坐标）</summary>
    [DllImport("user32.dll")]
    public static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    /// <summary>获取窗口标题文本</summary>
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetWindowText(IntPtr hWnd, System.Text.StringBuilder lpString, int nMaxCount);

    /// <summary>获取窗口标题文本长度</summary>
    [DllImport("user32.dll")]
    public static extern int GetWindowTextLength(IntPtr hWnd);

    /// <summary>枚举顶级窗口</summary>
    [DllImport("user32.dll")]
    public static extern bool EnumWindows(EnumWindowsDelegate lpEnumFunc, IntPtr lParam);

    /// <summary>枚举窗口回调委托</summary>
    public delegate bool EnumWindowsDelegate(IntPtr hWnd, IntPtr lParam);

    /// <summary>判断窗口是否可见</summary>
    [DllImport("user32.dll")]
    public static extern bool IsWindowVisible(IntPtr hWnd);

    /// <summary>判断窗口句柄是否仍有效</summary>
    [DllImport("user32.dll")]
    public static extern bool IsWindow(IntPtr hWnd);

    /// <summary>获取窗口所属进程 ID</summary>
    [DllImport("user32.dll")]
    public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    /// <summary>判断窗口是否最大化</summary>
    [DllImport("user32.dll")]
    public static extern bool IsZoomed(IntPtr hWnd);

    /// <summary>SW_RESTORE — 恢复窗口</summary>
    public const int SW_RESTORE = 9;

    // ==================== 受保护窗口检测 ====================

    /// <summary>设置窗口显示亲和性（防止截图/录屏）</summary>
    [DllImport("user32.dll")]
    public static extern bool SetWindowDisplayAffinity(IntPtr hWnd, uint dwAffinity);

    /// <summary>WDA_NONE — 无保护</summary>
    public const uint WDA_NONE = 0x00000000;

    /// <summary>WDA_EXCLUDEFROMCAPTURE — 排除窗口内容不被截图捕获（Win10 2004+）</summary>
    public const uint WDA_EXCLUDEFROMCAPTURE = 0x00000011;

    /// <summary>WDA_MONITOR — 窗口内容显示为黑色（Win7+）</summary>
    public const uint WDA_MONITOR = 0x00000001;

    // ==================== 未文档化 API（内存释放用） ====================

    /// <summary>系统信息类型枚举</summary>
    public enum SYSTEM_INFORMATION_CLASS
    {
        /// <summary>系统内存列表信息（待机列表）</summary>
        SystemMemoryListInformation = 0x50
    }

    /// <summary>
    /// 内存列表命令
    /// </summary>
    public enum MEMORY_LIST_COMMAND
    {
        MemoryCaptureAccessedBits = 0,
        MemoryEmptyWorkingSets = 1,
        MemoryFlushModifiedList = 2,
        MemoryPurgeStandbyList = 4,
        MemoryPurgeLowPriorityStandbyList = 5,
        MemoryCommandMax = 6
    }

    /// <summary>
    /// SYSTEM_MEMORY_LIST_COMMAND 结构
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct SYSTEM_MEMORY_LIST_COMMAND
    {
        public MEMORY_LIST_COMMAND Command;
    }

    /// <summary>设置系统信息（未文档化 API，用于清空待机列表）</summary>
    /// <param name="infoClass">信息类型</param>
    /// <param name="info">信息指针</param>
    /// <param name="length">数据长度</param>
    /// <returns>STATUS_SUCCESS=0 表示成功</returns>
    [DllImport("ntdll.dll")]
    public static extern int NtSetSystemInformation(int infoClass, IntPtr info, int length);

    // ==================== 系统环境滴答计数 ====================

    /// <summary>获取系统启动以来的毫秒数</summary>
    [DllImport("kernel32.dll")]
    public static extern uint GetTickCount();

    // ==================== 光标位置 ====================

    /// <summary>光标坐标点结构体</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct POINT
    {
        /// <summary>X 坐标</summary>
        public int X;
        /// <summary>Y 坐标</summary>
        public int Y;
    }

    /// <summary>获取当前光标位置（屏幕坐标）</summary>
    [DllImport("user32.dll")]
    public static extern bool GetCursorPos(out POINT lpPoint);

    /// <summary>获取按键异步状态（用于实时按键检测）</summary>
    /// <param name="vKey">虚拟键码</param>
    /// <returns>最高位为 1 表示按下</returns>
    [DllImport("user32.dll")]
    public static extern short GetAsyncKeyState(int vKey);
}
