using System.Text.Json.Serialization;

namespace CodexMeter;

public sealed record UsageSnapshot(
    string? PlanType,
    IReadOnlyList<UsageBucket> Buckets,
    DateTimeOffset FetchedAt);

public sealed record UsageBucket(
    string Id,
    string Name,
    UsageWindow? Primary,
    UsageWindow? Secondary);

public sealed record UsageWindow(
    int UsedPercent,
    long? WindowDurationMins,
    long? ResetsAt)
{
    public int RemainingPercent => Math.Clamp(100 - UsedPercent, 0, 100);
    public DateTimeOffset? ResetTime => ResetsAt is long value
        ? DateTimeOffset.FromUnixTimeSeconds(value)
        : null;
}

internal sealed class RpcEnvelope
{
    [JsonPropertyName("id")] public int? Id { get; init; }
    [JsonPropertyName("result")] public RateLimitsResponse? Result { get; init; }
    [JsonPropertyName("error")] public RpcError? Error { get; init; }
}

internal sealed class RpcError
{
    [JsonPropertyName("message")] public string? Message { get; init; }
}

internal sealed class RateLimitsResponse
{
    [JsonPropertyName("rateLimits")] public RateLimitSnapshot? RateLimits { get; init; }
    [JsonPropertyName("rateLimitsByLimitId")] public Dictionary<string, RateLimitSnapshot>? RateLimitsByLimitId { get; init; }
}

internal sealed class RateLimitSnapshot
{
    [JsonPropertyName("limitId")] public string? LimitId { get; init; }
    [JsonPropertyName("limitName")] public string? LimitName { get; init; }
    [JsonPropertyName("planType")] public string? PlanType { get; init; }
    [JsonPropertyName("primary")] public RateLimitWindow? Primary { get; init; }
    [JsonPropertyName("secondary")] public RateLimitWindow? Secondary { get; init; }
}

internal sealed class RateLimitWindow
{
    [JsonPropertyName("usedPercent")] public int UsedPercent { get; init; }
    [JsonPropertyName("windowDurationMins")] public long? WindowDurationMins { get; init; }
    [JsonPropertyName("resetsAt")] public long? ResetsAt { get; init; }

    public UsageWindow ToUsageWindow() => new(UsedPercent, WindowDurationMins, ResetsAt);
}
