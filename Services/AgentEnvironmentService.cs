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

            if (options.IncludeOtherAgentsShallowCheck)
                ScanOtherAgents(options, report);

            if (options.IncludeProjectRules)
                ScanProjectRules(options, report);

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

        if (options.IncludeTokenUsage)
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
                WHERE tokens_used IS NOT NULL AND tokens_used > 0
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

        var added = false;
        var previousTotal = 0L;
        var previousInput = 0L;
        var previousCachedInput = 0L;

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
                        payload.ValueKind != JsonValueKind.Object ||
                        !payload.TryGetProperty("info", out var info) ||
                        info.ValueKind != JsonValueKind.Object ||
                        !info.TryGetProperty("total_token_usage", out var totalUsage) ||
                        totalUsage.ValueKind != JsonValueKind.Object)
                        continue;

                    var totalTokens = ReadLongFromJson(totalUsage, "total_tokens");
                    if (totalTokens <= 0) continue;
                    var inputTokens = ReadLongFromJson(totalUsage, "input_tokens");
                    var cachedInputTokens = ReadLongFromJson(totalUsage, "cached_input_tokens");

                    var delta = totalTokens - previousTotal;
                    if (delta < 0)
                        delta = totalTokens;
                    var inputDelta = inputTokens - previousInput;
                    if (inputDelta < 0)
                        inputDelta = inputTokens;
                    var cachedInputDelta = cachedInputTokens - previousCachedInput;
                    if (cachedInputDelta < 0)
                        cachedInputDelta = cachedInputTokens;

                    previousTotal = totalTokens;
                    previousInput = inputTokens;
                    previousCachedInput = cachedInputTokens;
                    if (delta <= 0) continue;

                    var date = root.TryGetProperty("timestamp", out var timestamp)
                        ? ReadDate(timestamp.GetString())
                        : null;
                    if (date == null) continue;

                    report.TokenRecords.Add(new AgentTokenUsageRecord
                    {
                        Agent = AgentToolKind.Codex,
                        Date = date.Value.ToLocalTime(),
                        Tokens = delta,
                        InputTokens = Math.Max(0, inputDelta),
                        CachedInputTokens = Math.Max(0, cachedInputDelta),
                        Model = model,
                        MessageCount = 1,
                        ProjectPath = projectPath,
                        Provenance = AgentDataProvenance.LocalTokenCache,
                        EstimatedCostUsd = EstimateCost(delta)
                    });
                    added = true;
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

        return added;
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

        if (options.IncludeTokenUsage)
            ScanClaudeStats(report, Path.Combine(root, "stats-cache.json"));
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
    private readonly record struct SkillFileMetadata(string Name, string DescriptionEnglish, string DescriptionChinese);
    private readonly record struct SkillSourceInfo(string Source, string PluginName, bool IsUserEditable);
}
