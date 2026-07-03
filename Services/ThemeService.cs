using System.Windows;

namespace Toolbox.Services;

/// <summary>主题类型枚举</summary>
public enum ThemeType
{
    /// <summary>亮色主题</summary>
    Light,

    /// <summary>暗色主题</summary>
    Dark
}

/// <summary>
/// 主题服务接口
/// </summary>
public interface IThemeService
{
    /// <summary>当前主题</summary>
    ThemeType CurrentTheme { get; }

    /// <summary>设置主题</summary>
    void SetTheme(ThemeType theme);

    /// <summary>主题变化事件</summary>
    event Action<ThemeType>? ThemeChanged;
}

/// <summary>
/// 主题服务实现
/// 负责亮色/暗色主题切换，通过 ResourceDictionary 动态加载主题
/// 使用 pack URI 加载 Resources 目录下的主题 XAML 文件
/// </summary>
public class ThemeService : IThemeService
{
    /// <summary>当前主题，默认亮色</summary>
    public ThemeType CurrentTheme { get; private set; } = ThemeType.Light;

    /// <summary>主题切换事件</summary>
    public event Action<ThemeType>? ThemeChanged;

    /// <summary>
    /// 设置主题
    /// </summary>
    /// <param name="theme">目标主题类型</param>
    public void SetTheme(ThemeType theme)
    {
        CurrentTheme = theme;
        ThemeChanged?.Invoke(theme);
        ApplyTheme(theme);
    }

    /// <summary>
    /// 应用主题到应用程序资源
    /// 清空现有 MergedDictionaries 并加载新主题
    /// </summary>
    /// <param name="theme">目标主题类型</param>
    private void ApplyTheme(ThemeType theme)
    {
        var app = Application.Current;
        if (app == null) return;

        // 清空现有主题资源
        app.Resources.MergedDictionaries.Clear();

        // 根据主题类型加载对应的 ResourceDictionary
        var themeDict = new ResourceDictionary
        {
            Source = new Uri(theme == ThemeType.Dark
                ? "pack://application:,,,/Resources/DarkTheme.xaml"
                : "pack://application:,,,/Resources/LightTheme.xaml")
        };
        app.Resources.MergedDictionaries.Add(themeDict);
    }
}
