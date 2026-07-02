using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Toolbox.Helpers;
using Toolbox.Native;
using Toolbox.Services;
using NTSTATUS = System.Int32;

namespace Toolbox.ViewModels;

/// <summary>
/// 内存释放 ViewModel
/// 内存状态总览（Total/Used/Available/Cached）、前10进程内存排行、三种释放操作
/// 操作：清空工作集（EmptyWorkingSet）、清空待机列表（NtSetSystemInformation）、清空系统缓存
/// </summary>
public partial class MemoryReleaseViewModel : ViewModelBase
{
    private readonly ILogService _log;

    // ==================== 内存概览 ====================

    /// <summary>物理内存总量（格式化字符串）</summary>
    [ObservableProperty]
    private string _totalMemory = "--";

    /// <summary>已用内存（格式化字符串）</summary>
    [ObservableProperty]
    private string _usedMemory = "--";

    /// <summary>可用内存（格式化字符串）</summary>
    [ObservableProperty]
    private string _availableMemory = "--";

    /// <summary>已缓存内存（格式化字符串）</summary>
    [ObservableProperty]
    private string _cachedMemory = "--";

    /// <summary>内存使用率（0-100）</summary>
    [ObservableProperty]
    private double _memoryUsagePercent;

    /// <summary>总物理内存（字节）</summary>
    private ulong _totalBytes;

    /// <summary>已用物理内存（字节）</summary>
    private ulong _usedBytes;

    /// <summary>可用物理内存（字节）</summary>
    private ulong _availableBytes;

    /// <summary>释放前的已用内存（用于对比）</summary>
    private ulong _beforeReleaseBytes;

    // ==================== 进程列表 ====================

    /// <summary>内存占用前10的进程列表</summary>
    [ObservableProperty]
    private ObservableCollection<ProcessMemoryItem> _topProcesses = new();

    /// <summary>
    /// 进程内存信息项
    /// </summary>
    public class ProcessMemoryItem
    {
        /// <summary>进程 ID</summary>
        public int Pid { get; set; }
        /// <summary>进程名称</summary>
        public string Name { get; set; } = "";
        /// <summary>工作集大小（格式化）</summary>
        public string WorkingSet { get; set; } = "";
        /// <summary>工作集大小（字节）</summary>
        public long WorkingSetBytes { get; set; }
        /// <summary>私有工作集大小（格式化）</summary>
        public string PrivateWS { get; set; } = "";
    }

    // ==================== 释放操作状态 ====================

    /// <summary>是否正在释放</summary>
    [ObservableProperty]
    private bool _isReleasing;

    /// <summary>释放结果信息</summary>
    [ObservableProperty]
    private string _releaseResult = "";

    /// <summary>释放前已用内存</summary>
    [ObservableProperty]
    private string _beforeReleaseMemory = "";

    /// <summary>释放后已用内存</summary>
    [ObservableProperty]
    private string _afterReleaseMemory = "";

    /// <summary>是否已完成释放（显示对比）</summary>
    [ObservableProperty]
    private bool _showComparison;

    /// <summary>是否为管理员（非管理员时提示）</summary>
    [ObservableProperty]
    private bool _isAdmin;

    /// <summary>管理员权限提示文本</summary>
    [ObservableProperty]
    private string _adminTip = "";

    /// <summary>
    /// 构造函数 — 依赖注入
    /// </summary>
    public MemoryReleaseViewModel(ILogService log)
    {
        _log = log;
        IsAdmin = ConfirmationHelper.IsAdministrator();
        AdminTip = IsAdmin ? "" : "⚠ 部分操作需要管理员权限。按钮保持可点击，点击后将触发 UAC 提权。";

        // 加载内存信息
        RefreshMemoryInfo();
        StatusMessage = "就绪 — 点击释放按钮清理内存";
    }

    /// <summary>
    /// 刷新内存状态信息
    /// </summary>
    [RelayCommand]
    private void RefreshMemoryInfo()
    {
        try
        {
            var memStatus = new NativeMethods.MEMORYSTATUSEX
            {
                dwLength = (uint)Marshal.SizeOf<NativeMethods.MEMORYSTATUSEX>()
            };

            if (!NativeMethods.GlobalMemoryStatusEx(ref memStatus))
            {
                StatusMessage = "获取内存状态失败";
                return;
            }

            _totalBytes = memStatus.ullTotalPhys;
            _availableBytes = memStatus.ullAvailPhys;
            _usedBytes = _totalBytes - _availableBytes;
            ulong cachedBytes = 0;

            TotalMemory = FormatBytes(_totalBytes);
            UsedMemory = FormatBytes(_usedBytes);
            AvailableMemory = FormatBytes(_availableBytes);
            CachedMemory = FormatBytes(cachedBytes);
            MemoryUsagePercent = _totalBytes > 0 ? Math.Round((double)_usedBytes / _totalBytes * 100, 1) : 0;

            // 刷新进程列表
            RefreshTopProcesses();
        }
        catch (Exception ex)
        {
            StatusMessage = $"刷新内存信息失败: {ex.Message}";
            _log.LogError("刷新内存信息失败", ex);
        }
    }

    /// <summary>
    /// 获取内存占用前10的进程
    /// 每个 Process 对象使用后立即释放系统句柄
    /// </summary>
    private void RefreshTopProcesses()
    {
        try
        {
            var processInfos = new List<(int Pid, string Name, long WorkingSet, long PrivateBytes)>();

            foreach (var proc in Process.GetProcesses())
            {
                using (proc)
                {
                    try
                    {
                        long ws = proc.WorkingSet64;
                        long privateBytes = 0;
                        try
                        {
                            privateBytes = proc.PrivateMemorySize64;
                        }
                        catch
                        {
                            privateBytes = ws;
                        }

                        processInfos.Add((proc.Id, proc.ProcessName, ws, privateBytes));
                    }
                    catch
                    {
                        // 进程已退出或无权限访问
                    }
                }
            }

            var top10 = processInfos
                .OrderByDescending(p => p.WorkingSet)
                .Take(10)
                .ToList();

            TopProcesses.Clear();
            foreach (var (pid, name, ws, privateBytes) in top10)
            {
                TopProcesses.Add(new ProcessMemoryItem
                {
                    Pid = pid,
                    Name = name,
                    WorkingSetBytes = ws,
                    WorkingSet = FormatBytes((ulong)ws),
                    PrivateWS = FormatBytes((ulong)Math.Max(privateBytes, 0))
                });
            }
        }
        catch (Exception ex)
        {
            TopProcesses.Clear();
            _log.LogError("获取进程内存信息失败", ex);
        }
    }

    // ==================== 释放操作 ====================

    /// <summary>
    /// 清空进程工作集（EmptyWorkingSet API）
    /// </summary>
    [RelayCommand]
    private async Task ReleaseWorkingSetAsync()
    {
        if (IsReleasing) return;

        // 检查管理员权限
        if (!ConfirmationHelper.IsAdministrator())
        {
            if (!ConfirmationHelper.RequestAdmin())
                return; // 提权成功会重启进程
            // 提权被拒绝，仍尝试执行（非管理员也能清空部分工作集）
        }

        try
        {
            IsReleasing = true;
            ShowComparison = false;

            // 记录释放前内存
            var memBefore = new NativeMethods.MEMORYSTATUSEX
            {
                dwLength = (uint)Marshal.SizeOf<NativeMethods.MEMORYSTATUSEX>()
            };
            NativeMethods.GlobalMemoryStatusEx(ref memBefore);
            _beforeReleaseBytes = memBefore.ullTotalPhys - memBefore.ullAvailPhys;
            BeforeReleaseMemory = FormatBytes(_beforeReleaseBytes);

            StatusMessage = "正在清空进程工作集...";
            IsProgressIndeterminate = true;

            // 清空所有进程的工作集
            int successCount = 0;
            int failCount = 0;

            await Task.Run(() =>
            {
                foreach (var proc in Process.GetProcesses())
                {
                    try
                    {
                        if (NativeMethods.EmptyWorkingSet(proc.Handle))
                            successCount++;
                        else
                            failCount++;
                    }
                    catch
                    {
                        failCount++;
                    }
                }
            });

            // 记录释放后内存
            var memAfter = new NativeMethods.MEMORYSTATUSEX
            {
                dwLength = (uint)Marshal.SizeOf<NativeMethods.MEMORYSTATUSEX>()
            };
            NativeMethods.GlobalMemoryStatusEx(ref memAfter);
            ulong usedAfterBytes = memAfter.ullTotalPhys - memAfter.ullAvailPhys;
            AfterReleaseMemory = FormatBytes(usedAfterBytes);

            // 重新加载信息
            RefreshMemoryInfo();
            ShowComparison = true;

            ReleaseResult = $"清空工作集完成: 成功 {successCount}, 失败 {failCount}";
            _log.LogInfo($"清空工作集: 成功{successCount}, 失败{failCount}, 已用 {BeforeReleaseMemory} → {AfterReleaseMemory}");
        }
        catch (Exception ex)
        {
            StatusMessage = $"释放失败: {ex.Message}";
            _log.LogError("清空工作集失败", ex);
        }
        finally
        {
            IsReleasing = false;
            IsProgressIndeterminate = false;
        }
    }

    /// <summary>
    /// 清空待机列表（NtSetSystemInformation 未文档化 API）
    /// </summary>
    [RelayCommand]
    private async Task ReleaseStandbyListAsync()
    {
        if (IsReleasing) return;

        if (!ConfirmationHelper.IsAdministrator())
        {
            StatusMessage = "清空待机列表需要管理员权限";
            ReleaseResult = "请以管理员身份运行工具箱后再试";
            return;
        }

        if (!ConfirmationHelper.RequestL1(
            "此操作调用系统未公开 API (NtSetSystemInformation)，可能在 Windows 更新后失效。\n\n" +
            "操作本身是安全的（仅清空缓存，程序会自动重建），不会影响正在运行的程序和数据。\n\n" +
            "是否继续？"))
            return;

        try
        {
            IsReleasing = true;
            ShowComparison = false;

            var memBefore = new NativeMethods.MEMORYSTATUSEX
            {
                dwLength = (uint)Marshal.SizeOf<NativeMethods.MEMORYSTATUSEX>()
            };
            NativeMethods.GlobalMemoryStatusEx(ref memBefore);
            _beforeReleaseBytes = memBefore.ullTotalPhys - memBefore.ullAvailPhys;
            BeforeReleaseMemory = FormatBytes(_beforeReleaseBytes);

            StatusMessage = "正在清空待机列表...";
            IsProgressIndeterminate = true;

            await Task.Run(() =>
            {
                var command = new NativeMethods.SYSTEM_MEMORY_LIST_COMMAND
                {
                    Command = NativeMethods.MEMORY_LIST_COMMAND.MemoryPurgeStandbyList
                };

                IntPtr ptr = Marshal.AllocHGlobal(Marshal.SizeOf<NativeMethods.SYSTEM_MEMORY_LIST_COMMAND>());
                try
                {
                    Marshal.StructureToPtr(command, ptr, false);
                    NTSTATUS result = NativeMethods.NtSetSystemInformation(
                        (int)NativeMethods.SYSTEM_INFORMATION_CLASS.SystemMemoryListInformation,
                        ptr,
                        Marshal.SizeOf<NativeMethods.SYSTEM_MEMORY_LIST_COMMAND>());

                    if (result != 0)
                        throw new InvalidOperationException($"NtSetSystemInformation 返回 0x{result:X8}");
                }
                finally
                {
                    Marshal.FreeHGlobal(ptr);
                }
            });

            var memAfter = new NativeMethods.MEMORYSTATUSEX
            {
                dwLength = (uint)Marshal.SizeOf<NativeMethods.MEMORYSTATUSEX>()
            };
            NativeMethods.GlobalMemoryStatusEx(ref memAfter);
            ulong usedAfterBytes = memAfter.ullTotalPhys - memAfter.ullAvailPhys;
            AfterReleaseMemory = FormatBytes(usedAfterBytes);

            RefreshMemoryInfo();
            ShowComparison = true;

            ReleaseResult = "待机列表已清空";
            _log.LogInfo($"清空待机列表: 已用 {BeforeReleaseMemory} → {AfterReleaseMemory}");
        }
        catch (Exception ex)
        {
            StatusMessage = $"释放失败: {ex.Message}";
            ReleaseResult = $"操作失败: {ex.Message}";
            _log.LogError("清空待机列表失败", ex);
        }
        finally
        {
            IsReleasing = false;
            IsProgressIndeterminate = false;
        }
    }

    /// <summary>
    /// 清空系统缓存（文件系统缓存）
    /// </summary>
    [RelayCommand]
    private async Task ReleaseSystemCacheAsync()
    {
        if (IsReleasing) return;

        if (!ConfirmationHelper.IsAdministrator())
        {
            StatusMessage = "清空系统缓存需要管理员权限";
            ReleaseResult = "请以管理员身份运行工具箱后再试";
            return;
        }

        if (!ConfirmationHelper.RequestL1(
            "此操作调用系统未公开 API (NtSetSystemInformation)，可能在 Windows 更新后失效。\n\n" +
            "操作本身是安全的（仅清空缓存，程序会自动重建），不会影响正在运行的程序和数据。\n\n" +
            "是否继续？"))
            return;

        try
        {
            IsReleasing = true;
            ShowComparison = false;

            var memBefore = new NativeMethods.MEMORYSTATUSEX
            {
                dwLength = (uint)Marshal.SizeOf<NativeMethods.MEMORYSTATUSEX>()
            };
            NativeMethods.GlobalMemoryStatusEx(ref memBefore);
            _beforeReleaseBytes = memBefore.ullTotalPhys - memBefore.ullAvailPhys;
            BeforeReleaseMemory = FormatBytes(_beforeReleaseBytes);

            StatusMessage = "正在清空系统缓存...";
            IsProgressIndeterminate = true;

            await Task.Run(() =>
            {
                var command = new NativeMethods.SYSTEM_MEMORY_LIST_COMMAND
                {
                    Command = NativeMethods.MEMORY_LIST_COMMAND.MemoryPurgeStandbyList
                };

                IntPtr ptr = Marshal.AllocHGlobal(Marshal.SizeOf<NativeMethods.SYSTEM_MEMORY_LIST_COMMAND>());
                try
                {
                    Marshal.StructureToPtr(command, ptr, false);
                    NTSTATUS result = NativeMethods.NtSetSystemInformation(
                        (int)NativeMethods.SYSTEM_INFORMATION_CLASS.SystemMemoryListInformation,
                        ptr,
                        Marshal.SizeOf<NativeMethods.SYSTEM_MEMORY_LIST_COMMAND>());

                    if (result != 0)
                        throw new InvalidOperationException($"NtSetSystemInformation 返回 0x{result:X8}");
                }
                finally
                {
                    Marshal.FreeHGlobal(ptr);
                }
            });

            var memAfter = new NativeMethods.MEMORYSTATUSEX
            {
                dwLength = (uint)Marshal.SizeOf<NativeMethods.MEMORYSTATUSEX>()
            };
            NativeMethods.GlobalMemoryStatusEx(ref memAfter);
            ulong usedAfterBytes = memAfter.ullTotalPhys - memAfter.ullAvailPhys;
            AfterReleaseMemory = FormatBytes(usedAfterBytes);

            RefreshMemoryInfo();
            ShowComparison = true;

            ReleaseResult = "系统缓存已清空";
            _log.LogInfo($"清空系统缓存: 已用 {BeforeReleaseMemory} → {AfterReleaseMemory}");
        }
        catch (Exception ex)
        {
            StatusMessage = $"释放失败: {ex.Message}";
            ReleaseResult = $"操作失败: {ex.Message}";
            _log.LogError("清空系统缓存失败", ex);
        }
        finally
        {
            IsReleasing = false;
            IsProgressIndeterminate = false;
        }
    }

    /// <summary>
    /// 格式化字节数为可读字符串
    /// </summary>
    private static string FormatBytes(ulong bytes)
    {
        string[] sizes = { "B", "KB", "MB", "GB", "TB" };
        int order = 0;
        double size = bytes;
        while (size >= 1024 && order < sizes.Length - 1)
        {
            order++;
            size /= 1024;
        }
        return $"{size:0.#} {sizes[order]}";
    }
}
