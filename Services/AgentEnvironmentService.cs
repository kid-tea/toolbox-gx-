using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Toolbox.Models;

namespace Toolbox.Services;

public sealed class AgentEnvironmentService : IAgentEnvironmentService
{
    private static readonly string[] ProjectRuleNames = ["AGENTS.md", "CLAUDE.md", ".codex", ".claude"];
    private const long LargeRolloutBaselineTokenThreshold = 5_000_000;
    private const long MaxPlausibleSingleTokenRecord = 50_000_000;
    private const int SnapshotReplayMinUsageEvents = 50;
    private static readonly TimeSpan SnapshotReplayMaxSpan = TimeSpan.FromSeconds(3);
    private readonly IAgentTokenUsageApiClient _apiClient;

    public AgentEnvironmentService()
        : this(new AgentTokenUsageApiClient())
    {
    }

    public AgentEnvironmentService(IAgentTokenUsageApiClient apiClient)
    {
        _apiClient = apiClient;
    }

    public async Task<AgentScanReport> ScanAsync(AgentScanOptions options, CancellationToken cancellationToken = default)
    {
        return await Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();

            var report = new AgentScanReport
            {
                ScannedAt = options.Now,
                ProjectRoot = options.ProjectRoot
            };

            if (options.IncludeCodex)
                ScanCodex(options, report, cancellationToken);

            if (options.IncludeClaudeCode)
                ScanClaudeCode(options, report, cancellationToken);

            if (options.IncludeTokenUsage && options.TokenSource == AgentTokenSourceKind.Api)
                AppendApiTokenRecords(options, report, cancellationToken).GetAwaiter().GetResult();

            if (options.IncludeOtherAgentsShallowCheck)
                ScanOtherAgents(options, report);

            if (options.IncludeProjectRules)
                ScanProjectRules(options, report);

            SanitizeTokenRecords(report, options.Now);
            report.TokenSummary = BuildTokenSummary(report.TokenRecords, options.Now);
            return report;
        }, cancellationToken);
    }

    public async Task SaveSkillDescriptionAsync(AgentSkillItem skill, string language, CancellationToken cancellationToken = default)
    {
        if (!skill.IsUserEditable)
            throw new InvalidOperationException("Only user-owned skills can be edited.");
        if (string.IsNullOrWhiteSpace(skill.Path) || !File.Exists(skill.Path))
            throw new FileNotFoundException("Skill file was not found.", skill.Path);

        var isChinese = string.Equals(language, "中文", StringComparison.OrdinalIgnoreCase);
        var descriptionKey = isChinese ? "description_zh" : "description";
        var description = NormalizeFrontMatterValue(skill.Description);
        var lines = (await File.ReadAllLinesAsync(skill.Path, cancellationToken)).ToList();

        if (lines.Count == 0)
        {
            lines.Add("---");
            lines.Add($"name: {NormalizeFrontMatterValue(skill.Name)}");
            lines.Add($"{descriptionKey}: {description}");
            lines.Add("---");
        }
        else if (lines[0].Trim() == "---")
        {
            var endIndex = lines.FindIndex(1, line => line.Trim() == "---");
            if (endIndex < 0) endIndex = Math.Min(lines.Count, 1);

            var descriptionIndex = lines.FindIndex(1, Math.Max(0, endIndex - 1),
                line => line.TrimStart().StartsWith($"{descriptionKey}:", StringComparison.OrdinalIgnoreCase));
            if (descriptionIndex >= 0)
                lines[descriptionIndex] = $"{descriptionKey}: {description}";
            else
                lines.Insert(endIndex, $"{descriptionKey}: {description}");
        }
        else
        {
            lines.InsertRange(0,
            [
                "---",
                $"name: {NormalizeFrontMatterValue(skill.Name)}",
                $"{descriptionKey}: {description}",
                "---"
            ]);
        }

        await File.WriteAllLinesAsync(skill.Path, lines, cancellationToken);
        if (isChinese)
            skill.DescriptionChinese = description;
        else
            skill.DescriptionEnglish = description;
        skill.LastWriteTime = File.GetLastWriteTime(skill.Path);
    }

    private async Task AppendApiTokenRecords(
        AgentScanOptions options,
        AgentScanReport report,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(options.ApiKey))
        {
            report.Findings.Add(new AgentFinding
            {
                Kind = AgentToolKind.Codex,
                Level = AgentFindingLevel.Attention,
                Title = "API Token 数据源未配置",
                Message = "已选择 API 数据源，但设置里没有填写 API Key。",
                Provenance = AgentDataProvenance.RemoteUsageApi
            });
            return;
        }

        try
        {
            var start = options.Now.ToLocalTime().Date.AddDays(-29);
            var end = options.Now.ToLocalTime().Date.AddDays(1);
            var records = await _apiClient.GetUsageAsync(new AgentTokenUsageApiOptions
            {
                Provider = options.ApiProvider,
                ApiKey = options.ApiKey,
                Model = options.ApiModel,
                Start = new DateTimeOffset(start),
                End = new DateTimeOffset(end),
                Agent = string.Equals(options.ApiProvider, "Anthropic", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(options.ApiProvider, "Claude", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(options.ApiProvider, "Claude Code", StringComparison.OrdinalIgnoreCase)
                    ? AgentToolKind.ClaudeCode
                    : AgentToolKind.Codex
            }, cancellationToken);

            foreach (var record in records)
            {
                record.Provenance = AgentDataProvenance.RemoteUsageApi;
                record.EstimatedCostUsd ??= EstimateCost(record.Tokens);
                report.TokenRecords.Add(record);
            }
        }
        catch (Exception ex)
        {
            report.Findings.Add(new AgentFinding
            {
                Kind = AgentToolKind.Codex,
                Level = AgentFindingLevel.Attention,
                Title = "API Token 数据读取失败",
                Message = $"{options.ApiProvider} 用量 API 无法读取：{ex.Message}",
                Provenance = AgentDataProvenance.RemoteUsageApi
            });
        }
    }

    private static void ScanCodex(AgentScanOptions options, AgentScanReport report, CancellationToken cancellationToken)
    {
        var root = Path.Combine(options.UserProfilePath, ".codex");
        if (!Directory.Exists(root))
        {
            AddUnsupportedEnvironment(report, AgentToolKind.Codex, "Codex", root, "未发现 Codex 配置目录");
            return;
        }

        report.Environments.Add(new AgentEnvironmentItem
        {
            Kind = AgentToolKind.Codex,
            DisplayName = "Codex",
            RootPath = root,
            IsDetected = true,
            Level = AgentFindingLevel.Normal,
            Provenance = AgentDataProvenance.LocalConfig,
            Status = "已发现",
            Detail = "检测到本地 Codex 配置目录",
            LastWriteTime = Directory.GetLastWriteTime(root)
        });

        AddPresenceFinding(report, AgentToolKind.Codex, Path.Combine(root, "config.toml"), "config.toml");
        AddPresenceFinding(report, AgentToolKind.Codex, Path.Combine(root, "auth.json"), "auth.json", "仅检测存在性，未读取密钥内容");
        AddPresenceFinding(report, AgentToolKind.Codex, Path.Combine(root, "session_index.jsonl"), "session_index.jsonl");

        ScanSkillDirectory(report, AgentToolKind.Codex, Path.Combine(root, "skills"), "用户 Skills", cancellationToken);
        ScanSkillDirectory(report, AgentToolKind.Codex, Path.Combine(root, "plugins", "cache"), "插件缓存 Skills", cancellationToken);

        if (options.IncludeTokenUsage && options.TokenSource == AgentTokenSourceKind.Local)
            ScanCodexTokens(options, report, Path.Combine(root, "state_5.sqlite"));
    }

    private static void ScanCodexTokens(AgentScanOptions options, AgentScanReport report, string databasePath)
    {
        if (!File.Exists(databasePath))
        {
            report.Findings.Add(new AgentFinding
            {
                Kind = AgentToolKind.Codex,
                Level = AgentFindingLevel.Attention,
                Title = "未发现 Codex token 数据库",
                Message = "state_5.sqlite 不存在，无法读取本地线程 token 汇总。",
                Provenance = AgentDataProvenance.LocalTokenCache
            });
            return;
        }

        try
        {
            var builder = new SqliteConnectionStringBuilder
            {
                DataSource = databasePath,
                Mode = SqliteOpenMode.ReadOnly,
                Cache = SqliteCacheMode.Shared,
                Pooling = false
            };

            using var connection = new SqliteConnection(builder.ToString());
            connection.Open();

            var columns = GetTableColumns(connection, "threads");
            var updatedAtMsSelect = columns.Contains("updated_at_ms") ? "updated_at_ms" : "NULL";
            var createdAtMsSelect = columns.Contains("created_at_ms") ? "created_at_ms" : "NULL";
            var rolloutPathSelect = columns.Contains("rollout_path") ? "rollout_path" : "NULL";

            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT created_at,
                       updated_at,
                       {0} AS updated_at_ms,
                       {1} AS created_at_ms,
                       {2} AS rollout_path,
                       tokens_used,
                       model,
                       cwd
                FROM threads
                WHERE ({2} IS NOT NULL AND {2} <> '')
                   OR (tokens_used IS NOT NULL AND tokens_used > 0)
            """;
            command.CommandText = string.Format(CultureInfo.InvariantCulture, command.CommandText, updatedAtMsSelect, createdAtMsSelect, rolloutPathSelect);

            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var model = NormalizeModel(reader["model"]?.ToString());
                var projectPath = reader["cwd"]?.ToString() ?? "";
                var rolloutPath = NormalizeWindowsPath(reader["rollout_path"]?.ToString() ?? "");
                if (AppendCodexRolloutTokenRecords(report, rolloutPath, model, projectPath))
                    continue;

                var date = ReadDate(reader["updated_at_ms"]) ??
                           ReadDate(reader["updated_at"]) ??
                           ReadDate(reader["created_at_ms"]) ??
                           ReadDate(reader["created_at"]) ??
                           options.Now;
                var tokens = ReadLong(reader["tokens_used"]);
                if (tokens <= 0) continue;

                report.TokenRecords.Add(new AgentTokenUsageRecord
                {
                    Agent = AgentToolKind.Codex,
                    Date = date.ToLocalTime(),
                    Tokens = tokens,
                    Model = model,
                    SessionCount = 1,
                    ProjectPath = projectPath,
                    Provenance = AgentDataProvenance.LocalTokenCache,
                    EstimatedCostUsd = EstimateCost(tokens)
                });
            }
        }
        catch (Exception ex)
        {
            report.Findings.Add(new AgentFinding
            {
                Kind = AgentToolKind.Codex,
                Level = AgentFindingLevel.Attention,
                Title = "Codex token 数据读取失败",
                Message = $"state_5.sqlite 无法读取：{ex.Message}",
                Provenance = AgentDataProvenance.LocalTokenCache
            });
        }
    }

    private static bool AppendCodexRolloutTokenRecords(
        AgentScanReport report,
        string rolloutPath,
        string model,
        string projectPath)
    {
        if (string.IsNullOrWhiteSpace(rolloutPath) || !File.Exists(rolloutPath))
            return false;

        var parsedUsage = false;
        var currentPrompt = "";
        var sessionId = Path.GetFileNameWithoutExtension(rolloutPath);
        var currentConversationId = $"{sessionId}:0";
        var promptIndex = 0;
        var seenUsageKeys = new HashSet<string>(StringComparer.Ordinal);
        long? previousTotal = null;
        long? previousInput = null;
        long? previousCachedInput = null;
        long? previousOutput = null;
        var observedTokenRecords = new List<PendingTokenUsageRecord>();
        var pendingRecords = new List<PendingTokenUsageRecord>();

        try
        {
            using var stream = new FileStream(rolloutPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var streamReader = new StreamReader(stream);
            while (streamReader.ReadLine() is { } line)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;

                try
                {
                    using var document = JsonDocument.Parse(line);
                    var root = document.RootElement;
                    if (!root.TryGetProperty("payload", out var payload) ||
                        payload.ValueKind != JsonValueKind.Object)
                        continue;

                    var payloadType = payload.TryGetProperty("type", out var payloadTypeElement)
                        ? payloadTypeElement.GetString()
                        : "";
                    if (string.Equals(payloadType, "user_message", StringComparison.OrdinalIgnoreCase))
                    {
                        currentPrompt = ReadCodexPromptPreview(payload);
                        promptIndex++;
                        currentConversationId = $"{sessionId}:{promptIndex}";
                        continue;
                    }

                    if (!string.Equals(payloadType, "token_count", StringComparison.OrdinalIgnoreCase) ||
                        !payload.TryGetProperty("info", out var info) ||
                        info.ValueKind != JsonValueKind.Object)
                        continue;

                    var hasLastTokenUsage = info.TryGetProperty("last_token_usage", out var lastTokenUsage) &&
                                            lastTokenUsage.ValueKind == JsonValueKind.Object;
                    var usage = ReadCodexLastUsage(info);
                    if (usage == null &&
                        info.TryGetProperty("total_token_usage", out var totalUsage) &&
                        totalUsage.ValueKind == JsonValueKind.Object)
                    {
                        var totalTokens = ReadLongFromJson(totalUsage, "total_tokens");
                        var inputTokens = ReadLongFromJson(totalUsage, "input_tokens");
                        var cachedInputTokens = ReadLongFromJson(totalUsage, "cached_input_tokens");
                        var outputTokens = ReadLongFromJson(totalUsage, "output_tokens");

                        if (previousTotal == null)
                        {
                            usage = new TokenUsageComponents(totalTokens, inputTokens, cachedInputTokens, 0, outputTokens, 0);
                        }
                        else
                        {
                            var totalDelta = totalTokens - previousTotal.Value;
                            var inputDelta = inputTokens - (previousInput ?? 0);
                            var cachedDelta = cachedInputTokens - (previousCachedInput ?? 0);
                            var outputDelta = outputTokens - (previousOutput ?? 0);
                            usage = new TokenUsageComponents(
                                Math.Max(0, totalDelta),
                                Math.Max(0, inputDelta),
                                Math.Max(0, cachedDelta),
                                0,
                                Math.Max(0, outputDelta),
                                0);
                        }

                        previousTotal = totalTokens;
                        previousInput = inputTokens;
                        previousCachedInput = cachedInputTokens;
                        previousOutput = outputTokens;
                    }
                    if (usage == null)
                        continue;

                    var tokenUsage = usage.Value;
                    if (hasLastTokenUsage &&
                        tokenUsage.InputTokens == 0 &&
                        tokenUsage.CachedInputTokens == 0 &&
                        tokenUsage.OutputTokens == 0)
                        continue;

                    var date = root.TryGetProperty("timestamp", out var timestamp)
                        ? ReadDate(timestamp.GetString())
                        : null;
                    if (date == null) continue;

                    parsedUsage = true;
                    if (tokenUsage.TotalTokens <= 0)
                        continue;

                    var pendingRecord = new PendingTokenUsageRecord(
                        date.Value.ToLocalTime(),
                        tokenUsage.TotalTokens,
                        tokenUsage.InputTokens,
                        tokenUsage.CachedInputTokens,
                        tokenUsage.CacheWriteInputTokens,
                        tokenUsage.OutputTokens,
                        tokenUsage.ReasoningOutputTokens,
                        currentConversationId,
                        string.IsNullOrWhiteSpace(currentPrompt)
                            ? $"{sessionId} {date.Value.ToLocalTime():HH:mm:ss}"
                            : currentPrompt);
                    observedTokenRecords.Add(pendingRecord);

                    var usageKey = BuildCodexUsageDedupeKey(info, tokenUsage);
                    if (!seenUsageKeys.Add(usageKey))
                        continue;

                    pendingRecords.Add(pendingRecord);
                }
                catch (JsonException)
                {
                    continue;
                }
            }
        }
        catch (IOException ex)
        {
            AddRolloutReadFinding(report, rolloutPath, ex.Message);
            return true;
        }
        catch (UnauthorizedAccessException ex)
        {
            AddRolloutReadFinding(report, rolloutPath, ex.Message);
            return true;
        }

        if (IsCompressedRolloutSnapshotReplay(observedTokenRecords) || IsImplausiblyLargeRolloutReplay(observedTokenRecords))
        {
            AddRolloutSnapshotReplayFinding(report, rolloutPath);
            return true;
        }

        foreach (var pending in pendingRecords)
        {
            report.TokenRecords.Add(new AgentTokenUsageRecord
            {
                Agent = AgentToolKind.Codex,
                Date = pending.Date,
                Tokens = pending.Tokens,
                InputTokens = pending.InputTokens,
                CachedInputTokens = pending.CachedInputTokens,
                CacheWriteInputTokens = pending.CacheWriteInputTokens,
                OutputTokens = pending.OutputTokens,
                ReasoningOutputTokens = pending.ReasoningOutputTokens,
                Model = model,
                MessageCount = 1,
                ConversationId = pending.ConversationId,
                ConversationTitle = pending.ConversationTitle,
                ProjectPath = projectPath,
                Provenance = AgentDataProvenance.LocalTokenCache,
                EstimatedCostUsd = EstimateCost(pending.Tokens)
            });
        }

        return parsedUsage || pendingRecords.Count > 0;
    }

    private static string ReadCodexPromptPreview(JsonElement payload)
    {
        var text = "";
        if (payload.TryGetProperty("message", out var message))
            text = ReadTextFromJson(message);
        if (string.IsNullOrWhiteSpace(text) &&
            payload.TryGetProperty("text", out var textElement))
            text = ReadTextFromJson(textElement);

        const string marker = "## My request for Codex:";
        var markerIndex = text.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (markerIndex >= 0)
            text = text[(markerIndex + marker.Length)..];

        var imageCount = 0;
        if (payload.TryGetProperty("images", out var images) && images.ValueKind == JsonValueKind.Array)
            imageCount += images.GetArrayLength();
        if (payload.TryGetProperty("local_images", out var localImages) && localImages.ValueKind == JsonValueKind.Array)
            imageCount += localImages.GetArrayLength();

        var preview = NormalizePreviewText(text);
        var markerText = imageCount switch
        {
            > 1 => $"[{imageCount} images]",
            1 => "[image]",
            _ => ""
        };

        return NormalizePreviewText(string.Join(" ", new[] { markerText, preview }.Where(s => !string.IsNullOrWhiteSpace(s))));
    }

    private static string ReadTextFromJson(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.String)
            return element.GetString() ?? "";
        if (element.ValueKind != JsonValueKind.Array)
            return "";

        var parts = new List<string>();
        foreach (var part in element.EnumerateArray())
        {
            if (part.ValueKind == JsonValueKind.String)
            {
                parts.Add(part.GetString() ?? "");
                continue;
            }

            if (part.ValueKind == JsonValueKind.Object &&
                part.TryGetProperty("text", out var text) &&
                text.ValueKind == JsonValueKind.String)
                parts.Add(text.GetString() ?? "");
        }

        return string.Join(" ", parts);
    }

    private static TokenUsageComponents? ReadCodexLastUsage(JsonElement info)
    {
        if (!info.TryGetProperty("last_token_usage", out var usage) ||
            usage.ValueKind != JsonValueKind.Object)
            return null;

        var inputTokens = ReadLongFromJson(usage, "input_tokens");
        var cachedInputTokens = ReadLongFromJson(usage, "cached_input_tokens");
        var outputTokens = ReadLongFromJson(usage, "output_tokens");
        var reasoningOutputTokens = ReadLongFromJson(usage, "reasoning_output_tokens");
        var totalTokens = ReadLongFromJson(usage, "total_tokens");
        if (totalTokens <= 0)
            totalTokens = inputTokens + outputTokens;

        return new TokenUsageComponents(
            Math.Max(0, totalTokens),
            Math.Max(0, inputTokens),
            Math.Max(0, cachedInputTokens),
            0,
            Math.Max(0, outputTokens),
            Math.Max(0, reasoningOutputTokens));
    }

    private static string BuildCodexUsageDedupeKey(JsonElement info, TokenUsageComponents usage)
    {
        var cumulativeKey = "";
        if (info.TryGetProperty("total_token_usage", out var totalUsage) &&
            totalUsage.ValueKind == JsonValueKind.Object)
        {
            cumulativeKey = string.Join(":",
            [
                ReadLongFromJson(totalUsage, "input_tokens").ToString(CultureInfo.InvariantCulture),
                ReadLongFromJson(totalUsage, "cached_input_tokens").ToString(CultureInfo.InvariantCulture),
                ReadLongFromJson(totalUsage, "output_tokens").ToString(CultureInfo.InvariantCulture),
                ReadLongFromJson(totalUsage, "reasoning_output_tokens").ToString(CultureInfo.InvariantCulture),
                ReadLongFromJson(totalUsage, "total_tokens").ToString(CultureInfo.InvariantCulture)
            ]);
        }

        return string.Join("|",
        [
            usage.InputTokens.ToString(CultureInfo.InvariantCulture),
            usage.CachedInputTokens.ToString(CultureInfo.InvariantCulture),
            usage.OutputTokens.ToString(CultureInfo.InvariantCulture),
            usage.ReasoningOutputTokens.ToString(CultureInfo.InvariantCulture),
            usage.TotalTokens.ToString(CultureInfo.InvariantCulture),
            cumulativeKey
        ]);
    }

    private static string NormalizePreviewText(string text)
    {
        var normalized = string.Join(" ", (text ?? "")
            .Replace("\r", " ")
            .Replace("\n", " ")
            .Split(' ', StringSplitOptions.RemoveEmptyEntries));
        return normalized.Length <= 120 ? normalized : normalized[..117] + "...";
    }

    private static void AddRolloutReadFinding(AgentScanReport report, string rolloutPath, string message)
    {
        report.Findings.Add(new AgentFinding
        {
            Kind = AgentToolKind.Codex,
            Level = AgentFindingLevel.Attention,
            Title = "Codex token 明细读取跳过",
            Message = $"无法读取 rollout token 明细，已跳过该线程累计值避免虚高：{Path.GetFileName(rolloutPath)}。{message}",
            Provenance = AgentDataProvenance.LocalTokenCache
        });
    }

    private static void AddRolloutSnapshotReplayFinding(AgentScanReport report, string rolloutPath)
    {
        report.Findings.Add(new AgentFinding
        {
            Kind = AgentToolKind.Codex,
            Level = AgentFindingLevel.Attention,
            Title = "Codex token 快照回放已跳过",
            Message = $"rollout token 明细看起来像压缩快照回放，已跳过避免把历史累计值当成当天消耗：{Path.GetFileName(rolloutPath)}。",
            Provenance = AgentDataProvenance.LocalTokenCache
        });
    }

    private static long CalculateEffectiveTokens(long totalTokens, long cachedInputTokens)
    {
        if (totalTokens <= 0)
            return 0;

        if (cachedInputTokens <= 0)
            return totalTokens;

        var clampedCachedInputTokens = Math.Clamp(cachedInputTokens, 0, totalTokens);
        return Math.Max(0, totalTokens - clampedCachedInputTokens);
    }

    private static bool IsCompressedRolloutSnapshotReplay(IReadOnlyCollection<PendingTokenUsageRecord> records)
    {
        if (records.Count < SnapshotReplayMinUsageEvents)
            return false;

        var first = records.Min(record => record.Date);
        var last = records.Max(record => record.Date);
        if (last - first > SnapshotReplayMaxSpan)
            return false;

        return records.Sum(record => record.Tokens) >= LargeRolloutBaselineTokenThreshold;
    }

    private static bool IsImplausiblyLargeRolloutReplay(IReadOnlyCollection<PendingTokenUsageRecord> records)
    {
        if (records.Count < SnapshotReplayMinUsageEvents)
            return false;

        if (records.Sum(record => record.Tokens) <= MaxPlausibleSingleTokenRecord)
            return false;

        var distinctUsageShapes = records
            .Select(record => string.Create(
                CultureInfo.InvariantCulture,
                $"{record.Tokens}:{record.InputTokens}:{record.CachedInputTokens}:{record.CacheWriteInputTokens}:{record.OutputTokens}:{record.ReasoningOutputTokens}"))
            .Distinct(StringComparer.Ordinal)
            .Count();

        return distinctUsageShapes <= Math.Max(3, records.Count / 20);
    }

    private static void ScanClaudeCode(AgentScanOptions options, AgentScanReport report, CancellationToken cancellationToken)
    {
        var root = Path.Combine(options.UserProfilePath, ".claude");
        if (!Directory.Exists(root))
        {
            AddUnsupportedEnvironment(report, AgentToolKind.ClaudeCode, "Claude Code", root, "未发现 Claude Code 配置目录");
            return;
        }

        report.Environments.Add(new AgentEnvironmentItem
        {
            Kind = AgentToolKind.ClaudeCode,
            DisplayName = "Claude Code",
            RootPath = root,
            IsDetected = true,
            Level = AgentFindingLevel.Normal,
            Provenance = AgentDataProvenance.LocalConfig,
            Status = "已发现",
            Detail = "检测到本地 Claude Code 配置目录",
            LastWriteTime = Directory.GetLastWriteTime(root)
        });

        AddPresenceFinding(report, AgentToolKind.ClaudeCode, Path.Combine(root, "settings.json"), "settings.json");
        AddPresenceFinding(report, AgentToolKind.ClaudeCode, Path.Combine(root, "settings.local.json"), "settings.local.json");
        AddPresenceFinding(report, AgentToolKind.ClaudeCode, Path.Combine(root, "mcp.json"), "mcp.json");
        AddPresenceFinding(report, AgentToolKind.ClaudeCode, Path.Combine(root, "sessions"), "sessions");
        ScanSkillDirectory(report, AgentToolKind.ClaudeCode, root, "Claude Skills", cancellationToken);

        if (options.IncludeTokenUsage && options.TokenSource == AgentTokenSourceKind.Local)
            ScanClaudeLocalTokens(report, root);
    }

    private static void ScanClaudeStats(AgentScanReport report, string statsPath)
    {
        if (!File.Exists(statsPath))
        {
            report.Findings.Add(new AgentFinding
            {
                Kind = AgentToolKind.ClaudeCode,
                Level = AgentFindingLevel.Attention,
                Title = "未发现 Claude token 缓存",
                Message = "stats-cache.json 不存在，无法读取 Claude Code 本地 token 统计。",
                Provenance = AgentDataProvenance.LocalTokenCache
            });
            return;
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(statsPath));
            var activityByDate = ReadClaudeDailyActivity(document.RootElement);

            if (!document.RootElement.TryGetProperty("dailyModelTokens", out var dailyModelTokens) ||
                dailyModelTokens.ValueKind != JsonValueKind.Array)
                return;

            foreach (var day in dailyModelTokens.EnumerateArray())
            {
                if (!day.TryGetProperty("date", out var dateElement)) continue;
                var date = ReadDate(dateElement.GetString()) ?? DateTimeOffset.Now;
                var activity = activityByDate.GetValueOrDefault(date.Date);

                if (!day.TryGetProperty("tokensByModel", out var tokensByModel) ||
                    tokensByModel.ValueKind != JsonValueKind.Object)
                    continue;

                foreach (var modelToken in tokensByModel.EnumerateObject())
                {
                    var tokens = ReadLong(modelToken.Value);
                    if (tokens <= 0) continue;

                    report.TokenRecords.Add(new AgentTokenUsageRecord
                    {
                        Agent = AgentToolKind.ClaudeCode,
                        Date = date.ToLocalTime(),
                        Model = NormalizeModel(modelToken.Name),
                        Tokens = tokens,
                        MessageCount = activity.MessageCount,
                        SessionCount = activity.SessionCount,
                        ToolCallCount = activity.ToolCallCount,
                        Provenance = AgentDataProvenance.LocalTokenCache,
                        EstimatedCostUsd = EstimateCost(tokens)
                    });
                }
            }
        }
        catch (Exception ex)
        {
            report.Findings.Add(new AgentFinding
            {
                Kind = AgentToolKind.ClaudeCode,
                Level = AgentFindingLevel.Attention,
                Title = "Claude stats-cache.json 读取失败",
                Message = $"stats-cache.json 无法解析：{ex.Message}",
                Provenance = AgentDataProvenance.LocalTokenCache
            });
        }
    }

    private static void ScanClaudeLocalTokens(AgentScanReport report, string claudeRoot)
    {
        var beforeCount = report.TokenRecords.Count;
        var seenMessages = new HashSet<string>(StringComparer.Ordinal);

        ScanClaudeTranscriptDirectory(report, Path.Combine(claudeRoot, "projects"), seenMessages);
        ScanClaudeTranscriptDirectory(report, Path.Combine(claudeRoot, "transcripts"), seenMessages);

        if (report.TokenRecords.Count == beforeCount)
            ScanClaudeStats(report, Path.Combine(claudeRoot, "stats-cache.json"));
    }

    private static void ScanClaudeTranscriptDirectory(
        AgentScanReport report,
        string root,
        HashSet<string> seenMessages)
    {
        if (!Directory.Exists(root))
            return;

        try
        {
            foreach (var file in Directory.EnumerateFiles(root, "*.jsonl", SearchOption.AllDirectories))
                AppendClaudeTranscriptTokenRecords(report, file, seenMessages);
        }
        catch (Exception ex)
        {
            report.Findings.Add(new AgentFinding
            {
                Kind = AgentToolKind.ClaudeCode,
                Level = AgentFindingLevel.Attention,
                Title = "Claude transcript 读取失败",
                Message = ex.Message,
                Provenance = AgentDataProvenance.LocalTokenCache
            });
        }
    }

    private static void AppendClaudeTranscriptTokenRecords(
        AgentScanReport report,
        string transcriptPath,
        HashSet<string> seenMessages)
    {
        var conversationId = Path.GetFileNameWithoutExtension(transcriptPath);
        var currentPrompt = "";

        try
        {
            using var stream = new FileStream(transcriptPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            var fallbackIndex = 0;
            while (reader.ReadLine() is { } line)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                JsonDocument document;
                try
                {
                    document = JsonDocument.Parse(line);
                }
                catch (JsonException)
                {
                    continue;
                }

                using (document)
                {
                    var root = document.RootElement;
                    var type = root.TryGetProperty("type", out var typeElement)
                        ? typeElement.GetString()
                        : "";
                    if (!root.TryGetProperty("message", out var message) ||
                        message.ValueKind != JsonValueKind.Object)
                        continue;

                    if (string.Equals(type, "user", StringComparison.OrdinalIgnoreCase))
                    {
                        if (message.TryGetProperty("content", out var content))
                        {
                            var prompt = NormalizePreviewText(ReadTextFromJson(content));
                            if (!string.IsNullOrWhiteSpace(prompt))
                                currentPrompt = prompt;
                        }

                        continue;
                    }

                    if (!string.Equals(type, "assistant", StringComparison.OrdinalIgnoreCase) ||
                        !message.TryGetProperty("usage", out var usage) ||
                        usage.ValueKind != JsonValueKind.Object)
                        continue;

                    var messageId = ReadStringFromJson(message, "id");
                    if (string.IsNullOrWhiteSpace(messageId))
                        messageId = $"{transcriptPath}:{fallbackIndex++}";
                    if (!seenMessages.Add(messageId))
                        continue;

                    var input = ReadLongFromJson(usage, "input_tokens");
                    var output = ReadLongFromJson(usage, "output_tokens");
                    var cacheRead = ReadLongFromJson(usage, "cache_read_input_tokens");
                    var cacheWrite = ReadLongFromJson(usage, "cache_creation_input_tokens");
                    var total = input + output + cacheRead + cacheWrite;
                    if (total <= 0) continue;

                    var date = root.TryGetProperty("timestamp", out var timestamp)
                        ? ReadDate(timestamp.GetString())
                        : null;

                    report.TokenRecords.Add(new AgentTokenUsageRecord
                    {
                        Agent = AgentToolKind.ClaudeCode,
                        Date = (date ?? DateTimeOffset.Now).ToLocalTime(),
                        Model = NormalizeModel(ReadStringFromJson(message, "model")),
                        Tokens = total,
                        InputTokens = input,
                        CachedInputTokens = cacheRead,
                        CacheWriteInputTokens = cacheWrite,
                        OutputTokens = output,
                        MessageCount = 1,
                        ToolCallCount = CountClaudeToolUses(message),
                        ConversationId = conversationId,
                        ConversationTitle = string.IsNullOrWhiteSpace(currentPrompt)
                            ? $"{conversationId} {date?.ToLocalTime():HH:mm:ss}"
                            : currentPrompt,
                        ProjectPath = transcriptPath,
                        Provenance = AgentDataProvenance.LocalTokenCache,
                        EstimatedCostUsd = EstimateCost(total)
                    });
                }
            }
        }
        catch (IOException ex)
        {
            report.Findings.Add(new AgentFinding
            {
                Kind = AgentToolKind.ClaudeCode,
                Level = AgentFindingLevel.Attention,
                Title = "Claude transcript 跳过",
                Message = $"{Path.GetFileName(transcriptPath)} 无法读取：{ex.Message}",
                Provenance = AgentDataProvenance.LocalTokenCache
            });
        }
        catch (UnauthorizedAccessException ex)
        {
            report.Findings.Add(new AgentFinding
            {
                Kind = AgentToolKind.ClaudeCode,
                Level = AgentFindingLevel.Attention,
                Title = "Claude transcript 跳过",
                Message = $"{Path.GetFileName(transcriptPath)} 无法读取：{ex.Message}",
                Provenance = AgentDataProvenance.LocalTokenCache
            });
        }
    }

    private static int CountClaudeToolUses(JsonElement message)
    {
        if (!message.TryGetProperty("content", out var content) ||
            content.ValueKind != JsonValueKind.Array)
            return 0;

        var count = 0;
        foreach (var part in content.EnumerateArray())
        {
            if (part.ValueKind == JsonValueKind.Object &&
                part.TryGetProperty("type", out var type) &&
                string.Equals(type.GetString(), "tool_use", StringComparison.OrdinalIgnoreCase))
                count++;
        }

        return count;
    }

    private static void SanitizeTokenRecords(AgentScanReport report, DateTimeOffset now)
    {
        if (report.TokenRecords.Count == 0)
            return;

        var today = now.ToLocalTime().Date;
        var keptRecords = new List<AgentTokenUsageRecord>();
        var removedFuture = new Dictionary<AgentToolKind, int>();
        var removedImplausible = new Dictionary<AgentToolKind, int>();

        foreach (var record in report.TokenRecords)
        {
            if (record.Date.ToLocalTime().Date > today)
            {
                Increment(removedFuture, record.Agent);
                continue;
            }

            if (record.Tokens > MaxPlausibleSingleTokenRecord)
            {
                Increment(removedImplausible, record.Agent);
                continue;
            }

            keptRecords.Add(record);
        }

        if (keptRecords.Count == report.TokenRecords.Count)
            return;

        report.TokenRecords.Clear();
        foreach (var record in keptRecords)
            report.TokenRecords.Add(record);

        foreach (var pair in removedFuture)
        {
            report.Findings.Add(new AgentFinding
            {
                Kind = pair.Key,
                Level = AgentFindingLevel.Attention,
                Title = "Token 未来日期已忽略",
                Message = $"发现 {pair.Value} 条晚于本机当前日期的 token 记录，已忽略以避免统计错位。",
                Provenance = AgentDataProvenance.LocalTokenCache
            });
        }

        foreach (var pair in removedImplausible)
        {
            report.Findings.Add(new AgentFinding
            {
                Kind = pair.Key,
                Level = AgentFindingLevel.Attention,
                Title = "Token 异常大值已忽略",
                Message = $"发现 {pair.Value} 条单条超过 {MaxPlausibleSingleTokenRecord:N0} 的本地 token 记录，疑似累计值/缓存快照，已忽略。",
                Provenance = AgentDataProvenance.LocalTokenCache
            });
        }
    }

    private static void Increment(Dictionary<AgentToolKind, int> counts, AgentToolKind agent)
    {
        counts[agent] = counts.GetValueOrDefault(agent) + 1;
    }

    private static Dictionary<DateTime, ClaudeDailyActivity> ReadClaudeDailyActivity(JsonElement root)
    {
        var result = new Dictionary<DateTime, ClaudeDailyActivity>();
        if (!root.TryGetProperty("dailyActivity", out var dailyActivity) ||
            dailyActivity.ValueKind != JsonValueKind.Array)
            return result;

        foreach (var day in dailyActivity.EnumerateArray())
        {
            if (!day.TryGetProperty("date", out var dateElement)) continue;
            var date = ReadDate(dateElement.GetString());
            if (date == null) continue;

            result[date.Value.Date] = new ClaudeDailyActivity(
                ReadInt(day, "messageCount"),
                ReadInt(day, "sessionCount"),
                ReadInt(day, "toolCallCount"));
        }

        return result;
    }

    private static void ScanOtherAgents(AgentScanOptions options, AgentScanReport report)
    {
        AddShallowEnvironment(report, AgentToolKind.Gemini, "Gemini CLI", Path.Combine(options.UserProfilePath, ".gemini"));
        AddShallowEnvironment(report, AgentToolKind.Cursor, "Cursor", Path.Combine(options.AppDataRoamingPath, "Cursor", "User"));
        AddShallowEnvironment(report, AgentToolKind.Continue, "Continue", Path.Combine(options.UserProfilePath, ".continue"));

        report.Environments.Add(new AgentEnvironmentItem
        {
            Kind = AgentToolKind.ClineRoo,
            DisplayName = "Cline / Roo",
            IsDetected = false,
            Level = AgentFindingLevel.Unsupported,
            Provenance = AgentDataProvenance.Unsupported,
            Status = "暂不支持深度扫描",
            Detail = "第一版仅保留适配入口。"
        });
    }

    private static void ScanProjectRules(AgentScanOptions options, AgentScanReport report)
    {
        foreach (var ruleName in ProjectRuleNames)
        {
            var path = Path.Combine(options.ProjectRoot, ruleName);
            var item = new AgentProjectRuleItem
            {
                Name = ruleName,
                Path = path,
                Exists = File.Exists(path) || Directory.Exists(path)
            };

            if (!item.Exists)
            {
                item.Level = AgentFindingLevel.Unsupported;
                item.Detail = "未发现";
            }
            else
            {
                try
                {
                    item.IsReadable = true;
                    item.Level = AgentFindingLevel.Normal;
                    item.Detail = "已发现";
                    if (File.Exists(path))
                    {
                        var info = new FileInfo(path);
                        item.SizeBytes = info.Length;
                        item.LastWriteTime = info.LastWriteTime;
                    }
                    else
                    {
                        item.LastWriteTime = Directory.GetLastWriteTime(path);
                    }
                }
                catch (Exception ex)
                {
                    item.Level = AgentFindingLevel.Attention;
                    item.Detail = $"无法读取：{ex.Message}";
                }
            }

            report.ProjectRules.Add(item);
        }
    }

    private static AgentTokenUsageSummary BuildTokenSummary(IEnumerable<AgentTokenUsageRecord> records, DateTimeOffset now)
    {
        var list = records.ToList();
        var today = now.Date;
        var weekStart = today.AddDays(-(((int)today.DayOfWeek + 6) % 7));
        var monthStart = new DateTime(today.Year, today.Month, 1);

        return new AgentTokenUsageSummary
        {
            TodayTokens = list.Where(r => r.Date.ToLocalTime().Date == today).Sum(r => r.Tokens),
            ThisWeekTokens = list.Where(r => r.Date.ToLocalTime().Date >= weekStart).Sum(r => r.Tokens),
            ThisMonthTokens = list.Where(r => r.Date.ToLocalTime().Date >= monthStart).Sum(r => r.Tokens),
            TotalTokens = list.Sum(r => r.Tokens),
            SessionCount = list.Sum(r => r.SessionCount),
            MessageCount = list.Sum(r => r.MessageCount),
            ToolCallCount = list.Sum(r => r.ToolCallCount),
            EstimatedCostUsd = list.Sum(r => r.EstimatedCostUsd ?? 0)
        };
    }

    private static void ScanSkillDirectory(
        AgentScanReport report,
        AgentToolKind agent,
        string root,
        string source,
        CancellationToken cancellationToken)
    {
        if (!Directory.Exists(root)) return;

        try
        {
            foreach (var file in Directory.EnumerateFiles(root, "SKILL.md", SearchOption.AllDirectories))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var metadata = ReadSkillMetadata(file);
                var sourceInfo = ClassifySkillSource(file, root, source);
                report.Skills.Add(new AgentSkillItem
                {
                    Agent = agent,
                    Name = string.IsNullOrWhiteSpace(metadata.Name)
                        ? Path.GetFileName(Path.GetDirectoryName(file)) ?? "unknown"
                        : metadata.Name,
                    Description = string.IsNullOrWhiteSpace(metadata.DescriptionEnglish)
                        ? "未提供用途说明。"
                        : metadata.DescriptionEnglish,
                    DescriptionEnglish = metadata.DescriptionEnglish,
                    DescriptionChinese = metadata.DescriptionChinese,
                    Path = file,
                    Source = sourceInfo.Source,
                    PluginName = sourceInfo.PluginName,
                    IsUserEditable = sourceInfo.IsUserEditable,
                    Provenance = AgentDataProvenance.LocalSkills,
                    LastWriteTime = File.GetLastWriteTime(file)
                });
            }
        }
        catch (Exception ex)
        {
            report.Findings.Add(new AgentFinding
            {
                Kind = agent,
                Level = AgentFindingLevel.Attention,
                Title = $"{source} 扫描失败",
                Message = ex.Message,
                Provenance = AgentDataProvenance.LocalSkills
            });
        }
    }

    private static SkillFileMetadata ReadSkillMetadata(string path)
    {
        var metadata = new SkillFileMetadata("", "", "");
        try
        {
            foreach (var line in File.ReadLines(path).Take(80))
            {
                if (line.StartsWith("name:", StringComparison.OrdinalIgnoreCase))
                    metadata = metadata with { Name = CleanFrontMatterValue(line["name:".Length..]) };
                else if (line.StartsWith("description:", StringComparison.OrdinalIgnoreCase))
                    metadata = metadata with { DescriptionEnglish = CleanFrontMatterValue(line["description:".Length..]) };
                else if (line.StartsWith("description_zh:", StringComparison.OrdinalIgnoreCase) ||
                         line.StartsWith("description_cn:", StringComparison.OrdinalIgnoreCase))
                    metadata = metadata with { DescriptionChinese = CleanFrontMatterValue(line[(line.IndexOf(':') + 1)..]) };

                if (!string.IsNullOrWhiteSpace(metadata.Name) &&
                    !string.IsNullOrWhiteSpace(metadata.DescriptionEnglish) &&
                    !string.IsNullOrWhiteSpace(metadata.DescriptionChinese))
                    break;
            }
        }
        catch
        {
            return metadata;
        }

        return metadata;
    }

    private static SkillSourceInfo ClassifySkillSource(string skillPath, string scanRoot, string configuredSource)
    {
        var normalizedPath = NormalizePath(skillPath);
        var normalizedRoot = NormalizePath(scanRoot);

        if (normalizedPath.Contains("/plugins/cache/", StringComparison.OrdinalIgnoreCase))
        {
            var pluginName = ExtractPluginName(normalizedPath);
            return new SkillSourceInfo($"插件: {pluginName}", pluginName, false);
        }

        if (normalizedRoot.EndsWith("/.codex/skills", StringComparison.OrdinalIgnoreCase) ||
            normalizedRoot.EndsWith("/.claude/skills", StringComparison.OrdinalIgnoreCase) ||
            normalizedPath.Contains("/.claude/skills/", StringComparison.OrdinalIgnoreCase))
            return new SkillSourceInfo("其它 Skill", "其它 Skill", true);

        return new SkillSourceInfo(configuredSource, configuredSource, false);
    }

    private static string ExtractPluginName(string normalizedPath)
    {
        var parts = normalizedPath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var cacheIndex = Array.FindIndex(parts, part => part.Equals("cache", StringComparison.OrdinalIgnoreCase));
        var skillsIndex = Array.FindIndex(parts, part => part.Equals("skills", StringComparison.OrdinalIgnoreCase));
        if (cacheIndex < 0 || skillsIndex <= cacheIndex + 1)
            return "unknown";

        var pluginSegments = parts.Skip(cacheIndex + 1).Take(skillsIndex - cacheIndex - 1).ToArray();
        if (pluginSegments.Length >= 2)
            return pluginSegments[^2];
        return pluginSegments[0];
    }

    private static string NormalizePath(string path)
    {
        return path.Replace('\\', '/').TrimEnd('/');
    }

    private static string NormalizeWindowsPath(string path)
    {
        if (path.StartsWith(@"\\?\", StringComparison.Ordinal))
            return path[4..];
        return path;
    }

    private static string CleanFrontMatterValue(string value)
    {
        return value.Trim().Trim('"', '\'');
    }

    private static string NormalizeFrontMatterValue(string value)
    {
        return (value ?? "")
            .Replace("\r", " ")
            .Replace("\n", " ")
            .Trim()
            .Trim('"', '\'');
    }

    private static void AddUnsupportedEnvironment(
        AgentScanReport report,
        AgentToolKind kind,
        string displayName,
        string root,
        string detail)
    {
        report.Environments.Add(new AgentEnvironmentItem
        {
            Kind = kind,
            DisplayName = displayName,
            RootPath = root,
            IsDetected = false,
            Level = AgentFindingLevel.Unsupported,
            Provenance = AgentDataProvenance.Unsupported,
            Status = "未发现",
            Detail = detail
        });
        report.Findings.Add(new AgentFinding
        {
            Kind = kind,
            Level = AgentFindingLevel.Unsupported,
            Title = $"{displayName} 未发现",
            Message = detail,
            Provenance = AgentDataProvenance.Unsupported
        });
    }

    private static void AddShallowEnvironment(AgentScanReport report, AgentToolKind kind, string displayName, string path)
    {
        var exists = Directory.Exists(path);
        report.Environments.Add(new AgentEnvironmentItem
        {
            Kind = kind,
            DisplayName = displayName,
            RootPath = path,
            IsDetected = exists,
            Level = exists ? AgentFindingLevel.Attention : AgentFindingLevel.Unsupported,
            Provenance = exists ? AgentDataProvenance.LocalConfig : AgentDataProvenance.Unsupported,
            Status = exists ? "已发现，暂不支持深度扫描" : "未发现",
            Detail = exists ? "第一版仅做存在性检测。" : "未发现配置目录。",
            LastWriteTime = exists ? Directory.GetLastWriteTime(path) : null
        });
    }

    private static void AddPresenceFinding(
        AgentScanReport report,
        AgentToolKind kind,
        string path,
        string displayName,
        string? extraMessage = null)
    {
        if (!File.Exists(path) && !Directory.Exists(path)) return;

        report.Findings.Add(new AgentFinding
        {
            Kind = kind,
            Level = AgentFindingLevel.Normal,
            Title = $"{displayName} 已发现",
            Message = extraMessage ?? $"{displayName} 存在。",
            Provenance = AgentDataProvenance.LocalConfig
        });
    }

    private static HashSet<string> GetTableColumns(SqliteConnection connection, string tableName)
    {
        var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info({tableName})";
        using var reader = command.ExecuteReader();
        while (reader.Read())
            columns.Add(reader["name"]?.ToString() ?? "");
        return columns;
    }

    private static DateTimeOffset? ReadDate(object? value)
    {
        if (value == null || value is DBNull) return null;
        if (value is DateTimeOffset offset) return offset.ToLocalTime();
        if (value is DateTime dateTime) return new DateTimeOffset(dateTime).ToLocalTime();

        if (TryReadLong(value, out var integerDate) &&
            TryReadUnixDate(integerDate, out var unixDate))
            return unixDate;

        var text = value.ToString();
        if (string.IsNullOrWhiteSpace(text)) return null;
        if (long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out integerDate) &&
            TryReadUnixDate(integerDate, out unixDate))
            return unixDate;
        if (DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out offset))
            return offset.ToLocalTime();
        return null;
    }

    private static bool TryReadUnixDate(long value, out DateTimeOffset date)
    {
        foreach (var divisor in new[] { 1L, 1_000L, 1_000_000L, 1_000_000_000L })
        {
            var seconds = value / divisor;
            if (seconds < 946_684_800L || seconds > 4_102_444_800L)
                continue;

            date = DateTimeOffset.FromUnixTimeSeconds(seconds).ToLocalTime();
            return true;
        }

        date = default;
        return false;
    }

    private static long ReadLong(object value)
    {
        return value switch
        {
            long l => l,
            int i => i,
            JsonElement { ValueKind: JsonValueKind.Number } e when e.TryGetInt64(out var l) => l,
            JsonElement { ValueKind: JsonValueKind.String } e when long.TryParse(e.GetString(), out var l) => l,
            _ when TryReadLong(value, out var l) => l,
            _ => 0
        };
    }

    private static long ReadLongFromJson(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property))
            return 0;
        return ReadLong(property);
    }

    private static string ReadStringFromJson(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property))
            return "";
        return property.ValueKind == JsonValueKind.String
            ? property.GetString() ?? ""
            : property.ToString();
    }

    private static bool TryReadLong(object value, out long result)
    {
        return value switch
        {
            long l => SetOut(l, out result),
            int i => SetOut(i, out result),
            JsonElement { ValueKind: JsonValueKind.Number } e when e.TryGetInt64(out var l) => SetOut(l, out result),
            JsonElement { ValueKind: JsonValueKind.String } e when long.TryParse(e.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var l) => SetOut(l, out result),
            _ => long.TryParse(value.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out result)
        };
    }

    private static bool SetOut(long value, out long result)
    {
        result = value;
        return true;
    }

    private static int ReadInt(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property)) return 0;
        if (property.ValueKind == JsonValueKind.Number && property.TryGetInt32(out var value)) return value;
        if (property.ValueKind == JsonValueKind.String && int.TryParse(property.GetString(), out value)) return value;
        return 0;
    }

    private static string NormalizeModel(string? model)
    {
        return string.IsNullOrWhiteSpace(model) ? "unknown" : model.Trim();
    }

    private static decimal EstimateCost(long tokens)
    {
        // Conservative placeholder estimate: $3 / 1M tokens. UI and report label this as estimated.
        return Math.Round(tokens / 1_000_000m * 3m, 4);
    }

    private readonly record struct ClaudeDailyActivity(int MessageCount, int SessionCount, int ToolCallCount);
    private readonly record struct PendingTokenUsageRecord(
        DateTimeOffset Date,
        long Tokens,
        long InputTokens,
        long CachedInputTokens,
        long CacheWriteInputTokens,
        long OutputTokens,
        long ReasoningOutputTokens,
        string ConversationId,
        string ConversationTitle);
    private readonly record struct TokenUsageComponents(
        long TotalTokens,
        long InputTokens,
        long CachedInputTokens,
        long CacheWriteInputTokens,
        long OutputTokens,
        long ReasoningOutputTokens);
    private readonly record struct SkillFileMetadata(string Name, string DescriptionEnglish, string DescriptionChinese);
    private readonly record struct SkillSourceInfo(string Source, string PluginName, bool IsUserEditable);
}
