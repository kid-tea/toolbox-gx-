using System.Collections.ObjectModel;

namespace Toolbox.Models;

public enum AgentToolKind
{
    Codex,
    ClaudeCode,
    Gemini,
    Cursor,
    Continue,
    ClineRoo
}

public enum AgentFindingLevel
{
    Normal,
    Attention,
    Risk,
    Unsupported
}

public enum AgentDataProvenance
{
    LocalConfig,
    LocalSkills,
    LocalTokenCache,
    LocalEstimate,
    RemoteUsageApi,
    Unsupported
}

public enum AgentTokenSourceKind
{
    Local,
    Api
}

public sealed class AgentScanOptions
{
    public bool IncludeCodex { get; init; } = true;
    public bool IncludeClaudeCode { get; init; } = true;
    public bool IncludeOtherAgentsShallowCheck { get; init; } = true;
    public bool IncludeTokenUsage { get; init; } = true;
    public bool IncludeProjectRules { get; init; } = true;
    public string ProjectRoot { get; init; } = @"D:\codex\探索使用1";
    public AgentTokenSourceKind TokenSource { get; init; } = AgentTokenSourceKind.Local;
    public string ApiProvider { get; init; } = "OpenAI";
    public string ApiKey { get; init; } = "";
    public string ApiModel { get; init; } = "";
    public string UserProfilePath { get; init; } =
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    public string AppDataRoamingPath { get; init; } =
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
    public DateTimeOffset Now { get; init; } = DateTimeOffset.Now;
}

public sealed class AgentReportPrivacyOptions
{
    public bool IncludeFullPaths { get; init; }
    public bool IncludeSessionTitles { get; init; }
    public bool IncludeMessageContent { get; init; }
}

public sealed class AgentScanReport
{
    public DateTimeOffset ScannedAt { get; set; } = DateTimeOffset.Now;
    public string ProjectRoot { get; set; } = "";
    public Collection<AgentEnvironmentItem> Environments { get; } = new();
    public Collection<AgentSkillItem> Skills { get; } = new();
    public Collection<AgentProjectRuleItem> ProjectRules { get; } = new();
    public Collection<AgentTokenUsageRecord> TokenRecords { get; } = new();
    public AgentTokenUsageSummary TokenSummary { get; set; } = new();
    public Collection<AgentFinding> Findings { get; } = new();
}

public sealed class AgentEnvironmentItem
{
    public AgentToolKind Kind { get; set; }
    public string DisplayName { get; set; } = "";
    public string RootPath { get; set; } = "";
    public bool IsDetected { get; set; }
    public AgentFindingLevel Level { get; set; } = AgentFindingLevel.Unsupported;
    public AgentDataProvenance Provenance { get; set; } = AgentDataProvenance.Unsupported;
    public string Status { get; set; } = "";
    public string Detail { get; set; } = "";
    public DateTimeOffset? LastWriteTime { get; set; }
}

public sealed class AgentSkillItem
{
    public AgentToolKind Agent { get; set; }
    public string Name { get; set; } = "";
    public string Source { get; set; } = "";
    public string PluginName { get; set; } = "";
    public string Description { get; set; } = "";
    public string DescriptionEnglish { get; set; } = "";
    public string DescriptionChinese { get; set; } = "";
    public string Path { get; set; } = "";
    public bool IsUserEditable { get; set; }
    public AgentDataProvenance Provenance { get; set; } = AgentDataProvenance.LocalSkills;
    public DateTimeOffset? LastWriteTime { get; set; }
}

public sealed class AgentSkillAgentGroup
{
    public AgentToolKind Agent { get; set; }
    public string DisplayName { get; set; } = "";
    public Collection<AgentSkillPluginGroup> PluginGroups { get; } = new();
}

public sealed class AgentSkillPluginGroup
{
    public string PluginName { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public Collection<AgentSkillItem> Skills { get; } = new();
}

public sealed class AgentProjectRuleItem
{
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";
    public bool Exists { get; set; }
    public bool IsReadable { get; set; }
    public long SizeBytes { get; set; }
    public DateTimeOffset? LastWriteTime { get; set; }
    public AgentFindingLevel Level { get; set; } = AgentFindingLevel.Unsupported;
    public string Detail { get; set; } = "";
}

public sealed class AgentTokenUsageRecord
{
    public AgentToolKind Agent { get; set; }
    public string Model { get; set; } = "unknown";
    public DateTimeOffset Date { get; set; }
    public long Tokens { get; set; }
    public long InputTokens { get; set; }
    public long CachedInputTokens { get; set; }
    public long CacheWriteInputTokens { get; set; }
    public long OutputTokens { get; set; }
    public long ReasoningOutputTokens { get; set; }
    public int MessageCount { get; set; }
    public int SessionCount { get; set; }
    public int ToolCallCount { get; set; }
    public string ConversationId { get; set; } = "";
    public string ConversationTitle { get; set; } = "";
    public string ProjectPath { get; set; } = "";
    public AgentDataProvenance Provenance { get; set; } = AgentDataProvenance.LocalTokenCache;
    public decimal? EstimatedCostUsd { get; set; }
}

public sealed class AgentTokenUsageApiOptions
{
    public string Provider { get; init; } = "OpenAI";
    public string ApiKey { get; init; } = "";
    public string Model { get; init; } = "";
    public DateTimeOffset Start { get; init; }
    public DateTimeOffset End { get; init; }
    public AgentToolKind Agent { get; init; } = AgentToolKind.Codex;
}

public sealed class AgentTokenConversationSummary
{
    public AgentToolKind Agent { get; set; }
    public string Model { get; set; } = "unknown";
    public DateTimeOffset Date { get; set; }
    public string ConversationId { get; set; } = "";
    public string ConversationTitle { get; set; } = "";
    public long Tokens { get; set; }
    public long InputTokens { get; set; }
    public long CachedInputTokens { get; set; }
    public long OutputTokens { get; set; }
    public string TokensText { get; set; } = "0";
    public Collection<AgentTokenUsageRecord> Requests { get; } = new();
}

public sealed class AgentTokenModelSummary
{
    public AgentToolKind Agent { get; set; }
    public string Model { get; set; } = "unknown";
    public long Tokens { get; set; }
    public string TokensText { get; set; } = "0";
    public long InputTokens { get; set; }
    public long CachedInputTokens { get; set; }
    public int CallCount { get; set; }
    public string CallCountText { get; set; } = "暂无精确数据";
    public string CacheHitRateText { get; set; } = "暂无数据";
}

public sealed class AgentDailyTokenHistoryRecord
{
    public DateTime Date { get; set; }
    public long TotalTokens { get; set; }
    public string TotalTokensText { get; set; } = "0";
    public int CallCount { get; set; }
    public long InputTokens { get; set; }
    public long CachedInputTokens { get; set; }
    public string CacheHitRateText { get; set; } = "暂无数据";
    public Collection<AgentTokenModelSummary> ModelSummaries { get; set; } = new();
}

public sealed class AgentTokenUsageSummary
{
    public long TodayTokens { get; set; }
    public long ThisWeekTokens { get; set; }
    public long ThisMonthTokens { get; set; }
    public long TotalTokens { get; set; }
    public int SessionCount { get; set; }
    public int MessageCount { get; set; }
    public int ToolCallCount { get; set; }
    public decimal? EstimatedCostUsd { get; set; }
}

public sealed class AgentFinding
{
    public AgentToolKind Kind { get; set; }
    public AgentFindingLevel Level { get; set; }
    public string Title { get; set; } = "";
    public string Message { get; set; } = "";
    public AgentDataProvenance Provenance { get; set; } = AgentDataProvenance.LocalConfig;
}
