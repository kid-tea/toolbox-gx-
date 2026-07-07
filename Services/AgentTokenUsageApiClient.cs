using System.Globalization;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using Toolbox.Models;

namespace Toolbox.Services;

public interface IAgentTokenUsageApiClient
{
    Task<IReadOnlyList<AgentTokenUsageRecord>> GetUsageAsync(
        AgentTokenUsageApiOptions options,
        CancellationToken cancellationToken = default);
}

public sealed class AgentTokenUsageApiClient : IAgentTokenUsageApiClient
{
    private readonly HttpClient _httpClient;

    public AgentTokenUsageApiClient()
        : this(new HttpClient())
    {
    }

    public AgentTokenUsageApiClient(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public async Task<IReadOnlyList<AgentTokenUsageRecord>> GetUsageAsync(
        AgentTokenUsageApiOptions options,
        CancellationToken cancellationToken = default)
    {
        if (string.Equals(options.Provider, "Anthropic", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(options.Provider, "Claude", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(options.Provider, "Claude Code", StringComparison.OrdinalIgnoreCase))
            return await GetAnthropicClaudeCodeUsageAsync(options, cancellationToken);

        return await GetOpenAiUsageAsync(options, cancellationToken);
    }

    private async Task<IReadOnlyList<AgentTokenUsageRecord>> GetOpenAiUsageAsync(
        AgentTokenUsageApiOptions options,
        CancellationToken cancellationToken)
    {
        var records = new List<AgentTokenUsageRecord>();
        var start = options.Start.ToUnixTimeSeconds();
        var end = options.End.ToUnixTimeSeconds();
        var url =
            "https://api.openai.com/v1/organization/usage/completions" +
            $"?start_time={start}&end_time={end}&bucket_width=1d&group_by[]=model";

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.ApiKey);

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        if (!document.RootElement.TryGetProperty("data", out var data) ||
            data.ValueKind != JsonValueKind.Array)
            return records;

        foreach (var bucket in data.EnumerateArray())
        {
            var date = ReadUnixDate(bucket, "start_time") ?? options.Start;
            if (!bucket.TryGetProperty("results", out var results) ||
                results.ValueKind != JsonValueKind.Array)
                continue;

            foreach (var result in results.EnumerateArray())
            {
                var model = ReadString(result, "model");
                if (!ModelMatches(model, options.Model))
                    continue;

                var input = ReadLong(result, "input_tokens");
                var output = ReadLong(result, "output_tokens");
                var cached = ReadLong(result, "input_cached_tokens");
                if (cached == 0)
                    cached = ReadLong(result, "cached_input_tokens");
                var requestCount = (int)Math.Min(int.MaxValue, ReadLong(result, "num_model_requests"));
                var total = input + output;
                if (total <= 0) continue;

                records.Add(new AgentTokenUsageRecord
                {
                    Agent = options.Agent,
                    Date = date.ToLocalTime(),
                    Model = string.IsNullOrWhiteSpace(model) ? NormalizeModel(options.Model) : model,
                    Tokens = total,
                    InputTokens = input,
                    CachedInputTokens = cached,
                    OutputTokens = output,
                    MessageCount = Math.Max(1, requestCount),
                    ConversationTitle = "OpenAI Usage API",
                    Provenance = AgentDataProvenance.RemoteUsageApi,
                    EstimatedCostUsd = EstimateCost(total)
                });
            }
        }

        return records;
    }

    private async Task<IReadOnlyList<AgentTokenUsageRecord>> GetAnthropicClaudeCodeUsageAsync(
        AgentTokenUsageApiOptions options,
        CancellationToken cancellationToken)
    {
        var records = new List<AgentTokenUsageRecord>();
        for (var day = options.Start.UtcDateTime.Date; day < options.End.UtcDateTime.Date; day = day.AddDays(1))
        {
            var url = "https://api.anthropic.com/v1/organizations/usage_report/claude_code" +
                      $"?starting_at={day:yyyy-MM-dd}&limit=1000";
            string? nextPage = null;

            do
            {
                var pageUrl = nextPage == null
                    ? url
                    : $"{url}&page={Uri.EscapeDataString(nextPage)}";
                using var request = new HttpRequestMessage(HttpMethod.Get, pageUrl);
                request.Headers.Add("x-api-key", options.ApiKey);
                request.Headers.Add("anthropic-version", "2023-06-01");

                using var response = await _httpClient.SendAsync(request, cancellationToken);
                response.EnsureSuccessStatusCode();

                await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
                using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
                if (!document.RootElement.TryGetProperty("data", out var data) ||
                    data.ValueKind != JsonValueKind.Array)
                    break;

                foreach (var actorUsage in data.EnumerateArray())
                {
                    var usageDate = ReadDateString(actorUsage, "date") ?? new DateTimeOffset(day, TimeSpan.Zero);
                    if (!actorUsage.TryGetProperty("model_breakdown", out var breakdown) ||
                        breakdown.ValueKind != JsonValueKind.Array)
                        continue;

                    foreach (var modelUsage in breakdown.EnumerateArray())
                    {
                        var model = ReadString(modelUsage, "model");
                        if (!ModelMatches(model, options.Model))
                            continue;
                        if (!modelUsage.TryGetProperty("tokens", out var tokens) ||
                            tokens.ValueKind != JsonValueKind.Object)
                            continue;

                        var input = ReadLong(tokens, "input");
                        var output = ReadLong(tokens, "output");
                        var cacheRead = ReadLong(tokens, "cache_read");
                        var cacheWrite = ReadLong(tokens, "cache_creation");
                        var total = input + output + cacheRead + cacheWrite;
                        if (total <= 0) continue;

                        records.Add(new AgentTokenUsageRecord
                        {
                            Agent = AgentToolKind.ClaudeCode,
                            Date = usageDate.ToLocalTime(),
                            Model = string.IsNullOrWhiteSpace(model) ? NormalizeModel(options.Model) : model,
                            Tokens = total,
                            InputTokens = input,
                            CachedInputTokens = cacheRead,
                            CacheWriteInputTokens = cacheWrite,
                            OutputTokens = output,
                            SessionCount = ReadInt(actorUsage, "core_metrics", "num_sessions"),
                            ConversationTitle = "Anthropic Claude Code Usage API",
                            Provenance = AgentDataProvenance.RemoteUsageApi,
                            EstimatedCostUsd = EstimateCost(total)
                        });
                    }
                }

                nextPage = ReadString(document.RootElement, "next_page");
                var hasMore = ReadBool(document.RootElement, "has_more");
                if (!hasMore)
                    break;
            }
            while (!string.IsNullOrWhiteSpace(nextPage));
        }

        return records;
    }

    private static bool ModelMatches(string model, string requested)
    {
        if (string.IsNullOrWhiteSpace(requested) ||
            string.Equals(requested, "all", StringComparison.OrdinalIgnoreCase))
            return true;

        return string.Equals(model, requested, StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeModel(string model)
    {
        return string.IsNullOrWhiteSpace(model) ? "unknown" : model.Trim();
    }

    private static DateTimeOffset? ReadUnixDate(JsonElement element, string propertyName)
    {
        var value = ReadLong(element, propertyName);
        return value > 0 ? DateTimeOffset.FromUnixTimeSeconds(value) : null;
    }

    private static DateTimeOffset? ReadDateString(JsonElement element, string propertyName)
    {
        var value = ReadString(element, propertyName);
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed))
            return parsed;
        if (DateTime.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date))
            return new DateTimeOffset(date, TimeSpan.Zero);
        return null;
    }

    private static string ReadString(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property))
            return "";
        return property.ValueKind == JsonValueKind.String ? property.GetString() ?? "" : property.ToString();
    }

    private static long ReadLong(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property))
            return 0;
        return property.ValueKind switch
        {
            JsonValueKind.Number when property.TryGetInt64(out var value) => value,
            JsonValueKind.String when long.TryParse(property.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) => value,
            _ => 0
        };
    }

    private static int ReadInt(JsonElement element, string objectName, string propertyName)
    {
        if (!element.TryGetProperty(objectName, out var obj) || obj.ValueKind != JsonValueKind.Object)
            return 0;
        return (int)Math.Min(int.MaxValue, ReadLong(obj, propertyName));
    }

    private static bool ReadBool(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property))
            return false;
        return property.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String when bool.TryParse(property.GetString(), out var value) => value,
            _ => false
        };
    }

    private static decimal EstimateCost(long tokens)
    {
        return Math.Round(tokens / 1_000_000m * 3m, 4);
    }
}
