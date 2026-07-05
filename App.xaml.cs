using Microsoft.Extensions.DependencyInjection;
using System.Windows;
using Toolbox.Models;
using Toolbox.Services;
using Toolbox.ViewModels;
using Toolbox.Views;

namespace Toolbox;

/// <summary>
/// 应用程序入口 — 负责 DI 注册、启动流程、.NET 运行时检测、配置文件完整性校验
/// 所有服务通过 Microsoft.Extensions.DependencyInjection 管理
/// 启动流程：检测运行时 → 配置 DI → 校验配置 → 加载主题 → 注册导航 → 显示窗口
/// </summary>
public partial class App : Application
{
    /// <summary>DI 容器，供全应用访问</summary>
    public static IServiceProvider ServiceProvider { get; private set; } = null!;

    /// <summary>
    /// 应用程序启动入口
    /// </summary>
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // ===== 第 1 步：检测 .NET 8.0 桌面运行时 =====
        if (!CheckDotNetRuntime())
        {
            Shutdown();
            return;
        }

        // ===== 第 2 步：单实例检查 =====
        // 必须在 DI 容器构建之前完成（使用临时 ServiceProvider 或直接检查）
        // 先构建一个最小 DI 容器用于单实例检测
        var tempServices = new ServiceCollection();
        tempServices.AddSingleton<ISingleInstanceService, SingleInstanceService>();
        var tempProvider = tempServices.BuildServiceProvider();
        var singleInstance = tempProvider.GetRequiredService<ISingleInstanceService>();
        if (!singleInstance.IsFirstInstance())
        {
            Shutdown();
            return;
        }

        // ===== 第 3 步：配置 DI 容器 =====
        var services = new ServiceCollection();
        ConfigureServices(services);
        ServiceProvider = services.BuildServiceProvider();

        // ===== 第 3 步：加载配置并校验完整性 =====
        var config = ServiceProvider.GetRequiredService<IConfigService>();
        AppSettings settings = config.LoadConfig<AppSettings>(config.SettingsFilePath) ?? new AppSettings();

        // 计算并校验配置文件哈希（完整性保护）
        if (settings != null && !string.IsNullOrEmpty(settings.ConfigHash) &&
            !config.ValidateHash(config.SettingsFilePath, settings.ConfigHash))
        {
            // 配置文件被篡改或损坏
            var backupPath = config.SettingsFilePath + $".corrupted.{DateTime.Now:yyyyMMddHHmmss}";
            try
            {
                if (System.IO.File.Exists(config.SettingsFilePath))
                    System.IO.File.Copy(config.SettingsFilePath, backupPath);
            }
            catch { /* 备份失败不阻止启动 */ }

            // 使用默认配置
            settings = new AppSettings();
        }

        // ===== 第 4 步：应用主题 =====
        var theme = ServiceProvider.GetRequiredService<IThemeService>();
        // settings 已通过 ?? 操作符确保非 null，显式声明以消除编译器警告
        theme.SetTheme(ThemeService.ParseTheme(settings!.Theme));

        // ===== 第 5 步：注册导航项 =====
        var nav = ServiceProvider.GetRequiredService<INavigationService>();
        PopulateNavigation(nav);

        // 默认选中第一个可用导航项
        var firstItem = nav.NavItems.FirstOrDefault();
        if (firstItem != null)
            nav.SelectedItem = firstItem;

        // ===== 第 6 步：记录启动日志 =====
        var logger = ServiceProvider.GetRequiredService<ILogService>();
        logger.LogInfo("工具箱启动成功");

        // ===== 第 7 步：显示主窗口 =====
        var mainWindow = ServiceProvider.GetRequiredService<MainWindow>();
        mainWindow.Show();
    }

    /// <summary>
    /// 检测 .NET 8.0 桌面运行时是否已安装
    /// 未安装时引导用户跳转微软官网下载
    /// </summary>
    /// <returns>运行时满足要求返回 true，否则 false</returns>
    private bool CheckDotNetRuntime()
    {
        // .NET 版本号：.NET Core 3.x = 3, .NET 5 = 5, .NET 8 = 8
        var version = Environment.Version;

        if (version.Major >= 8) return true;

        // 运行时版本不满足要求，弹窗引导下载
        var result = MessageBox.Show(
            "需要 .NET 8.0 桌面运行时才能运行工具箱。\n\n" +
            "是否打开微软下载页面？",
            ".NET 运行时未安装",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (result == MessageBoxResult.Yes)
        {
            // 使用系统默认浏览器打开下载页
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "https://dotnet.microsoft.com/download/dotnet/8.0",
                    UseShellExecute = true
                });
            }
            catch { /* 浏览器打开失败 */ }
        }

        return false;
    }

    /// <summary>
    /// 配置 DI 容器 — 注册所有服务和 ViewModel
    /// 服务生命周期：
    ///   - Singleton：全局共享（日志、配置、导航、主题、单实例）
    ///   - Transient：每次请求新建（ViewModel、View）
    /// </summary>
    private void ConfigureServices(IServiceCollection services)
    {
        // ===== 基础设施服务（Singleton） =====
        services.AddSingleton<ILogService, LogService>();
        services.AddSingleton<IConfigService, ConfigService>();
        services.AddSingleton<INavigationService, NavigationService>();
        services.AddSingleton<IThemeService, ThemeService>();
        services.AddSingleton<ISingleInstanceService, SingleInstanceService>();

        // ===== C盘清理（Singleton ViewModel keeps scan results until app exit） =====
        services.AddSingleton<ICleanupService, CleanupService>();
        services.AddSingleton<CleanupViewModel>();
        services.AddTransient<Views.CleanupView>();

        // ===== 磁盘空间分析（Singleton ViewModel keeps scan results until app exit） =====
        services.AddSingleton<IDiskAnalyzerService, DiskAnalyzerService>();
        services.AddSingleton<DiskAnalyzerViewModel>();
        services.AddTransient<Views.DiskAnalyzerView>();

        // ===== 大文件扫描（Transient） =====
        services.AddTransient<LargeFileScannerViewModel>();
        services.AddTransient<Views.LargeFileScannerView>();

        // ===== 重复文件查找（Transient） =====
        services.AddSingleton<IDuplicateFinderService, DuplicateFinderService>();
        services.AddTransient<DuplicateFinderViewModel>();
        services.AddTransient<Views.DuplicateFinderView>();

        // ===== 文件强制删除（Singleton ViewModel keeps pending file list until app exit） =====
        services.AddTransient<IFileUnlockService, FileUnlockService>();
        services.AddSingleton<ForceDeleteViewModel>();
        services.AddTransient<ForceDeleteView>();

        // ===== 文件粉碎（Singleton ViewModel keeps pending file list until app exit） =====
        services.AddTransient<IFileShredderService, FileShredderService>();
        services.AddSingleton<FileShredderViewModel>();
        services.AddTransient<FileShredderView>();

        // ===== 批量重命名（Singleton ViewModel keeps file/rule list until app exit） =====
        services.AddSingleton<BatchRenameViewModel>();
        services.AddTransient<BatchRenameView>();

        // ===== 定时关机（Singleton服务 + Transient ViewModel/View） =====
        services.AddSingleton<IShutdownService, ShutdownService>();
        services.AddTransient<ScheduledShutdownViewModel>();
        services.AddTransient<ScheduledShutdownView>();

        // ===== 截屏（Singleton 服务 + Transient ViewModel/View） =====
        services.AddSingleton<ScreenshotService>();
        services.AddTransient<ScreenshotViewModel>();
        services.AddTransient<ScreenshotView>();

        // ===== 取色器（Singleton 服务 + Transient ViewModel/View） =====
        services.AddSingleton<ColorPickerService>();
        services.AddTransient<ColorPickerViewModel>();
        services.AddTransient<ColorPickerView>();

        // ===== 窗口置顶（Transient ViewModel/View） =====
        services.AddTransient<AlwaysOnTopViewModel>();
        services.AddTransient<AlwaysOnTopView>();

        // ===== 任务管理（Singleton 服务 + Transient ViewModel/View） =====
        services.AddSingleton<ITaskManagerService, TaskManagerService>();
        services.AddTransient<TasksViewModel>();
        services.AddTransient<TasksView>();

        // ===== 内存释放（Transient ViewModel/View） =====
        services.AddTransient<MemoryReleaseViewModel>();
        services.AddTransient<MemoryReleaseView>();

        // ===== 设置页面（Transient ViewModel/View） =====
        services.AddTransient<SettingsViewModel>();
        services.AddTransient<SettingsView>();

        // ===== 开机启动项管理（Singleton服务 + Transient ViewModel/View） =====
        services.AddSingleton<IStartupService, StartupService>();
        services.AddTransient<StartupManagerViewModel>();
        services.AddTransient<StartupManagerView>();

        // ===== 右键菜单管理（Singleton服务 + Transient ViewModel/View） =====
        services.AddSingleton<IContextMenuService, ContextMenuService>();
        services.AddTransient<ContextMenuViewModel>();
        services.AddTransient<ContextMenuView>();

        // ===== 注册表清理 + 高级电源选项（Singleton服务 + Transient ViewModel/View） =====
        services.AddSingleton<IRegistryCleanerService, RegistryCleanerService>();
        services.AddTransient<RegistryCleanerViewModel>();
        services.AddTransient<RegistryCleanerView>();

        // ===== AI Agent 检验（Singleton 服务 + Transient ViewModel/View） =====
        services.AddSingleton<IAgentEnvironmentService, AgentEnvironmentService>();
        services.AddSingleton<IAgentReportExportService, AgentReportExportService>();
        services.AddSingleton<IAgentTokenHistoryService, AgentTokenHistoryService>();
        services.AddTransient<AgentInspectorViewModel>();
        services.AddTransient<AgentInspectorView>();

        // ===== ViewModel（Transient — 每次导航创建新实例） =====
        services.AddTransient<MainViewModel>();

        // ===== 窗口（Transient） =====
        services.AddTransient<MainWindow>();

        // 后续功能实现时在此注册对应的 ViewModel 和 View
    }

    /// <summary>
    /// 填充导航项到导航服务
    /// 侧边栏分为三级：⭐ 核心功能 / ✅ 重要功能 / 📦 补充功能
    /// 所有 ViewType 使用占位 object，点击时 DI 解析失败 → MainWindow 显示占位文本
    /// 功能实现后替换为实际的 View Type
    /// </summary>
    private void PopulateNavigation(INavigationService nav)
    {
        bool PopulateCleanNavigation()
        {
            nav.NavItems.Clear();
            const string storage = "\U0001F4BE \u5B58\u50A8\u6E05\u7406";
            const string files = "\U0001F4C1 \u6587\u4EF6\u64CD\u4F5C";
            const string system = "\u2699\uFE0F \u7CFB\u7EDF\u7BA1\u7406";
            const string debug = "\U0001F527 \u8C03\u8BD5\u529F\u80FD";
            const string productivity = "\U0001F3AF \u65E5\u5E38\u6548\u7387";

            nav.NavItems.Add(new NavItem { Name = "C\u76D8\u6E05\u7406", Category = storage, Icon = "\U0001F9F9", ViewType = typeof(Views.CleanupView), RequiresAdmin = false });
            nav.NavItems.Add(new NavItem { Name = "\u78C1\u76D8\u7A7A\u95F4\u5206\u6790", Category = storage, Icon = "\U0001F4CA", ViewType = typeof(Views.DiskAnalyzerView), RequiresAdmin = false, IsPendingOptimization = false });
            nav.NavItems.Add(new NavItem { Name = "\u5927\u6587\u4EF6\u626B\u63CF", Category = storage, Icon = "\U0001F50D", ViewType = typeof(Views.LargeFileScannerView), RequiresAdmin = false });
            nav.NavItems.Add(new NavItem { Name = "\u91CD\u590D\u6587\u4EF6\u67E5\u627E", Category = storage, Icon = "\U0001F4C4", ViewType = typeof(Views.DuplicateFinderView), RequiresAdmin = false });
            nav.NavItems.Add(new NavItem { Name = "\u6587\u4EF6\u5F3A\u5236\u5220\u9664", Category = files, Icon = "\U0001F5D1\uFE0F", ViewType = typeof(Views.ForceDeleteView), RequiresAdmin = false });
            nav.NavItems.Add(new NavItem { Name = "\u6587\u4EF6\u7C89\u788E", Category = files, Icon = "\u2702\uFE0F", ViewType = typeof(Views.FileShredderView), RequiresAdmin = false });
            nav.NavItems.Add(new NavItem { Name = "\u6279\u91CF\u91CD\u547D\u540D", Category = files, Icon = "\U0001F4DD", ViewType = typeof(Views.BatchRenameView), RequiresAdmin = false });
            nav.NavItems.Add(new NavItem { Name = "\u5F00\u673A\u542F\u52A8\u9879\u7BA1\u7406", Category = system, Icon = "\U0001F680", ViewType = typeof(Views.StartupManagerView), RequiresAdmin = false });
            nav.NavItems.Add(new NavItem { Name = "\u53F3\u952E\u83DC\u5355\u7BA1\u7406", Category = system, Icon = "\U0001F5B1\uFE0F", ViewType = typeof(Views.ContextMenuView), RequiresAdmin = false });
            nav.NavItems.Add(new NavItem { Name = "\u6CE8\u518C\u8868\u6E05\u7406", Category = system, Icon = "\U0001F527", ViewType = typeof(Views.RegistryCleanerView), RequiresAdmin = true });
            nav.NavItems.Add(new NavItem { Name = "\u5185\u5B58\u91CA\u653E", Category = system, Icon = "\U0001F4BE", ViewType = typeof(Views.MemoryReleaseView), RequiresAdmin = true });
            nav.NavItems.Add(new NavItem { Name = "\u5B9A\u65F6\u5173\u673A", Category = system, Icon = "\u23F0", ViewType = typeof(Views.ScheduledShutdownView), RequiresAdmin = false });
            nav.NavItems.Add(new NavItem { Name = "AI Agent \u68C0\u9A8C\uFF082.0.0 \u9884\u89C8\uFF09", Category = debug, Icon = "\U0001F9EA", ViewType = typeof(Views.AgentInspectorView), RequiresAdmin = false, IsPendingOptimization = true });
            nav.NavItems.Add(new NavItem { Name = "\u622A\u5C4F\uFF08\u5F85\u4F18\u5316\uFF09", Category = debug, Icon = "\U0001F4F7", ViewType = typeof(Views.ScreenshotView), RequiresAdmin = false, IsPendingOptimization = true });
            nav.NavItems.Add(new NavItem { Name = "\u53D6\u8272\u5668", Category = productivity, Icon = "\U0001F3A8", ViewType = typeof(Views.ColorPickerView), RequiresAdmin = false });
            nav.NavItems.Add(new NavItem { Name = "\u7A97\u53E3\u7F6E\u9876", Category = productivity, Icon = "\U0001F4CC", ViewType = typeof(Views.AlwaysOnTopView), RequiresAdmin = false });
            nav.NavItems.Add(new NavItem { Name = "\u4EFB\u52A1\uFF08\u5F85\u4F18\u5316\uFF09", Category = debug, Icon = "\U0001F4CB", ViewType = typeof(Views.TasksView), RequiresAdmin = false, IsPendingOptimization = true });
            return true;
        }

        if (PopulateCleanNavigation())
            return;
        // 💾 存储清理 — 磁盘空间释放相关
        nav.NavItems.Add(new NavItem
        {
            Name = "C盘清理",
            Category = "💾 存储清理",
            Icon = "🧹",
            ViewType = typeof(Views.CleanupView),
            RequiresAdmin = false
        });
        nav.NavItems.Add(new NavItem
        {
            Name = "磁盘空间分析",
            Category = "💾 存储清理",
            Icon = "📊",
            ViewType = typeof(Views.DiskAnalyzerView),
            RequiresAdmin = false,
            IsPendingOptimization = false
        });
        nav.NavItems.Add(new NavItem
        {
            Name = "大文件扫描",
            Category = "💾 存储清理",
            Icon = "🔍",
            ViewType = typeof(Views.LargeFileScannerView),
            RequiresAdmin = false
        });
        nav.NavItems.Add(new NavItem
        {
            Name = "重复文件查找",
            Category = "💾 存储清理",
            Icon = "📄",
            ViewType = typeof(Views.DuplicateFinderView),
            RequiresAdmin = false
        });

        // 📁 文件操作 — 文件管理相关
        nav.NavItems.Add(new NavItem
        {
            Name = "文件强制删除",
            Category = "📁 文件操作",
            Icon = "🗑️",
            ViewType = typeof(Views.ForceDeleteView),
            RequiresAdmin = false
        });
        nav.NavItems.Add(new NavItem
        {
            Name = "文件粉碎",
            Category = "📁 文件操作",
            Icon = "✂️",
            ViewType = typeof(Views.FileShredderView),
            RequiresAdmin = false
        });
        nav.NavItems.Add(new NavItem
        {
            Name = "批量重命名",
            Category = "📁 文件操作",
            Icon = "📝",
            ViewType = typeof(Views.BatchRenameView),
            RequiresAdmin = false
        });

        // ⚙️ 系统管理 — 系统设置与维护
        nav.NavItems.Add(new NavItem
        {
            Name = "开机启动项管理",
            Category = "⚙️ 系统管理",
            Icon = "🚀",
            ViewType = typeof(Views.StartupManagerView),
            RequiresAdmin = false
        });
        nav.NavItems.Add(new NavItem
        {
            Name = "右键菜单管理",
            Category = "⚙️ 系统管理",
            Icon = "🖱️",
            ViewType = typeof(Views.ContextMenuView),
            RequiresAdmin = false
        });
        nav.NavItems.Add(new NavItem
        {
            Name = "注册表清理",
            Category = "⚙️ 系统管理",
            Icon = "🔧",
            ViewType = typeof(Views.RegistryCleanerView),
            RequiresAdmin = true
        });
        nav.NavItems.Add(new NavItem
        {
            Name = "内存释放",
            Category = "⚙️ 系统管理",
            Icon = "💾",
            ViewType = typeof(Views.MemoryReleaseView),
            RequiresAdmin = true
        });
        nav.NavItems.Add(new NavItem
        {
            Name = "定时关机",
            Category = "⚙️ 系统管理",
            Icon = "⏰",
            ViewType = typeof(Views.ScheduledShutdownView),
            RequiresAdmin = false
        });

        // 🔧 调试功能 — 2.0.0 预览功能，1.0.4 正式版默认不开放
        nav.NavItems.Add(new NavItem
        {
            Name = "AI Agent 检验（2.0.0 预览）",
            Category = "🔧 调试功能",
            Icon = "🧪",
            ViewType = typeof(Views.AgentInspectorView),
            RequiresAdmin = false,
            IsPendingOptimization = true
        });

        // 🎯 日常效率 — 日常使用的高频小工具
        nav.NavItems.Add(new NavItem
        {
            Name = "截屏（待优化）",
            Category = "🔧 调试功能",
            Icon = "📷",
            ViewType = typeof(Views.ScreenshotView),
            RequiresAdmin = false,
            IsPendingOptimization = true
        });
        nav.NavItems.Add(new NavItem
        {
            Name = "取色器",
            Category = "🎯 日常效率",
            Icon = "🎨",
            ViewType = typeof(Views.ColorPickerView),
            RequiresAdmin = false
        });
        nav.NavItems.Add(new NavItem
        {
            Name = "窗口置顶",
            Category = "🎯 日常效率",
            Icon = "📌",
            ViewType = typeof(Views.AlwaysOnTopView),
            RequiresAdmin = false
        });
        nav.NavItems.Add(new NavItem
        {
            Name = "任务（待优化）",
            Category = "🔧 调试功能",
            Icon = "📋",
            ViewType = typeof(Views.TasksView),
            RequiresAdmin = false,
            IsPendingOptimization = true
        });
    }

    /// <summary>
    /// 全局 UI 线程未处理异常捕获，防止闪退并记录错误
    /// </summary>
    private void OnDispatcherUnhandledException(object sender, System.Windows.Threading.DispatcherUnhandledExceptionEventArgs e)
    {
        var log = ServiceProvider?.GetService<ILogService>();
        log?.LogError("未处理的UI异常", e.Exception);

        MessageBox.Show(
            $"发生未处理的错误:\n\n{e.Exception.GetType().Name}: {e.Exception.Message}\n\n" +
            $"StackTrace:\n{e.Exception.StackTrace}\n\n" +
            "应用将继续运行，但可能不稳定。",
            "错误",
            MessageBoxButton.OK,
            MessageBoxImage.Error);

        e.Handled = true; // 阻止进程退出
    }
}
