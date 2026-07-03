using CommunityToolkit.Mvvm.ComponentModel;

namespace Toolbox.Models;

/// <summary>
/// 重命名规则模型
/// 支持6种重命名规则类型，可多规则组合使用
/// </summary>
public partial class RenameRule : ObservableObject
{
    /// <summary>规则类型</summary>
    [ObservableProperty]
    private RenameRuleType _ruleType = RenameRuleType.Prefix;

    /// <summary>规则参数（如前缀文本、查找文本、正则表达式等）</summary>
    [ObservableProperty]
    private string _parameter = "";

    /// <summary>第二参数（如替换文本）</summary>
    [ObservableProperty]
    private string _parameter2 = "";

    /// <summary>规则描述（只读UI显示）</summary>
    public string Description => RuleType switch
    {
        RenameRuleType.Prefix => $"添加前缀: \"{Parameter}\"",
        RenameRuleType.Suffix => $"添加后缀: \"{Parameter}\"",
        RenameRuleType.FindReplace => $"替换: \"{Parameter}\" → \"{Parameter2}\"",
        RenameRuleType.Numbering => $"序号: 起始{Parameter}, 步长{Parameter2}",
        RenameRuleType.RemoveChars => $"移除字符: 从{Parameter}位起{Parameter2}个字符",
        RenameRuleType.Case => $"大小写: {Parameter}",
        RenameRuleType.Regex => $"正则: 匹配\"{Parameter}\" → 替换\"{Parameter2}\"",
        RenameRuleType.Template => $"整体重命名: \"{Parameter}\"",
        RenameRuleType.Extension => $"格式/扩展名: {Parameter}",
        _ => ""
    };
}

/// <summary>
/// 重命名规则类型枚举
/// </summary>
public enum RenameRuleType
{
    /// <summary>添加前缀</summary>
    Prefix,
    /// <summary>添加后缀（在扩展名之前）</summary>
    Suffix,
    /// <summary>查找并替换文本</summary>
    FindReplace,
    /// <summary>添加序号（001, 002, ...）</summary>
    Numbering,
    /// <summary>移除指定位置的字符</summary>
    RemoveChars,
    /// <summary>更改大小写（大写/小写/首字母大写）</summary>
    Case,
    /// <summary>正则表达式查找替换</summary>
    Regex,
    /// <summary>整体模板重命名，支持 {name}, {n}, {nn}, {date}</summary>
    Template,
    /// <summary>更改文件扩展名或格式</summary>
    Extension
}
