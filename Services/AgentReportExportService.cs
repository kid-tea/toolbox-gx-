using System.Text;
using Toolbox.Models;

namespace Toolbox.Services;

public sealed class AgentReportExportService : IAgentReportExportService
{
    public string BuildMarkdown(AgentScanReport report, AgentReportPrivacyOptions privacyOptions)
    {
        var builder = new StringBuilder();
        builder.AppendLine("# AI Agent 检验报告");
        builder.AppendLine();
        builder.AppendLine($"- 扫描时间：{report.ScannedAt:yyyy-MM-dd HH:mm:ss}");
        builder.AppendLine($"- 项目根目录：{FormatPath(report.ProjectRoot, privacyOptions)}");
        builder.AppendLine($"- 检测 Agent：{report.Environments.Count(e => e.IsDetected)} / {report.Environments.Count}");
        builder.AppendLine($"- Skills：{report.Skills.Count}");
        builder.AppendLine($"- 本地 Token：{report.TokenSummary.TotalTokens:N0}");
        builder.AppendLine($"- 估算费用：{FormatCost(report.TokenSummary.EstimatedCostUsd)}");
        builder.AppendLine();

        builder.AppendLine("## 环境概览");
        foreach (var environment in report.Environments)
        {
            builder.AppendLine($"- {environment.DisplayName}：{FormatLevel(environment.Level)} / {environment.Status} / {environment.Detail}");
        }
        builder.AppendLine();

        builder.AppendLine("## Token 消耗");
        builder.AppendLine($"- 今日：{report.TokenSummary.TodayTokens:N0}");
        builder.AppendLine($"- 本周：{report.TokenSummary.ThisWeekTokens:N0}");
        builder.AppendLine($"- 本月：{report.TokenSummary.ThisMonthTokens:N0}");
        builder.AppendLine($"- 总计：{report.TokenSummary.TotalTokens:N0}");
        builder.AppendLine("- 说明：费用为本地价格表估算，不代表 API 账单。");
        builder.AppendLine();

        builder.AppendLine("## Skills / 项目规则");
        foreach (var group in report.Skills.GroupBy(s => s.Agent).OrderBy(g => g.Key.ToString()))
        {
            builder.AppendLine($"- {FormatAgent(group.Key)} Skills：{group.Count()}");
        }
        foreach (var rule in report.ProjectRules)
        {
            builder.AppendLine($"- {rule.Name}：{(rule.Exists ? "已发现" : "未发现")} / {rule.Detail}");
        }
        builder.AppendLine();

        builder.AppendLine("## 注意 / 风险项");
        var findings = report.Findings
            .Where(f => f.Level is AgentFindingLevel.Attention or AgentFindingLevel.Risk or AgentFindingLevel.Unsupported)
            .ToList();
        if (findings.Count == 0)
        {
            builder.AppendLine("- 未发现需要处理的注意项。");
        }
        else
        {
            foreach (var finding in findings)
                builder.AppendLine($"- [{FormatLevel(finding.Level)}] {FormatAgent(finding.Kind)}：{finding.Title} - {finding.Message}");
        }

        builder.AppendLine();
        builder.AppendLine("## 隐私说明");
        builder.AppendLine("- 报告不包含聊天正文。");
        builder.AppendLine("- 报告不包含 API Key 或 auth.json 内容。");
        builder.AppendLine("- 默认隐藏完整用户目录路径。");

        return builder.ToString();
    }

    private static string FormatPath(string path, AgentReportPrivacyOptions privacyOptions)
    {
        if (string.IsNullOrWhiteSpace(path)) return "未提供";
        if (privacyOptions.IncludeFullPaths) return path;
        return "已隐藏";
    }

    private static string FormatCost(decimal? cost)
    {
        return cost.HasValue ? $"约 ${cost.Value:0.0000}（估算）" : "无法估算";
    }

    private static string FormatLevel(AgentFindingLevel level) => level switch
    {
        AgentFindingLevel.Normal => "正常",
        AgentFindingLevel.Attention => "注意",
        AgentFindingLevel.Risk => "风险",
        AgentFindingLevel.Unsupported => "不支持",
        _ => level.ToString()
    };

    private static string FormatAgent(AgentToolKind kind) => kind switch
    {
        AgentToolKind.Codex => "Codex",
        AgentToolKind.ClaudeCode => "Claude Code",
        AgentToolKind.Gemini => "Gemini",
        AgentToolKind.Cursor => "Cursor",
        AgentToolKind.Continue => "Continue",
        AgentToolKind.ClineRoo => "Cline / Roo",
        _ => kind.ToString()
    };
}
