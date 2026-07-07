using Microsoft.Extensions.DependencyInjection;
using System.Windows;
using Toolbox.Models;
using Toolbox.Services;
using Toolbox.ViewModels;
using Toolbox.Views;

namespace Toolbox;

public partial class App : Application
{
    public static IServiceProvider ServiceProvider { get; private set; } = null!;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        if (!CheckDotNetRuntime())
        {
            Shutdown();
            return;
        }

        var tempServices = new ServiceCollection();
        tempServices.AddSingleton<ISingleInstanceService, SingleInstanceService>();
        using var tempProvider = tempServices.BuildServiceProvider();
        if (!tempProvider.GetRequiredService<ISingleInstanceService>().IsFirstInstance())
        {
            Shutdown();
            return;
        }

        var services = new ServiceCollection();
        ConfigureServices(services);
        ServiceProvider = services.BuildServiceProvider();

        var config = ServiceProvider.GetRequiredService<IConfigService>();
        var settings = config.LoadConfig<AppSettings>(config.SettingsFilePath) ?? new AppSettings();

        var theme = ServiceProvider.GetRequiredService<IThemeService>();
        theme.SetTheme(ThemeService.ParseTheme(settings.Theme));

        var nav = ServiceProvider.GetRequiredService<INavigationService>();
        PopulateNavigation(nav);
        if (nav.NavItems.FirstOrDefault() is { } firstItem)
            nav.SelectedItem = firstItem;

        var logger = ServiceProvider.GetRequiredService<ILogService>();
        logger.LogInfo("Toolbox started");

        // 任务提醒服务必须随应用启动，不能依赖用户先进入“任务”页面。
        _ = ServiceProvider.GetRequiredService<ITaskManagerService>();

        var mainWindow = ServiceProvider.GetRequiredService<MainWindow>();
        mainWindow.Show();
    }

    private bool CheckDotNetRuntime()
    {
        if (Environment.Version.Major >= 8)
            return true;

        var result = MessageBox.Show(
            "需要 .NET 8.0 桌面运行时才能运行工具箱。\n\n是否打开微软下载页面？",
            ".NET 运行时未安装",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (result == MessageBoxResult.Yes)
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "https://dotnet.microsoft.com/download/dotnet/8.0",
                    UseShellExecute = true
                });
            }
            catch
            {
                // ignored
            }
        }

        return false;
    }

    private static void ConfigureServices(IServiceCollection services)
    {
        services.AddSingleton<ILogService, LogService>();
        services.AddSingleton<IConfigService, ConfigService>();
        services.AddSingleton<INavigationService, NavigationService>();
        services.AddSingleton<IThemeService, ThemeService>();
        services.AddSingleton<ISingleInstanceService, SingleInstanceService>();

        services.AddSingleton<ICleanupService, CleanupService>();
        services.AddSingleton<CleanupViewModel>();
        services.AddTransient<CleanupView>();

        services.AddSingleton<IDiskAnalyzerService, DiskAnalyzerService>();
        services.AddSingleton<DiskAnalyzerViewModel>();
        services.AddTransient<DiskAnalyzerView>();

        services.AddTransient<LargeFileScannerViewModel>();
        services.AddTransient<LargeFileScannerView>();

        services.AddSingleton<IDuplicateFinderService, DuplicateFinderService>();
        services.AddTransient<DuplicateFinderViewModel>();
        services.AddTransient<DuplicateFinderView>();

        services.AddTransient<IFileUnlockService, FileUnlockService>();
        services.AddSingleton<ForceDeleteViewModel>();
        services.AddTransient<ForceDeleteView>();

        services.AddTransient<IFileShredderService, FileShredderService>();
        services.AddSingleton<FileShredderViewModel>();
        services.AddTransient<FileShredderView>();

        services.AddSingleton<BatchRenameViewModel>();
        services.AddTransient<BatchRenameView>();

        services.AddSingleton<IShutdownService, ShutdownService>();
        services.AddTransient<ScheduledShutdownViewModel>();
        services.AddTransient<ScheduledShutdownView>();

        services.AddSingleton<ScreenshotService>();
        services.AddSingleton<ScreenshotViewModel>();
        services.AddTransient<ScreenshotView>();

        services.AddSingleton<ColorPickerService>();
        services.AddSingleton<ColorPickerViewModel>();
        services.AddTransient<ColorPickerView>();

        services.AddTransient<AlwaysOnTopViewModel>();
        services.AddTransient<AlwaysOnTopView>();

        services.AddSingleton<ITaskManagerService, TaskManagerService>();
        services.AddSingleton<TasksViewModel>();
        services.AddTransient<TasksView>();

        services.AddTransient<MemoryReleaseViewModel>();
        services.AddTransient<MemoryReleaseView>();

        services.AddTransient<SettingsViewModel>();
        services.AddTransient<SettingsView>();

        services.AddSingleton<IStartupService, StartupService>();
        services.AddTransient<StartupManagerViewModel>();
        services.AddTransient<StartupManagerView>();

        services.AddSingleton<IContextMenuService, ContextMenuService>();
        services.AddTransient<ContextMenuViewModel>();
        services.AddTransient<ContextMenuView>();

        services.AddSingleton<IRegistryCleanerService, RegistryCleanerService>();
        services.AddTransient<RegistryCleanerViewModel>();
        services.AddTransient<RegistryCleanerView>();

        services.AddSingleton<IAgentEnvironmentService, AgentEnvironmentService>();
        services.AddSingleton<IAgentReportExportService, AgentReportExportService>();
        services.AddSingleton<IAgentTokenHistoryService, AgentTokenHistoryService>();
        services.AddSingleton<IAgentTokenUsageApiClient, AgentTokenUsageApiClient>();
        services.AddTransient<AgentInspectorViewModel>();
        services.AddTransient<AgentInspectorView>();

        services.AddTransient<MainViewModel>();
        services.AddTransient<MainWindow>();
    }

    private static void PopulateNavigation(INavigationService nav)
    {
        nav.NavItems.Clear();

        const string storage = "💾 存储清理";
        const string files = "📁 文件操作";
        const string system = "⚙️ 系统管理";
        const string productivity = "🎯 日常效率";

        nav.NavItems.Add(new NavItem { Name = "C盘清理", Category = storage, Icon = "🧹", ViewType = typeof(CleanupView), RequiresAdmin = false });
        nav.NavItems.Add(new NavItem { Name = "磁盘空间分析", Category = storage, Icon = "📊", ViewType = typeof(DiskAnalyzerView), RequiresAdmin = false });
        nav.NavItems.Add(new NavItem { Name = "大文件扫描", Category = storage, Icon = "🔍", ViewType = typeof(LargeFileScannerView), RequiresAdmin = false });
        nav.NavItems.Add(new NavItem { Name = "重复文件查找", Category = storage, Icon = "📄", ViewType = typeof(DuplicateFinderView), RequiresAdmin = false });

        nav.NavItems.Add(new NavItem { Name = "文件强制删除", Category = files, Icon = "🗑️", ViewType = typeof(ForceDeleteView), RequiresAdmin = false });
        nav.NavItems.Add(new NavItem { Name = "文件粉碎", Category = files, Icon = "✂️", ViewType = typeof(FileShredderView), RequiresAdmin = false });
        nav.NavItems.Add(new NavItem { Name = "批量重命名", Category = files, Icon = "📝", ViewType = typeof(BatchRenameView), RequiresAdmin = false });

        nav.NavItems.Add(new NavItem { Name = "开机启动项管理", Category = system, Icon = "🚀", ViewType = typeof(StartupManagerView), RequiresAdmin = false });
        nav.NavItems.Add(new NavItem { Name = "右键菜单管理", Category = system, Icon = "🖱️", ViewType = typeof(ContextMenuView), RequiresAdmin = false });
        nav.NavItems.Add(new NavItem { Name = "注册表清理", Category = system, Icon = "🔧", ViewType = typeof(RegistryCleanerView), RequiresAdmin = true });
        nav.NavItems.Add(new NavItem { Name = "内存释放", Category = system, Icon = "💾", ViewType = typeof(MemoryReleaseView), RequiresAdmin = true });
        nav.NavItems.Add(new NavItem { Name = "定时关机", Category = system, Icon = "⏰", ViewType = typeof(ScheduledShutdownView), RequiresAdmin = false });

        nav.NavItems.Add(new NavItem { Name = "任务", Category = productivity, Icon = "📋", ViewType = typeof(TasksView), RequiresAdmin = false });
        nav.NavItems.Add(new NavItem { Name = "取色器", Category = productivity, Icon = "🎨", ViewType = typeof(ColorPickerView), RequiresAdmin = false });
        nav.NavItems.Add(new NavItem { Name = "窗口置顶", Category = productivity, Icon = "📌", ViewType = typeof(AlwaysOnTopView), RequiresAdmin = false });

        nav.NavItems.Add(new NavItem { Name = "AI Agent 检验", Category = productivity, Icon = "🧪", ViewType = typeof(AgentInspectorView), RequiresAdmin = false });
        nav.NavItems.Add(new NavItem { Name = "截屏", Category = productivity, Icon = "📷", ViewType = typeof(ScreenshotView), RequiresAdmin = false });
    }

    private void OnDispatcherUnhandledException(object sender, System.Windows.Threading.DispatcherUnhandledExceptionEventArgs e)
    {
        var log = ServiceProvider?.GetService<ILogService>();
        log?.LogError("Unhandled UI exception", e.Exception);

        MessageBox.Show(
            $"程序遇到未处理错误：{e.Exception.Message}",
            "错误",
            MessageBoxButton.OK,
            MessageBoxImage.Error);

        e.Handled = true;
    }
}
