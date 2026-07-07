using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using OxyPlot;
using OxyPlot.Axes;
using OxyPlot.Series;
using Toolbox.Models;
using Toolbox.Services;

namespace Toolbox.ViewModels;

public partial class AgentInspectorViewModel : ViewModelBase
{
    private readonly IAgentEnvironmentService _environmentService;
    private readonly IAgentReportExportService _reportExportService;
    private readonly IAgentTokenHistoryService _tokenHistoryService;
    private readonly IConfigService _configService;
    private CancellationTokenSource? _scanCts;
    private bool _isSyncingTokenDateText;
    private IReadOnlyList<AgentDailyTokenHistoryRecord> _tokenHistory = [];

    public ObservableCollection<AgentEnvironmentItem> Environments { get; } = new();
    public ObservableCollection<AgentSkillItem> Skills { get; } = new();
    public ObservableCollection<AgentSkillAgentGroup> SkillAgentGroups { get; } = new();
    public ObservableCollection<AgentProjectRuleItem> ProjectRules { get; } = new();
    public ObservableCollection<AgentTokenUsageRecord> TokenRecords { get; } = new();
    public ObservableCollection<AgentTokenUsageRecord> SelectedDateTokenRecords { get; } = new();
    public ObservableCollection<AgentTokenConversationSummary> SelectedDateTokenConversations { get; } = new();
    public ObservableCollection<AgentTokenModelSummary> TokenModelSummaries { get; } = new();
    public ObservableCollection<AgentTokenModelSummary> SelectedDayTokenModelSummaries { get; } = new();
    public ObservableCollection<AgentFinding> Findings { get; } = new();

    [ObservableProperty]
    private AgentTokenUsageSummary _tokenSummary = new();

    [ObservableProperty]
    private string _todayTokensText = "0";

    [ObservableProperty]
    private string _weekTokensText = "0";

    [ObservableProperty]
    private string _monthTokensText = "0";

    [ObservableProperty]
    private string _estimatedCostText = "约 $0.0000 估算";

    [ObservableProperty]
    private string _selectedTokenPeriod = "TODAY";

    [ObservableProperty]
    private string _selectedTokenPeriodTitle = "今日 软件 / 模型消耗";

    [ObservableProperty]
    private string _tokenScopeNote = "本地 raw token，含上下文/缓存，不等同于额度扣费。";

    [ObservableProperty]
    private DateTime? _selectedTokenDate = DateTime.Today;

    [ObservableProperty]
    private string _selectedTokenDateText = DateTime.Today.ToString("yyyy-MM-dd");

    [ObservableProperty]
    private string _selectedDateTokensText = "0";

    [ObservableProperty]
    private string _selectedDateCallCountText = "0";

    [ObservableProperty]
    private string _selectedDateModelCountText = "0";

    [ObservableProperty]
    private string _selectedDayTotalTokensText = "0";

    [ObservableProperty]
    private string _selectedDayCallCountText = "暂无精确数据";

    [ObservableProperty]
    private string _selectedDayCacheHitRateText = "暂无数据";

    [ObservableProperty]
    private string _selectedDayHistoryStatusText = "选择日期后显示单日总消耗";

    [ObservableProperty]
    private string _selectedTokenDateLabel = DateTime.Today.ToString("yyyy-MM-dd");

    [ObservableProperty]
    private PlotModel _tokenTrendPlot = CreateEmptyPlot("Token 消耗曲线");

    [ObservableProperty]
    private PlotModel _modelCallTrendPlot = CreateEmptyPlot("统计记录数量曲线");

    [ObservableProperty]
    private string _reportText = "";

    [ObservableProperty]
    private bool _hasScanResult;

    [ObservableProperty]
    private string _currentScanTarget = "";

    [ObservableProperty]
    private string _detectedAgentCountText = "0";

    [ObservableProperty]
    private string _skillCountText = "0";

    [ObservableProperty]
    private string _attentionCountText = "0";

    [ObservableProperty]
    private string _tokenAvailabilityText = "未扫描";

    [ObservableProperty]
    private string _selectedSkillDescriptionLanguage = "中文";

    public string[] SkillDescriptionLanguages { get; } = ["中文", "English"];

    public AgentInspectorViewModel(
        IAgentEnvironmentService environmentService,
        IAgentReportExportService reportExportService,
        IAgentTokenHistoryService tokenHistoryService,
        IConfigService? configService = null)
    {
        _environmentService = environmentService;
        _reportExportService = reportExportService;
        _tokenHistoryService = tokenHistoryService;
        _configService = configService ?? new ConfigService();
        _tokenHistory = _tokenHistoryService.LoadHistory(DateTime.Today);
        RefreshSelectedDayHistory();
        StatusMessage = "等待扫描。点击开始扫描后才会读取本机 AI Agent 配置。";
    }

    [RelayCommand]
    private async Task ScanAsync()
    {
        if (IsBusy) return;

        _scanCts?.Dispose();
        _scanCts = new CancellationTokenSource();

        ClearResults();
        IsBusy = true;
        IsProgressIndeterminate = true;
        ProgressValue = 0;
        CurrentScanTarget = "初始化";
        StatusMessage = "正在准备扫描 AI Agent 环境...";

        try
        {
            CurrentScanTarget = "Codex / Claude Code / 项目规则";
            var settings = _configService.LoadConfig<AppSettings>(_configService.SettingsFilePath) ?? new AppSettings();
            var report = await _environmentService.ScanAsync(new AgentScanOptions
            {
                TokenSource = string.Equals(settings.AgentTokenDataSource, "Api", StringComparison.OrdinalIgnoreCase)
                    ? AgentTokenSourceKind.Api
                    : AgentTokenSourceKind.Local,
                ApiProvider = settings.AgentTokenApiProvider,
                ApiKey = settings.AgentTokenApiKey,
                ApiModel = settings.AgentTokenApiModel
            }, _scanCts.Token);
            ApplyReport(report);
            StatusMessage = string.Equals(settings.AgentTokenDataSource, "Api", StringComparison.OrdinalIgnoreCase)
                ? "扫描完成。Token 数据来自设置里的 API 用量接口。"
                : "扫描完成。所有数据均来自本机只读元数据。";
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "扫描已取消。";
            CurrentScanTarget = "";
        }
        catch (Exception ex)
        {
            StatusMessage = $"扫描失败：{ex.Message}";
            Findings.Add(new AgentFinding
            {
                Kind = AgentToolKind.Codex,
                Level = AgentFindingLevel.Risk,
                Title = "扫描失败",
                Message = ex.Message,
                Provenance = AgentDataProvenance.LocalConfig
            });
            RefreshMetricTexts();
        }
        finally
        {
            IsBusy = false;
            IsProgressIndeterminate = false;
            ProgressValue = 100;
            CurrentScanTarget = "";
        }
    }

    [RelayCommand]
    private void CancelScan()
    {
        _scanCts?.Cancel();
    }

    [RelayCommand]
    private void ShowTodayTokens()
    {
        SelectedTokenDate = DateTime.Today;
        SelectTokenPeriod("TODAY");
        RefreshTokenDateView();
    }

    [RelayCommand]
    private void SelectTokenPeriod(string? period)
    {
        SelectedTokenPeriod = string.IsNullOrWhiteSpace(period) ? "TODAY" : period.ToUpperInvariant();
        SelectedTokenPeriodTitle = $"{FormatPeriodName(SelectedTokenPeriod)} 软件 / 模型消耗";
        RefreshTokenModelSummaries();
    }

    [RelayCommand]
    private void CopyReport()
    {
        if (string.IsNullOrWhiteSpace(ReportText))
        {
            StatusMessage = "暂无报告可复制。";
            return;
        }

        Clipboard.SetText(ReportText);
        StatusMessage = "Markdown 报告已复制到剪贴板。";
    }

    [RelayCommand]
    private void SaveReport()
    {
        if (string.IsNullOrWhiteSpace(ReportText))
        {
            StatusMessage = "暂无报告可保存。";
            return;
        }

        var dialog = new SaveFileDialog
        {
            Title = "保存 AI Agent 检验报告",
            Filter = "Markdown 文件 (*.md)|*.md|文本文件 (*.txt)|*.txt",
            FileName = $"AI-Agent-Report-{DateTime.Now:yyyyMMdd-HHmm}.md"
        };

        if (dialog.ShowDialog() != true) return;

        File.WriteAllText(dialog.FileName, ReportText);
        StatusMessage = $"报告已保存：{dialog.FileName}";
    }

    [RelayCommand]
    private async Task SaveSkillDescriptionAsync(AgentSkillItem? skill)
    {
        if (skill == null) return;
        if (!skill.IsUserEditable)
        {
            StatusMessage = "插件缓存中的 Skill 不允许在这里修改说明。";
            return;
        }

        try
        {
            await _environmentService.SaveSkillDescriptionAsync(skill, SelectedSkillDescriptionLanguage);
            if (SelectedSkillDescriptionLanguage == "中文")
                skill.DescriptionChinese = skill.Description;
            else
                skill.DescriptionEnglish = skill.Description;
            RebuildSkillGroups();
            StatusMessage = $"已保存 Skill 说明：{skill.Name}";
        }
        catch (Exception ex)
        {
            StatusMessage = $"保存 Skill 说明失败：{ex.Message}";
        }
    }

    private void ApplyReport(AgentScanReport report)
    {
        ClearResults();

        foreach (var item in report.Environments) Environments.Add(item);
        foreach (var item in report.Skills) Skills.Add(item);
        ApplySkillDescriptionLanguage();
        RebuildSkillGroups();
        foreach (var item in report.ProjectRules) ProjectRules.Add(item);
        foreach (var item in report.TokenRecords.OrderByDescending(r => r.Date.ToLocalTime())) TokenRecords.Add(item);
        foreach (var item in report.Findings) Findings.Add(item);

        TokenSummary = report.TokenSummary;
        ReportText = _reportExportService.BuildMarkdown(report, new AgentReportPrivacyOptions());
        HasScanResult = true;
        _tokenHistory = _tokenHistoryService.SaveFromRecords(TokenRecords, DateTime.Today);
        RefreshTokenDateView();
        RefreshTokenCharts();
        RefreshTokenModelSummaries();
        RefreshSelectedDayHistory();
        RefreshMetricTexts();
    }

    private void ClearResults()
    {
        Environments.Clear();
        Skills.Clear();
        SkillAgentGroups.Clear();
        ProjectRules.Clear();
        TokenRecords.Clear();
        SelectedDateTokenRecords.Clear();
        SelectedDateTokenConversations.Clear();
        TokenModelSummaries.Clear();
        SelectedDayTokenModelSummaries.Clear();
        Findings.Clear();
        TokenSummary = new AgentTokenUsageSummary();
        SelectedDateTokensText = "0";
        SelectedDateCallCountText = "0";
        SelectedDateModelCountText = "0";
        SelectedDayTotalTokensText = "0";
        SelectedDayCallCountText = "暂无精确数据";
        SelectedDayCacheHitRateText = "暂无数据";
        SelectedDayHistoryStatusText = "选择日期后显示单日总消耗";
        TokenTrendPlot = CreateEmptyPlot("Token 消耗曲线");
        ModelCallTrendPlot = CreateEmptyPlot("统计记录数量曲线");
        ReportText = "";
        HasScanResult = false;
        RefreshMetricTexts();
    }

    private void RefreshMetricTexts()
    {
        DetectedAgentCountText = Environments.Count(e => e.IsDetected).ToString();
        SkillCountText = Skills.Count.ToString();
        AttentionCountText = Findings.Count(f => f.Level is AgentFindingLevel.Attention or AgentFindingLevel.Risk).ToString();
        TokenAvailabilityText = TokenSummary.TotalTokens > 0 ? FormatCompactTokens(TokenSummary.TotalTokens) : HasScanResult ? "无数据" : "未扫描";
        TodayTokensText = FormatCompactTokens(TokenSummary.TodayTokens);
        WeekTokensText = FormatCompactTokens(TokenSummary.ThisWeekTokens);
        MonthTokensText = FormatCompactTokens(TokenSummary.ThisMonthTokens);
        EstimatedCostText = $"约 ${TokenSummary.EstimatedCostUsd ?? 0:0.0000} 估算";
    }

    partial void OnSelectedSkillDescriptionLanguageChanged(string value)
    {
        ApplySkillDescriptionLanguage();
        RebuildSkillGroups();
    }

    partial void OnSelectedTokenDateChanged(DateTime? value)
    {
        if (!_isSyncingTokenDateText)
        {
            _isSyncingTokenDateText = true;
            SelectedTokenDateText = (value ?? DateTime.Today).ToString("yyyy-MM-dd");
            _isSyncingTokenDateText = false;
        }

        RefreshTokenDateView();
        RefreshSelectedDayHistory();
    }

    partial void OnSelectedTokenDateTextChanged(string value)
    {
        if (_isSyncingTokenDateText)
            return;

        if (!DateTime.TryParseExact(value.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed) &&
            !DateTime.TryParse(value, CultureInfo.CurrentCulture, DateTimeStyles.None, out parsed))
            return;

        _isSyncingTokenDateText = true;
        SelectedTokenDate = parsed.Date;
        _isSyncingTokenDateText = false;
        RefreshTokenDateView();
        RefreshSelectedDayHistory();
    }

    private void RefreshTokenDateView()
    {
        SelectedDateTokenRecords.Clear();
        SelectedDateTokenConversations.Clear();
        var selectedDate = (SelectedTokenDate ?? DateTime.Today).Date;
        SelectedTokenDateLabel = selectedDate.ToString("yyyy-MM-dd");

        var records = TokenRecords
            .Where(record => record.Date.ToLocalTime().Date == selectedDate)
            .OrderByDescending(record => record.Date.ToLocalTime())
            .ThenByDescending(record => record.Tokens)
            .ToList();

        foreach (var record in records)
            SelectedDateTokenRecords.Add(record);

        foreach (var conversation in BuildConversationSummaries(records))
            SelectedDateTokenConversations.Add(conversation);

        SelectedDateTokensText = records.Sum(record => record.Tokens).ToString("N0");
        SelectedDateCallCountText = records.Sum(GetCallCount).ToString("N0");
        SelectedDateModelCountText = records.Select(record => record.Model).Distinct(StringComparer.OrdinalIgnoreCase).Count().ToString("N0");
    }

    private static IReadOnlyList<AgentTokenConversationSummary> BuildConversationSummaries(IReadOnlyList<AgentTokenUsageRecord> records)
    {
        return records
            .GroupBy(record => new
            {
                record.Agent,
                record.Model,
                ConversationId = string.IsNullOrWhiteSpace(record.ConversationId)
                    ? $"{record.Agent}:{record.Model}:{record.ConversationTitle}:{record.Date.ToLocalTime():yyyyMMddHHmmss}"
                    : record.ConversationId,
                Title = string.IsNullOrWhiteSpace(record.ConversationTitle)
                    ? "(无标题对话)"
                    : record.ConversationTitle
            })
            .Select(group =>
            {
                var orderedRequests = group
                    .OrderBy(record => record.Date.ToLocalTime())
                    .ThenByDescending(record => record.Tokens)
                    .ToList();
                var summary = new AgentTokenConversationSummary
                {
                    Agent = group.Key.Agent,
                    Model = group.Key.Model,
                    ConversationId = group.Key.ConversationId,
                    ConversationTitle = group.Key.Title,
                    Date = orderedRequests.First().Date,
                    Tokens = group.Sum(record => record.Tokens),
                    InputTokens = group.Sum(record => record.InputTokens),
                    CachedInputTokens = group.Sum(record => record.CachedInputTokens),
                    OutputTokens = group.Sum(record => record.OutputTokens),
                    RequestCount = orderedRequests.Count
                };
                summary.TokensText = summary.Tokens.ToString("N0");
                summary.RequestCountText = summary.RequestCount.ToString("N0");
                foreach (var request in orderedRequests)
                    summary.Requests.Add(request);
                return summary;
            })
            .OrderByDescending(summary => summary.Date.ToLocalTime())
            .ThenByDescending(summary => summary.Tokens)
            .ToList();
    }

    private void RefreshSelectedDayHistory()
    {
        SelectedDayTokenModelSummaries.Clear();
        var selectedDate = (SelectedTokenDate ?? DateTime.Today).Date;
        var currentRecords = TokenRecords
            .Where(record => record.Date.ToLocalTime().Date == selectedDate)
            .ToList();

        var dailyRecord = currentRecords.Count > 0
            ? BuildDailyRecord(selectedDate, currentRecords)
            : _tokenHistory.FirstOrDefault(record => record.Date.Date == selectedDate);

        if (dailyRecord == null)
        {
            SelectedDayTotalTokensText = "0";
            SelectedDayCallCountText = "暂无精确数据";
            SelectedDayCacheHitRateText = "暂无数据";
            SelectedDayHistoryStatusText = $"{selectedDate:yyyy-MM-dd} 暂无历史记录";
            return;
        }

        SelectedDayTotalTokensText = dailyRecord.TotalTokensText;
        SelectedDayCallCountText = dailyRecord.CallCount > 0
            ? dailyRecord.CallCount.ToString("N0")
            : "暂无精确数据";
        SelectedDayCacheHitRateText = dailyRecord.CacheHitRateText;
        SelectedDayHistoryStatusText = currentRecords.Count > 0
            ? $"{selectedDate:yyyy-MM-dd} 来自当前扫描，并已写入 30 天历史"
            : $"{selectedDate:yyyy-MM-dd} 来自本地 30 天历史";

        foreach (var summary in dailyRecord.ModelSummaries.OrderByDescending(summary => summary.Tokens))
            SelectedDayTokenModelSummaries.Add(summary);
    }

    private void RefreshTokenCharts()
    {
        TokenTrendPlot = CreateTokenTrendPlot(TokenRecords);
        ModelCallTrendPlot = CreateModelCallTrendPlot(TokenRecords);
    }

    private void RefreshTokenModelSummaries()
    {
        TokenModelSummaries.Clear();
        var records = FilterRecordsForPeriod(SelectedTokenPeriod).ToList();

        foreach (var group in records
            .GroupBy(record => new { record.Agent, Model = record.Model })
            .Select(group =>
            {
                var inputTokens = group.Sum(record => record.InputTokens);
                var cachedInputTokens = group.Sum(record => record.CachedInputTokens);
                var tokens = group.Sum(record => record.Tokens);
                var callCount = group.Sum(GetCallCount);
                return new AgentTokenModelSummary
                {
                    Agent = group.Key.Agent,
                    Model = group.Key.Model,
                    Tokens = tokens,
                    TokensText = FormatCompactTokens(tokens),
                    InputTokens = inputTokens,
                    CachedInputTokens = cachedInputTokens,
                    CallCount = callCount,
                    CallCountText = callCount > 0 ? callCount.ToString("N0") : "暂无精确数据",
                    CacheHitRateText = FormatCacheHitRate(inputTokens, cachedInputTokens)
                };
            })
            .OrderByDescending(summary => summary.Tokens))
        {
            TokenModelSummaries.Add(group);
        }
    }

    private static AgentDailyTokenHistoryRecord BuildDailyRecord(DateTime date, IReadOnlyList<AgentTokenUsageRecord> records)
    {
        var daily = new AgentDailyTokenHistoryRecord
        {
            Date = date.Date,
            TotalTokens = records.Sum(record => record.Tokens),
            InputTokens = records.Sum(record => record.InputTokens),
            CachedInputTokens = records.Sum(record => record.CachedInputTokens),
            CallCount = records.Sum(GetCallCount)
        };

        daily.TotalTokensText = FormatCompactTokens(daily.TotalTokens);
        daily.CacheHitRateText = FormatCacheHitRate(daily.InputTokens, daily.CachedInputTokens);

        foreach (var summary in records
            .GroupBy(record => new { record.Agent, record.Model })
            .Select(group =>
            {
                var inputTokens = group.Sum(record => record.InputTokens);
                var cachedInputTokens = group.Sum(record => record.CachedInputTokens);
                var tokens = group.Sum(record => record.Tokens);
                var callCount = group.Sum(GetCallCount);
                return new AgentTokenModelSummary
                {
                    Agent = group.Key.Agent,
                    Model = group.Key.Model,
                    Tokens = tokens,
                    TokensText = FormatCompactTokens(tokens),
                    InputTokens = inputTokens,
                    CachedInputTokens = cachedInputTokens,
                    CallCount = callCount,
                    CallCountText = callCount > 0 ? callCount.ToString("N0") : "暂无精确数据",
                    CacheHitRateText = FormatCacheHitRate(inputTokens, cachedInputTokens)
                };
            })
            .OrderByDescending(summary => summary.Tokens))
        {
            daily.ModelSummaries.Add(summary);
        }

        return daily;
    }

    private IEnumerable<AgentTokenUsageRecord> FilterRecordsForPeriod(string period)
    {
        var today = DateTime.Today;
        return (period ?? "TODAY").ToUpperInvariant() switch
        {
            "WEEK" => TokenRecords.Where(record => record.Date.ToLocalTime().Date >= today.AddDays(-(((int)today.DayOfWeek + 6) % 7))),
            "MONTH" => TokenRecords.Where(record => record.Date.ToLocalTime().Date >= new DateTime(today.Year, today.Month, 1)),
            _ => TokenRecords.Where(record => record.Date.ToLocalTime().Date == today)
        };
    }

    private static PlotModel CreateTokenTrendPlot(IEnumerable<AgentTokenUsageRecord> records)
    {
        var model = CreatePlot("Token 消耗曲线", "Token 数");
        var series = new LineSeries
        {
            Title = "Token 消耗",
            TrackerFormatString = "{0}\n日期：{2:yyyy-MM-dd}\n消耗：{4:0,0} 原始 Token",
            Color = OxyColor.FromRgb(0, 136, 255),
            MarkerType = MarkerType.Circle,
            MarkerFill = OxyColor.FromRgb(0, 240, 255),
            MarkerSize = 3,
            StrokeThickness = 2
        };

        foreach (var point in records
            .GroupBy(record => record.Date.ToLocalTime().Date)
            .OrderBy(group => group.Key)
            .Select(group => new { Date = group.Key, Tokens = group.Sum(record => record.Tokens) }))
        {
            series.Points.Add(new DataPoint(DateTimeAxis.ToDouble(point.Date), point.Tokens));
        }

        model.Series.Add(series);
        return model;
    }

    private static PlotModel CreateModelCallTrendPlot(IEnumerable<AgentTokenUsageRecord> records)
    {
        var model = CreatePlot("统计记录数量曲线", "记录数");
        var palette = new[]
        {
            OxyColor.FromRgb(0, 240, 255),
            OxyColor.FromRgb(0, 255, 136),
            OxyColor.FromRgb(153, 68, 255),
            OxyColor.FromRgb(255, 102, 0),
            OxyColor.FromRgb(255, 0, 136),
            OxyColor.FromRgb(0, 136, 255)
        };

        var topModels = records
            .GroupBy(record => record.Model, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(group => group.Count())
            .Take(6)
            .Select(group => group.Key)
            .ToList();

        for (var index = 0; index < topModels.Count; index++)
        {
            var modelName = topModels[index];
            var series = new LineSeries
            {
                Title = modelName,
                TrackerFormatString = "模型：{0}\n日期：{2:yyyy-MM-dd}\n记录：{4:0,0} 条",
                Color = palette[index % palette.Length],
                MarkerType = MarkerType.Circle,
                MarkerSize = 3,
                StrokeThickness = 2
            };

            foreach (var point in records
                .Where(record => string.Equals(record.Model, modelName, StringComparison.OrdinalIgnoreCase))
                .GroupBy(record => record.Date.ToLocalTime().Date)
                .OrderBy(group => group.Key)
                .Select(group => new { Date = group.Key, Calls = group.Count() }))
            {
                series.Points.Add(new DataPoint(DateTimeAxis.ToDouble(point.Date), point.Calls));
            }

            model.Series.Add(series);
        }

        return model;
    }

    private static PlotModel CreatePlot(string title, string leftAxisTitle)
    {
        var model = new PlotModel
        {
            Title = title,
            Background = OxyColors.Transparent,
            PlotAreaBackground = OxyColor.FromArgb(18, 0, 136, 255),
            TextColor = OxyColor.FromRgb(232, 240, 255),
            TitleColor = OxyColor.FromRgb(232, 240, 255),
            DefaultFont = "Consolas"
        };

        model.Axes.Add(new DateTimeAxis
        {
            Position = AxisPosition.Bottom,
            StringFormat = "MM-dd",
            IntervalType = DateTimeIntervalType.Days,
            MajorGridlineStyle = LineStyle.Dot,
            MajorGridlineColor = OxyColor.FromArgb(70, 153, 68, 255),
            MinorGridlineColor = OxyColor.FromArgb(25, 153, 68, 255),
            TextColor = OxyColor.FromRgb(176, 188, 210),
            TicklineColor = OxyColor.FromArgb(120, 0, 240, 255),
            AxislineColor = OxyColor.FromArgb(120, 0, 240, 255)
        });
        model.Axes.Add(new LinearAxis
        {
            Position = AxisPosition.Left,
            Title = leftAxisTitle,
            Minimum = 0,
            MajorGridlineStyle = LineStyle.Dot,
            MajorGridlineColor = OxyColor.FromArgb(50, 153, 68, 255),
            TextColor = OxyColor.FromRgb(176, 188, 210),
            TicklineColor = OxyColor.FromArgb(120, 0, 240, 255),
            AxislineColor = OxyColor.FromArgb(120, 0, 240, 255)
        });

        return model;
    }

    private static PlotModel CreateEmptyPlot(string title)
    {
        return CreatePlot(title, "");
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

    private static string FormatPeriodName(string period)
    {
        return (period ?? "TODAY").ToUpperInvariant() switch
        {
            "WEEK" => "本周",
            "MONTH" => "本月",
            _ => "今日"
        };
    }

    private static string FormatCacheHitRate(long inputTokens, long cachedInputTokens)
    {
        if (inputTokens <= 0)
            return "暂无数据";

        if (cachedInputTokens >= inputTokens)
            return "暂无精确数据";

        var clampedCachedTokens = Math.Clamp(cachedInputTokens, 0, inputTokens);
        return $"{clampedCachedTokens * 100d / inputTokens:0.0}%";
    }

    private void ApplySkillDescriptionLanguage()
    {
        foreach (var skill in Skills)
        {
            if (SelectedSkillDescriptionLanguage == "中文")
            {
                skill.Description = string.IsNullOrWhiteSpace(skill.DescriptionChinese)
                    ? string.IsNullOrWhiteSpace(skill.DescriptionEnglish)
                        ? "暂无说明。"
                        : skill.DescriptionEnglish
                    : skill.DescriptionChinese;
            }
            else
            {
                skill.Description = string.IsNullOrWhiteSpace(skill.DescriptionEnglish)
                    ? "No English description."
                    : skill.DescriptionEnglish;
            }
        }
    }

    private void RebuildSkillGroups()
    {
        SkillAgentGroups.Clear();
        foreach (var agentGroup in Skills
            .GroupBy(skill => skill.Agent)
            .OrderBy(group => group.Key.ToString()))
        {
            var agentNode = new AgentSkillAgentGroup
            {
                Agent = agentGroup.Key,
                DisplayName = agentGroup.Key.ToString()
            };

            foreach (var pluginGroup in agentGroup
                .GroupBy(skill => string.IsNullOrWhiteSpace(skill.PluginName) ? "其它 Skill" : skill.PluginName)
                .OrderBy(group => group.Key == "其它 Skill" ? "zzz" : group.Key))
            {
                var pluginNode = new AgentSkillPluginGroup
                {
                    PluginName = pluginGroup.Key,
                    DisplayName = pluginGroup.Key
                };

                foreach (var skill in pluginGroup.OrderBy(skill => skill.Name))
                    pluginNode.Skills.Add(skill);

                agentNode.PluginGroups.Add(pluginNode);
            }

            SkillAgentGroups.Add(agentNode);
        }
    }
}
