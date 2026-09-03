using System.Diagnostics;
using System.Text.Json;

namespace CodexMeter;

public sealed class CodexUsageClient
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public async Task<UsageSnapshot> GetUsageAsync(CancellationToken cancellationToken = default)
    {
        using var process = StartCodex();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));

        try
        {
            await SendAsync(process, new
            {
                id = 1,
                method = "initialize",
                @params = new
                {
                    clientInfo = new { name = "codex-meter", version = "1.0.0" },
                    capabilities = new { }
                }
            }, timeout.Token);

            await ReadResponseAsync(process, 1, timeout.Token);
            await SendAsync(process, new { method = "initialized" }, timeout.Token);
            await SendAsync(process, new { id = 2, method = "account/rateLimits/read" }, timeout.Token);

            var response = await ReadResponseAsync(process, 2, timeout.Token);
            if (response.Error is not null)
                throw new InvalidOperationException(response.Error.Message ?? "Codex 返回了未知错误。");
            if (response.Result is null)
                throw new InvalidOperationException("Codex 没有返回额度数据。");

            return Map(response.Result);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("读取 Codex 额度超时。");
        }
        finally
        {
            if (!process.HasExited)
            {
                try { process.Kill(entireProcessTree: true); } catch { }
            }
        }
    }

    private static Process StartCodex()
    {
        var info = new ProcessStartInfo
        {
            FileName = ResolveCodexExecutable(),
            Arguments = "app-server --stdio",
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        var process = Process.Start(info)
            ?? throw new InvalidOperationException("无法启动 Codex。请先安装并登录 Codex 桌面应用。");
        return process;
    }

    internal static string ResolveCodexExecutable()
    {
        var pathDirectories = (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        foreach (var directory in pathDirectories)
        {
            try
            {
                var candidate = Path.Combine(directory.Trim('"'), "codex.exe");
                if (File.Exists(candidate))
                    return candidate;
            }
            catch (ArgumentException)
            {
                // Ignore malformed PATH entries and continue with the desktop installation.
            }
        }

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var desktopBinRoot = Path.Combine(localAppData, "OpenAI", "Codex", "bin");
        if (Directory.Exists(desktopBinRoot))
        {
            try
            {
                var desktopExecutable = Directory
                    .EnumerateFiles(desktopBinRoot, "codex.exe", SearchOption.AllDirectories)
                    .OrderByDescending(File.GetLastWriteTimeUtc)
                    .FirstOrDefault();
                if (desktopExecutable is not null)
                    return desktopExecutable;
            }
            catch (IOException)
            {
                // Fall through to the actionable error below.
            }
            catch (UnauthorizedAccessException)
            {
                // Fall through to the actionable error below.
            }
        }

        throw new FileNotFoundException(
            "未找到 Codex 命令行组件。请确认 Codex 桌面应用已安装并至少启动过一次。");
    }

    private static async Task SendAsync(Process process, object message, CancellationToken cancellationToken)
    {
        await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(message).AsMemory(), cancellationToken);
        await process.StandardInput.FlushAsync(cancellationToken);
    }

    private static async Task<RpcEnvelope> ReadResponseAsync(Process process, int expectedId, CancellationToken cancellationToken)
    {
        while (true)
        {
            var line = await process.StandardOutput.ReadLineAsync(cancellationToken);
            if (line is null)
            {
                var details = await process.StandardError.ReadToEndAsync(cancellationToken);
                throw new InvalidOperationException(string.IsNullOrWhiteSpace(details)
                    ? "Codex 本地服务意外退出。"
                    : details.Trim());
            }

            var envelope = JsonSerializer.Deserialize<RpcEnvelope>(line, JsonOptions);
            if (envelope?.Id == expectedId)
                return envelope;
        }
    }

    internal static UsageSnapshot Map(RateLimitsResponse response)
    {
        var source = response.RateLimitsByLimitId is { Count: > 0 }
            ? response.RateLimitsByLimitId
            : response.RateLimits is not null
                ? new Dictionary<string, RateLimitSnapshot> { [response.RateLimits.LimitId ?? "codex"] = response.RateLimits }
                : new Dictionary<string, RateLimitSnapshot>();

        var buckets = source
            .Select(pair => new UsageBucket(
                pair.Value.LimitId ?? pair.Key,
                DisplayName(pair.Value.LimitName, pair.Value.LimitId ?? pair.Key),
                pair.Value.Primary?.ToUsageWindow(),
                pair.Value.Secondary?.ToUsageWindow()))
            .OrderBy(bucket => bucket.Id == "codex" ? 0 : 1)
            .ThenBy(bucket => bucket.Name)
            .ToArray();

        if (buckets.Length == 0)
            throw new InvalidOperationException("当前账户没有可显示的 Codex 额度池。");

        return new UsageSnapshot(source.Values.Select(value => value.PlanType).FirstOrDefault(value => value is not null), buckets, DateTimeOffset.Now);
    }

    private static string DisplayName(string? name, string id) =>
        !string.IsNullOrWhiteSpace(name) ? name : id == "codex" ? "Codex" : id;
}
