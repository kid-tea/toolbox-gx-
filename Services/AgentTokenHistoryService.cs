using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using Toolbox.Models;

namespace Toolbox.Services;

public sealed class AgentTokenHistoryService : IAgentTokenHistoryService
{
    private const int RetentionDays = 30;
    private readonly string _historyPath;
    private readonly JsonSerializerOptions _jsonOptions = new() { WriteIndented = true };

    public AgentTokenHistoryService()
        : this(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Toolbox",
            "agent-token-history.json"))
    {
    }

    public AgentTokenHistoryService(string historyPath)
    {
        _historyPath = historyPath;
    }

    public IReadOnlyList<AgentDailyTokenHistoryRecord> LoadHistory(DateTime today)
    {
        var records = ReadAll();
        var pruned = Prune(records, today);
        if (pruned.Count != records.Count)
            WriteAll(pruned);
        return pruned;
    }

    public IReadOnlyList<AgentDailyTokenHistoryRecord> SaveFromRecords(IEnumerable<AgentTokenUsageRecord> records, DateTime today)
    {
        var merged = LoadHistory(today)
            .ToDictionary(record => record.Date.Date, record => record);

        foreach (var dailyRecord in BuildDailyRecords(records))
            merged[dailyRecord.Date.Date] = dailyRecord;

        var pruned = Prune(merged.Values, today);
        WriteAll(pruned);
        return pruned;
    }

    private static List<AgentDailyTokenHistoryRecord> BuildDailyRecords(IEnumerable<AgentTokenUsageRecord> records)
    {
        return records
            .GroupBy(record => record.Date.ToLocalTime().Date)
            .Select(group =>
            {
                var daily = new AgentDailyTokenHistoryRecord
                {
                    Date = group.Key,
                    TotalTokens = group.Sum(record => record.Tokens),
                    CallCount = group.Sum(GetCallCount),
                    InputTokens = group.Sum(record => record.InputTokens),
                    CachedInputTokens = group.Sum(record => record.CachedInputTokens)
                };

                daily.TotalTokensText = FormatCompactTokens(daily.TotalTokens);
                daily.CacheHitRateText = FormatCacheHitRate(daily.InputTokens, daily.CachedInputTokens);

                foreach (var summary in group
                    .GroupBy(record => new { record.Agent, record.Model })
                    .Select(modelGroup =>
                    {
                        var tokens = modelGroup.Sum(record => record.Tokens);
                        var inputTokens = modelGroup.Sum(record => record.InputTokens);
                        var cachedInputTokens = modelGroup.Sum(record => record.CachedInputTokens);
                        return new AgentTokenModelSummary
                        {
                            Agent = modelGroup.Key.Agent,
                            Model = modelGroup.Key.Model,
                            Tokens = tokens,
                            TokensText = FormatCompactTokens(tokens),
                            InputTokens = inputTokens,
                            CachedInputTokens = cachedInputTokens,
                            CallCount = 0,
                            CallCountText = "暂无精确数据",
                            CacheHitRateText = FormatCacheHitRate(inputTokens, cachedInputTokens)
                        };
                    })
                    .OrderByDescending(summary => summary.Tokens))
                {
                    daily.ModelSummaries.Add(summary);
                }

                return daily;
            })
            .OrderByDescending(record => record.Date)
            .ToList();
    }

    private List<AgentDailyTokenHistoryRecord> ReadAll()
    {
        try
        {
            if (!File.Exists(_historyPath))
                return [];

            var text = File.ReadAllText(_historyPath);
            return JsonSerializer.Deserialize<List<AgentDailyTokenHistoryRecord>>(text, _jsonOptions) ?? [];
        }
        catch
        {
            return [];
        }
    }

    private void WriteAll(IReadOnlyList<AgentDailyTokenHistoryRecord> records)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_historyPath)!);
        File.WriteAllText(_historyPath, JsonSerializer.Serialize(records, _jsonOptions));
    }

    private static List<AgentDailyTokenHistoryRecord> Prune(IEnumerable<AgentDailyTokenHistoryRecord> records, DateTime today)
    {
        var cutoff = today.Date.AddDays(-(RetentionDays - 1));
        return records
            .Where(record => record.Date.Date >= cutoff && record.Date.Date <= today.Date)
            .OrderByDescending(record => record.Date)
            .ToList();
    }

    private static int GetCallCount(AgentTokenUsageRecord record)
    {
        if (record.SessionCount > 0) return record.SessionCount;
        if (record.MessageCount > 0) return record.MessageCount;
        return record.Tokens > 0 ? 1 : 0;
    }

    private static string FormatCompactTokens(long value)
    {
        return value switch
        {
            >= 1_000_000_000 => $"{value / 1_000_000_000d:0.##}B",
            >= 1_000_000 => $"{value / 1_000_000d:0.##}M",
            >= 1_000 => $"{value / 1_000d:0.##}K",
            _ => value.ToString("N0")
        };
    }

    private static string FormatCacheHitRate(long inputTokens, long cachedInputTokens)
    {
        if (inputTokens <= 0)
            return "暂无数据";

        var clampedCachedTokens = Math.Clamp(cachedInputTokens, 0, inputTokens);
        return $"{clampedCachedTokens * 100d / inputTokens:0.0}%";
    }
}
