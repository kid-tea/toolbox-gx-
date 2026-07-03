using System.Diagnostics;

namespace Toolbox.Services;

/// <summary>
/// 单实例服务接口
/// </summary>
public interface ISingleInstanceService
{
    /// <summary>检查当前是否为第一个实例</summary>
    /// <returns>是第一个实例返回 true，否则返回 false（应退出）</returns>
    bool IsFirstInstance();

    /// <summary>激活已存在的实例窗口</summary>
    void ActivateExistingWindow();
}

/// <summary>
/// 单实例服务实现
/// 使用命名 Mutex 确保工具箱只有一个实例在运行
/// 检测到已有实例时，激活现有窗口而不是重复启动
/// </summary>
public class SingleInstanceService : ISingleInstanceService
{
    /// <summary>Mutex 名称，使用全局命名空间确保跨用户会话检测</summary>
    private const string MutexName = @"Global\Toolbox_SingleInstance_Mutex";

    /// <summary>
    /// 持有 Mutex 引用作为类字段，防止被 GC 回收。
    /// 必须保持为类级别字段，局部变量会被 GC 在方法返回后回收导致互斥锁失效。
    /// </summary>
    private Mutex? _mutex;

    /// <summary>
    /// 检查当前是否为第一个实例
    /// 如果不是首个实例，则激活已有窗口并返回 false
    /// </summary>
    public bool IsFirstInstance()
    {
        try
        {
            _mutex = new Mutex(true, MutexName, out bool createdNew);
            if (!createdNew)
            {
                // 已有实例在运行，激活它
                _mutex.Dispose();
                _mutex = null;
                ActivateExistingWindow();
                return false;
            }

            return true;
        }
        catch
        {
            // Mutex 创建失败时允许启动（降级处理）
            return true;
        }
    }

    /// <summary>
    /// 激活已存在的工具箱实例窗口
    /// 通过进程名称查找，将窗口恢复到前台
    /// </summary>
    public void ActivateExistingWindow()
    {
        try
        {
            var current = Process.GetCurrentProcess();

            // 查找同名的其他进程
            var existing = Process.GetProcessesByName(current.ProcessName)
                .FirstOrDefault(p => p.Id != current.Id);

            if (existing != null)
            {
                var hWnd = existing.MainWindowHandle;
                if (hWnd != IntPtr.Zero)
                {
                    // 如果窗口最小化，先恢复
                    if (Native.NativeMethods.IsIconic(hWnd))
                        Native.NativeMethods.ShowWindow(hWnd, 9); // SW_RESTORE = 9

                    // 将窗口设为前台
                    Native.NativeMethods.SetForegroundWindow(hWnd);
                }
            }
        }
        catch
        {
            // 激活已有窗口失败不影响启动流程
        }
    }
}
