using System.Windows;

namespace Toolbox.Helpers;

/// <summary>
/// 安全确认辅助类 — 全局统一的四级确认阶梯
/// L1 提示：弹窗提示 + 取消/确认按钮
/// L2 二次确认：弹窗 + 5秒冷却 + 确认按钮
/// L3 输入确认：L2基础上需要手动输入确认短语
/// L4 管理员确认：L3 + 需要管理员权限
/// </summary>
public static class ConfirmationHelper
{
    /// <summary>
    /// L1 级别确认 — 简单提示弹窗
    /// 适用于：清空回收站、删除非系统右键菜单项等
    /// </summary>
    /// <param name="message">提示消息</param>
    /// <returns>用户点击确认返回 true，取消返回 false</returns>
    public static bool RequestL1(string message)
    {
        var result = MessageBox.Show(
            message,
            "确认操作",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Information);
        return result == MessageBoxResult.OK;
    }

    /// <summary>
    /// L2 级别确认 — 带5秒冷却的确认弹窗
    /// 适用于：禁用系统关键启动项、删除系统右键菜单项等不可逆操作
    /// </summary>
    /// <param name="message">警告消息</param>
    /// <returns>用户等待冷却后点击确认返回 true，否则 false</returns>
    public static bool RequestL2(string message)
    {
        var dialog = new Views.Dialogs.L2ConfirmDialog(message);
        return dialog.ShowDialog() == true;
    }

    /// <summary>
    /// L3 级别确认 — 需要手动输入确认短语的弹窗
    /// 适用于：文件粉碎、C盘清理高级项等危险操作
    /// </summary>
    /// <param name="message">警告消息</param>
    /// <param name="confirmPhrase">需要用户输入的确认短语</param>
    /// <returns>用户输入正确短语并确认返回 true，否则 false</returns>
    public static bool RequestL3(string message, string confirmPhrase)
    {
        var dialog = new Views.Dialogs.L3ConfirmDialog(message, confirmPhrase);
        return dialog.ShowDialog() == true;
    }

    /// <summary>
    /// L4 级别确认 — 需要管理员权限的操作
    /// 适用于：注册表修复、内存释放、修改系统目录等系统级操作
    /// 非管理员时会触发 UAC 提权重新启动
    /// </summary>
    /// <returns>已有管理员权限返回 true，提权成功返回 false（当前进程退出）</returns>
    public static bool RequestAdmin()
    {
        // 如果已经是管理员，直接返回 true
        if (IsAdministrator()) return true;

        try
        {
            // 以管理员权限重新启动自身
            var exePath = Environment.ProcessPath!;
            var psi = new System.Diagnostics.ProcessStartInfo(exePath)
            {
                UseShellExecute = true,
                Verb = "runas"  // 触发 UAC 提权
            };
            System.Diagnostics.Process.Start(psi);

            // 当前非管理员进程退出，由提权后的新进程继续
            return false;
        }
        catch
        {
            // 用户拒绝 UAC 或提权失败
            MessageBox.Show(
                "需要管理员权限执行此操作。\n请右键工具箱图标 → 以管理员身份运行。",
                "权限不足",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return false;
        }
    }

    /// <summary>
    /// 检查当前进程是否以管理员权限运行
    /// </summary>
    /// <returns>是管理员返回 true，否则 false</returns>
    public static bool IsAdministrator()
    {
        using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
        var principal = new System.Security.Principal.WindowsPrincipal(identity);
        return principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
    }
}
