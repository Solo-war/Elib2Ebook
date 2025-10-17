using System;
using System.Collections.Generic;

namespace Core.Net.Politeness;

public sealed record PolitenessOptions {
    public double MaxRequestsPerSecondPerHost { get; init; } = 0.35;

    public int MaxConcurrencyPerHost { get; init; } = 1;

    public TimeSpan MinJitter { get; init; } = TimeSpan.FromMilliseconds(200);

    public TimeSpan MaxJitter { get; init; } = TimeSpan.FromMilliseconds(800);

    public TimeSpan RobotsCacheTtl { get; init; } = TimeSpan.FromHours(24);

    public bool RespectRobotsTxt { get; init; } = true;

    public RetryPolicyOptions RetryPolicy { get; init; } = RetryPolicyOptions.Default;

    public IReadOnlyList<string> UserAgents { get; init; } = DefaultUserAgents;

    public string AcceptLanguage { get; init; } = "en-US,en;q=0.9";

    public string Accept { get; init; } = "text/html,application/xhtml+xml,application/xml;q=0.9,image/avif,image/webp,image/apng,*/*;q=0.8";

    public string DntHeader { get; init; } = "1";

    public int MaxCaptchaRetries { get; init; } = 3;

    public static IReadOnlyList<string> DefaultUserAgents { get; } = new[] {
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/123.0.0.0 Safari/537.36",
        "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/17.3 Safari/605.1.15",
        "Mozilla/5.0 (X11; Ubuntu; Linux x86_64; rv:123.0) Gecko/20100101 Firefox/123.0",
        "Mozilla/5.0 (iPhone; CPU iPhone OS 17_0 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/17.0 Mobile/15E148 Safari/604.1"
    };

    public static PolitenessOptions FromEnvironment() {
        var options = new PolitenessOptions();

        var rps = ReadDouble("POLITENESS_MAX_RPS_PER_HOST", options.MaxRequestsPerSecondPerHost);
        var minJitter = ReadInt("POLITENESS_JITTER_MIN_MS", (int)options.MinJitter.TotalMilliseconds);
        var maxJitter = ReadInt("POLITENESS_JITTER_MAX_MS", (int)options.MaxJitter.TotalMilliseconds);
        if (maxJitter < minJitter) {
            (minJitter, maxJitter) = (maxJitter, minJitter);
        }

        var retryPolicy = options.RetryPolicy with {
            MaxRetries = ReadInt("POLITENESS_MAX_RETRIES", options.RetryPolicy.MaxRetries),
            InitialDelay = ReadTimeSpan("POLITENESS_INITIAL_RETRY_DELAY_MS", options.RetryPolicy.InitialDelay),
            BackoffFactor = ReadDouble("POLITENESS_RETRY_BACKOFF", options.RetryPolicy.BackoffFactor)
        };

        var userAgents = ReadList("POLITENESS_USER_AGENTS", options.UserAgents);

        return options with {
            MaxRequestsPerSecondPerHost = rps,
            MaxConcurrencyPerHost = ReadInt("POLITENESS_MAX_CONCURRENCY_PER_HOST", options.MaxConcurrencyPerHost),
            MinJitter = TimeSpan.FromMilliseconds(minJitter),
            MaxJitter = TimeSpan.FromMilliseconds(maxJitter),
            RobotsCacheTtl = ReadTimeSpan("POLITENESS_ROBOTS_CACHE_TTL_SECONDS", options.RobotsCacheTtl),
            RespectRobotsTxt = ReadBool("POLITENESS_RESPECT_ROBOTS", options.RespectRobotsTxt),
            RetryPolicy = retryPolicy,
            UserAgents = userAgents,
            AcceptLanguage = ReadString("POLITENESS_ACCEPT_LANGUAGE", options.AcceptLanguage),
            Accept = ReadString("POLITENESS_ACCEPT", options.Accept),
            DntHeader = ReadString("POLITENESS_DNT", options.DntHeader),
            MaxCaptchaRetries = ReadInt("POLITENESS_MAX_CAPTCHA_RETRIES", options.MaxCaptchaRetries)
        };
    }

    private static bool ReadBool(string name, bool current) {
        var value = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrWhiteSpace(value)) {
            return current;
        }

        return value.Equals("1", StringComparison.OrdinalIgnoreCase) ||
               value.Equals("true", StringComparison.OrdinalIgnoreCase) ||
               value.Equals("yes", StringComparison.OrdinalIgnoreCase);
    }

    private static int ReadInt(string name, int current) {
        var value = Environment.GetEnvironmentVariable(name);
        return int.TryParse(value, out var parsed) && parsed > 0 ? parsed : current;
    }

    private static double ReadDouble(string name, double current) {
        var value = Environment.GetEnvironmentVariable(name);
        return double.TryParse(value, out var parsed) && parsed > 0 ? parsed : current;
    }

    private static TimeSpan ReadTimeSpan(string name, TimeSpan current) {
        var value = Environment.GetEnvironmentVariable(name);
        return double.TryParse(value, out var parsed) && parsed > 0
            ? TimeSpan.FromMilliseconds(parsed)
            : current;
    }

    private static string ReadString(string name, string current) {
        var value = Environment.GetEnvironmentVariable(name);
        return string.IsNullOrWhiteSpace(value) ? current : value.Trim();
    }

    private static IReadOnlyList<string> ReadList(string name, IReadOnlyList<string> current) {
        var value = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrWhiteSpace(value)) {
            return current;
        }

        var list = new List<string>();
        foreach (var item in value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)) {
            if (!string.IsNullOrWhiteSpace(item)) {
                list.Add(item);
            }
        }

        return list.Count == 0 ? current : list;
    }
}
