using Microsoft.Win32;

namespace Toolbox.Services;

// ==================== 模型定义 ====================

/// <summary>
/// 右键菜单项模型 — 描述一个 Shell Extension 右键菜单项
/// </summary>
public class ContextMenuItem
{
    /// <summary>菜单项显示名称（从注册表键名或默认值获取）</summary>
    public string Name { get; set; } = "";

    /// <summary>CLSID（COM 组件类标识符）</summary>
    public string Clsid { get; set; } = "";

    /// <summary>关联的文件类型或菜单位置</summary>
    public string FileType { get; set; } = "";

    /// <summary>来源路径（注册表完整路径）</summary>
    public string RegistryPath { get; set; } = "";

    /// <summary>菜单位置分类</summary>
    public ContextMenuLocation Location { get; set; }

    /// <summary>位置分类的中文显示名称</summary>
    public string LocationDisplay => Location switch
    {
        ContextMenuLocation.AllFiles => "所有文件",
        ContextMenuLocation.Directory => "文件夹",
        ContextMenuLocation.DirectoryBackground => "文件夹背景",
        ContextMenuLocation.Folder => "文件夹",
        ContextMenuLocation.Drive => "驱动器",
        ContextMenuLocation.AllObjects => "所有对象",
        ContextMenuLocation.LnkFile => "快捷方式",
        ContextMenuLocation.Approved => "系统已批准",
        ContextMenuLocation.IconOverlay => "图标叠加层",
        _ => Location.ToString()
    };

    /// <summary>Shell Extension 的 DLL 路径</summary>
    public string DllPath { get; set; } = "";

    /// <summary>是否已启用</summary>
    public bool IsEnabled { get; set; } = true;

    /// <summary>是否为系统关键项（位于 Shell Extensions\Approved 路径下）</summary>
    public bool IsSystemItem { get; set; }

    /// <summary>注册表键的父路径（用于启用/禁用操作）</summary>
    public string? ParentKeyPath { get; set; }

    /// <summary>子键名称（CLSID 或值名称）</summary>
    public string? SubKeyName { get; set; }

    /// <summary>原始值（用于 Approved 列表恢复）</summary>
    public string? OriginalValue { get; set; }
}

/// <summary>
/// 右键菜单位置分类
/// </summary>
public enum ContextMenuLocation
{
    /// <summary>所有文件类型（HKCR\*）</summary>
    AllFiles,
    /// <summary>目录（HKCR\Directory）</summary>
    Directory,
    /// <summary>目录背景（HKCR\Directory\Background）</summary>
    DirectoryBackground,
    /// <summary>文件夹（HKCR\Folder）</summary>
    Folder,
    /// <summary>驱动器（HKCR\Drive）</summary>
    Drive,
    /// <summary>所有文件系统对象（HKCR\AllFileSystemObjects）</summary>
    AllObjects,
    /// <summary>快捷方式（HKCR\lnkfile）</summary>
    LnkFile,
    /// <summary>系统已批准的 Shell 扩展</summary>
    Approved,
    /// <summary>图标叠加层标识符</summary>
    IconOverlay
}

// ==================== 右键菜单服务 ====================

/// <summary>
/// 右键菜单服务接口
/// </summary>
public interface IContextMenuService
{
    /// <summary>扫描所有 Shell Extension 注册位置的右键菜单项</summary>
    List<ContextMenuItem> ScanAllContextMenuItems(
        IProgress<int>? progress = null, CancellationToken cancellationToken = default);

    /// <summary>启用指定菜单项</summary>
    bool EnableItem(ContextMenuItem item);

    /// <summary>禁用指定菜单项</summary>
    bool DisableItem(ContextMenuItem item);

    /// <summary>删除指定菜单项</summary>
    bool DeleteItem(ContextMenuItem item);

    /// <summary>重启 Explorer 进程以应用更改</summary>
    void RestartExplorer();
}

/// <summary>
/// 右键菜单服务实现
/// 扫描 9 个 Shell Extension 注册位置，管理启用/禁用/删除
/// 禁用策略：重命名 CLSID 键（添加前缀）或修改 Approved 列表值
/// 系统项检测：路径含 Shell Extensions → L2 二次确认
/// </summary>
public class ContextMenuService : IContextMenuService
{
    private readonly ILogService _log;

    /// <summary>用于标记禁用的键名前缀</summary>
    private const string DisabledPrefix = "_toolbox_disabled_";

    public ContextMenuService(ILogService logService)
    {
        _log = logService;
    }

    // ==================== 扫描路径定义 ====================

    /// <summary>
    /// 9 个 Shell Extension 扫描位置
    /// </summary>
    private static readonly (string KeyPath, RegistryHive Hive, ContextMenuLocation Location, string FileType)[] ScanLocations = new[]
    {
        // 1. 所有文件类型的右键菜单
        (@"SOFTWARE\Classes\*\shellex\ContextMenuHandlers", RegistryHive.LocalMachine, ContextMenuLocation.AllFiles, "*"),
        // 2. 目录右键菜单
        (@"SOFTWARE\Classes\Directory\shellex\ContextMenuHandlers", RegistryHive.LocalMachine, ContextMenuLocation.Directory, "Directory"),
        // 3. 目录背景右键菜单
        (@"SOFTWARE\Classes\Directory\Background\shellex\ContextMenuHandlers", RegistryHive.LocalMachine, ContextMenuLocation.DirectoryBackground, "Directory\\Background"),
        // 4. 文件夹右键菜单
        (@"SOFTWARE\Classes\Folder\shellex\ContextMenuHandlers", RegistryHive.LocalMachine, ContextMenuLocation.Folder, "Folder"),
        // 5. 驱动器右键菜单
        (@"SOFTWARE\Classes\Drive\shellex\ContextMenuHandlers", RegistryHive.LocalMachine, ContextMenuLocation.Drive, "Drive"),
        // 6. 所有文件系统对象右键菜单
        (@"SOFTWARE\Classes\AllFileSystemObjects\shellex\ContextMenuHandlers", RegistryHive.LocalMachine, ContextMenuLocation.AllObjects, "AllFileSystemObjects"),
        // 7. 快捷方式右键菜单
        (@"SOFTWARE\Classes\lnkfile\shellex\ContextMenuHandlers", RegistryHive.LocalMachine, ContextMenuLocation.LnkFile, "lnkfile"),
        // 8. 系统已批准的 Shell 扩展
        (@"SOFTWARE\Microsoft\Windows\CurrentVersion\Shell Extensions\Approved", RegistryHive.LocalMachine, ContextMenuLocation.Approved, "Approved"),
        // 9. 图标叠加层标识符
        (@"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\ShellIconOverlayIdentifiers", RegistryHive.LocalMachine, ContextMenuLocation.IconOverlay, "IconOverlay"),
    };

    // ==================== 扫描 ====================

    /// <summary>
    /// 扫描所有 9 个 Shell Extension 注册位置
    /// </summary>
    public List<ContextMenuItem> ScanAllContextMenuItems(
        IProgress<int>? progress = null, CancellationToken cancellationToken = default)
    {
        var items = new List<ContextMenuItem>();
        int total = ScanLocations.Length;
        int current = 0;

        foreach (var (keyPath, hive, location, fileType) in ScanLocations)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
                using var key = baseKey.OpenSubKey(keyPath, writable: false);

                if (key == null)
                {
                    _log.LogWarning($"注册表路径不存在: {keyPath}");
                    current++;
                    progress?.Report(current * 100 / total);
                    continue;
                }

                var fullPath = hive == RegistryHive.LocalMachine
                    ? $"HKLM\\{keyPath}" : $"HKCU\\{keyPath}";

                // 第 8 项（Approved 列表）的结构不同：值是 "CLSID" = "描述"
                if (location == ContextMenuLocation.Approved)
                {
                    items.AddRange(ScanApprovedList(key, fullPath));
                }
                else
                {
                    // 其他位置：子键名 = CLSID，可能包含默认值
                    items.AddRange(ScanContextMenuHandlers(key, fullPath, location, fileType));
                }

                current++;
                progress?.Report(current * 100 / total);
            }
            catch (Exception ex)
            {
                _log.LogWarning($"扫描右键菜单位置失败: {keyPath}: {ex.Message}");
                current++;
                progress?.Report(current * 100 / total);
            }
        }

        _log.LogInfo($"右键菜单扫描完成: 共 {items.Count} 项 (9个位置)");
        return items;
    }

    /// <summary>
    /// 扫描 ContextMenuHandlers 类型的键
    /// 子键名 = CLSID，默认值 = 显示名称
    /// </summary>
    private List<ContextMenuItem> ScanContextMenuHandlers(
        RegistryKey parentKey, string fullPath, ContextMenuLocation location, string fileType)
    {
        var items = new List<ContextMenuItem>();

        foreach (var subKeyName in parentKey.GetSubKeyNames())
        {
            try
            {
                using var subKey = parentKey.OpenSubKey(subKeyName, writable: false);
                if (subKey == null) continue;

                string displayName = subKeyName;
                string defaultValue = subKey.GetValue("")?.ToString() ?? "";
                if (!string.IsNullOrEmpty(defaultValue))
                    displayName = defaultValue;

                // 查找对应的 CLSID 键以获取 DLL 路径
                string dllPath = ResolveClsidDllPath(subKeyName);

                // 检查是否为已禁用的项
                bool isEnabled = !subKeyName.StartsWith(DisabledPrefix);
                string originalClsid = subKeyName.StartsWith(DisabledPrefix)
                    ? subKeyName.Substring(DisabledPrefix.Length) : subKeyName;

                var item = new ContextMenuItem
                {
                    Name = displayName,
                    Clsid = originalClsid,
                    FileType = fileType,
                    RegistryPath = $"{fullPath}\\{subKeyName}",
                    Location = location,
                    DllPath = dllPath,
                    IsEnabled = isEnabled,
                    IsSystemItem = false, // ContextMenuHandlers 不是 Approved 系统项
                    ParentKeyPath = fullPath,
                    SubKeyName = subKeyName
                };

                items.Add(item);
            }
            catch (Exception ex)
            {
                _log.LogWarning($"读取右键菜单项失败: {fullPath}\\{subKeyName}: {ex.Message}");

                items.Add(new ContextMenuItem
                {
                    Name = subKeyName,
                    Clsid = subKeyName,
                    FileType = fileType,
                    RegistryPath = $"{fullPath}\\{subKeyName}",
                    Location = location,
                    DllPath = "",
                    IsEnabled = false,
                    IsSystemItem = false,
                    ParentKeyPath = fullPath,
                    SubKeyName = subKeyName
                });
            }
        }

        return items;
    }

    /// <summary>
    /// 扫描 Shell Extensions\Approved 列表
    /// 结构：值名 = CLSID GUID 字符串，值数据 = 描述文本
    /// 特殊处理：也扫描 WOW6432Node 对应的 Approved 列表
    /// </summary>
    private List<ContextMenuItem> ScanApprovedList(RegistryKey approvedKey, string fullPath)
    {
        var items = new List<ContextMenuItem>();

        foreach (var valueName in approvedKey.GetValueNames())
        {
            try
            {
                var value = approvedKey.GetValue(valueName);
                if (value == null) continue;

                string displayName = value.ToString() ?? valueName;
                string dllPath = ResolveClsidDllPath(valueName);

                // 检查是否为已禁用的项（以 _toolbox_disabled_ 开头的值名）
                bool isEnabled = !valueName.StartsWith(DisabledPrefix);
                string originalValueName = valueName.StartsWith(DisabledPrefix)
                    ? valueName.Substring(DisabledPrefix.Length) : valueName;

                items.Add(new ContextMenuItem
                {
                    Name = displayName,
                    Clsid = originalValueName,
                    FileType = "Approved",
                    RegistryPath = $"{fullPath}\\{valueName}",
                    Location = ContextMenuLocation.Approved,
                    DllPath = dllPath,
                    IsEnabled = isEnabled,
                    IsSystemItem = true, // Approved 列表中的都是系统项
                    ParentKeyPath = fullPath,
                    SubKeyName = valueName,
                    OriginalValue = isEnabled ? value.ToString() : null
                });
            }
            catch (Exception ex)
            {
                _log.LogWarning($"读取 Approved 列表项失败: {valueName}: {ex.Message}");
            }
        }

        return items;
    }

    /// <summary>
    /// 根据 CLSID 解析对应的 DLL 路径
    /// 查找 HKCR\CLSID\{guid}\InprocServer32 的默认值
    /// </summary>
    private string ResolveClsidDllPath(string clsid)
    {
        try
        {
            // 移除可能的前缀
            string cleanClsid = clsid.StartsWith(DisabledPrefix)
                ? clsid.Substring(DisabledPrefix.Length) : clsid;

            // 确保是 GUID 格式
            if (!cleanClsid.StartsWith("{"))
                return "";

            // 查找 CLSID 键
            using var clsidKey = Registry.ClassesRoot.OpenSubKey(
                $@"CLSID\{cleanClsid}\InprocServer32", writable: false);

            if (clsidKey != null)
            {
                var dllPath = clsidKey.GetValue("")?.ToString() ?? "";
                return dllPath;
            }

            // 尝试 LocalServer32
            using var localServerKey = Registry.ClassesRoot.OpenSubKey(
                $@"CLSID\{cleanClsid}\LocalServer32", writable: false);

            if (localServerKey != null)
            {
                return localServerKey.GetValue("")?.ToString() ?? "";
            }
        }
        catch
        {
            // 无法解析CLSID，忽略
        }

        return "";
    }

    // ==================== 启用/禁用 ====================

    /// <summary>
    /// 启用指定右键菜单项
    /// </summary>
    public bool EnableItem(ContextMenuItem item)
    {
        try
        {
            if (item.Location == ContextMenuLocation.Approved)
            {
                return RestoreApprovedItem(item);
            }
            else
            {
                return RestoreSubKey(item);
            }
        }
        catch (Exception ex)
        {
            _log.LogError($"启用右键菜单项失败: {item.Name}", ex);
            return false;
        }
    }

    /// <summary>
    /// 禁用指定右键菜单项
    /// ContextMenuHandlers 类型：重命名子键（添加 DisabledPrefix）
    /// Approved 类型：重命名值名称（添加 DisabledPrefix）
    /// </summary>
    public bool DisableItem(ContextMenuItem item)
    {
        try
        {
            if (item.Location == ContextMenuLocation.Approved)
            {
                return RenameApprovedItem(item);
            }
            else
            {
                return RenameSubKey(item);
            }
        }
        catch (Exception ex)
        {
            _log.LogError($"禁用右键菜单项失败: {item.Name}", ex);
            return false;
        }
    }

    /// <summary>
    /// 重命名子键来禁用（ContextMenuHandlers 类型）
    /// </summary>
    private bool RenameSubKey(ContextMenuItem item)
    {
        if (string.IsNullOrEmpty(item.ParentKeyPath) || string.IsNullOrEmpty(item.SubKeyName))
            return false;

        try
        {
            // 解析父路径
            var parts = item.ParentKeyPath.Split('\\', 2);
            if (parts.Length < 2) return false;
            var hive = parts[0] == "HKLM" ? Registry.LocalMachine : Registry.CurrentUser;
            var subPath = parts[1];

            using var parentKey = RegistryKey.OpenBaseKey(
                hive == Registry.LocalMachine ? RegistryHive.LocalMachine : RegistryHive.CurrentUser,
                RegistryView.Registry64).OpenSubKey(subPath, writable: true);

            if (parentKey == null) return false;

            string oldName = item.SubKeyName;
            string newName = DisabledPrefix + item.Clsid;

            // 如果已经是禁用状态，跳过
            if (oldName.StartsWith(DisabledPrefix)) return true;

            // 使用 Windows API 重命名注册表键（只能通过复制-删除实现）
            // 复制键值
            using var sourceKey = parentKey.OpenSubKey(oldName, writable: false);
            if (sourceKey == null) return false;

            // 检查目标是否已存在
            var existingSubKey = parentKey.OpenSubKey(newName);
            if (existingSubKey != null)
            {
                existingSubKey.Close();
                // 如果已存在带前缀的键，先删除
                parentKey.DeleteSubKeyTree(newName, throwOnMissingSubKey: false);
            }

            // 创建新的禁用键
            using (var destKey = parentKey.CreateSubKey(newName))
            {
                CopyKey(sourceKey, destKey);
            }

            // 删除原键
            parentKey.DeleteSubKeyTree(oldName);

            item.SubKeyName = newName;
            item.IsEnabled = false;

            _log.LogOperation("contextmenu", "disable", $"{item.Name} ({item.LocationDisplay}): {oldName} -> {newName}");
            return true;
        }
        catch (Exception ex)
        {
            _log.LogError($"重命名注册表子键失败: {item.Name}", ex);
            return false;
        }
    }

    /// <summary>
    /// 恢复子键名称来启用
    /// </summary>
    private bool RestoreSubKey(ContextMenuItem item)
    {
        if (string.IsNullOrEmpty(item.ParentKeyPath) || string.IsNullOrEmpty(item.SubKeyName))
            return false;

        // 如果键名不含禁用前缀，说明已经启用
        if (!item.SubKeyName.StartsWith(DisabledPrefix)) return true;

        try
        {
            var parts = item.ParentKeyPath.Split('\\', 2);
            if (parts.Length < 2) return false;
            var hive = parts[0] == "HKLM" ? Registry.LocalMachine : Registry.CurrentUser;
            var subPath = parts[1];

            using var parentKey = RegistryKey.OpenBaseKey(
                hive == Registry.LocalMachine ? RegistryHive.LocalMachine : RegistryHive.CurrentUser,
                RegistryView.Registry64).OpenSubKey(subPath, writable: true);

            if (parentKey == null) return false;

            string oldName = item.SubKeyName;
            string newName = item.Clsid; // 恢复为原始 CLSID

            using var sourceKey = parentKey.OpenSubKey(oldName, writable: false);
            if (sourceKey == null) return false;

            // 删除可能存在的原始键
            parentKey.DeleteSubKeyTree(newName, throwOnMissingSubKey: false);

            using (var destKey = parentKey.CreateSubKey(newName))
            {
                CopyKey(sourceKey, destKey);
            }

            parentKey.DeleteSubKeyTree(oldName);

            item.SubKeyName = newName;
            item.IsEnabled = true;

            _log.LogOperation("contextmenu", "enable", $"{item.Name} ({item.LocationDisplay}): {oldName} -> {newName}");
            return true;
        }
        catch (Exception ex)
        {
            _log.LogError($"恢复注册表子键失败: {item.Name}", ex);
            return false;
        }
    }

    /// <summary>
    /// 复制注册表键的所有值和子键
    /// </summary>
    private void CopyKey(RegistryKey source, RegistryKey dest)
    {
        // 复制值
        foreach (var valueName in source.GetValueNames())
        {
            var value = source.GetValue(valueName);
            var kind = source.GetValueKind(valueName);
            if (value != null)
                dest.SetValue(valueName, value, kind);
        }

        // 复制子键（递归）
        foreach (var subKeyName in source.GetSubKeyNames())
        {
            using var sourceSub = source.OpenSubKey(subKeyName, writable: false);
            if (sourceSub == null) continue;

            using var destSub = dest.CreateSubKey(subKeyName);
            CopyKey(sourceSub, destSub);
        }
    }

    /// <summary>
    /// Approved 列表禁用：重命名值
    /// </summary>
    private bool RenameApprovedItem(ContextMenuItem item)
    {
        if (string.IsNullOrEmpty(item.ParentKeyPath) || string.IsNullOrEmpty(item.SubKeyName))
            return false;

        try
        {
            var parts = item.ParentKeyPath.Split('\\', 2);
            if (parts.Length < 2) return false;
            var subPath = parts[1];

            using var key = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64)
                .OpenSubKey(subPath, writable: true);
            if (key == null) return false;

            string oldName = item.SubKeyName;
            if (oldName.StartsWith(DisabledPrefix)) return true;

            string newName = DisabledPrefix + item.Clsid;

            // 备份原值
            var originalValue = key.GetValue(oldName);
            if (originalValue != null)
            {
                key.SetValue(newName, originalValue);
                key.DeleteValue(oldName);

                item.SubKeyName = newName;
                item.IsEnabled = false;
                item.OriginalValue = originalValue.ToString();

                _log.LogOperation("contextmenu", "disable", $"Approved: {item.Name}: {oldName} -> {newName}");
                return true;
            }
            return false;
        }
        catch (Exception ex)
        {
            _log.LogError($"禁用 Approved 列表项失败: {item.Name}", ex);
            return false;
        }
    }

    /// <summary>
    /// Approved 列表启用：恢复值名称
    /// </summary>
    private bool RestoreApprovedItem(ContextMenuItem item)
    {
        if (string.IsNullOrEmpty(item.ParentKeyPath) || string.IsNullOrEmpty(item.SubKeyName))
            return false;

        if (!item.SubKeyName.StartsWith(DisabledPrefix)) return true;

        try
        {
            var parts = item.ParentKeyPath.Split('\\', 2);
            if (parts.Length < 2) return false;
            var subPath = parts[1];

            using var key = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64)
                .OpenSubKey(subPath, writable: true);
            if (key == null) return false;

            string oldName = item.SubKeyName;
            string newName = item.Clsid;

            var disabledValue = key.GetValue(oldName);
            if (disabledValue != null)
            {
                key.SetValue(newName, disabledValue);
                key.DeleteValue(oldName);

                item.SubKeyName = newName;
                item.IsEnabled = true;

                _log.LogOperation("contextmenu", "enable", $"Approved: {item.Name}: {oldName} -> {newName}");
                return true;
            }
            return false;
        }
        catch (Exception ex)
        {
            _log.LogError($"恢复 Approved 列表项失败: {item.Name}", ex);
            return false;
        }
    }

    // ==================== 删除 ====================

    /// <summary>
    /// 删除指定右键菜单项
    /// ContextMenuHandlers 类型：删除子键树
    /// Approved 类型：删除值
    /// </summary>
    public bool DeleteItem(ContextMenuItem item)
    {
        try
        {
            if (string.IsNullOrEmpty(item.ParentKeyPath) || string.IsNullOrEmpty(item.SubKeyName))
                return false;

            var parts = item.ParentKeyPath.Split('\\', 2);
            if (parts.Length < 2) return false;
            var hive = parts[0] == "HKLM" ? Registry.LocalMachine : Registry.CurrentUser;
            var subPath = parts[1];

            using var parentKey = RegistryKey.OpenBaseKey(
                hive == Registry.LocalMachine ? RegistryHive.LocalMachine : RegistryHive.CurrentUser,
                RegistryView.Registry64).OpenSubKey(subPath, writable: true);

            if (parentKey == null) return false;

            if (item.Location == ContextMenuLocation.Approved)
            {
                try { parentKey.DeleteValue(item.SubKeyName); } catch { /* 值不存在则忽略 */ }
            }
            else
            {
                parentKey.DeleteSubKeyTree(item.SubKeyName, throwOnMissingSubKey: false);
            }

            _log.LogOperation("contextmenu", "delete", $"{item.Name} ({item.LocationDisplay})");
            return true;
        }
        catch (Exception ex)
        {
            _log.LogError($"删除右键菜单项失败: {item.Name}", ex);
            return false;
        }
    }

    /// <summary>
    /// 重启 Explorer 进程以应用右键菜单更改
    /// </summary>
    public void RestartExplorer()
    {
        try
        {
            var explorerProcesses = System.Diagnostics.Process.GetProcessesByName("explorer");
            foreach (var p in explorerProcesses)
            {
                try
                {
                    p.Kill();
                    p.WaitForExit(3000);
                }
                catch { }
            }

            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "explorer.exe",
                UseShellExecute = true
            });

            _log.LogInfo("Explorer 进程已重启以应用右键菜单更改");
        }
        catch (Exception ex)
        {
            _log.LogError("重启 Explorer 进程失败", ex);
        }
    }
}
