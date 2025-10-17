using System;
using System.Collections.Generic;

namespace Core.Services.Captcha;

public sealed record CaptchaOptions {
    public string Provider { get; init; } = "mock";

    public string? ApiKey { get; init; }

    public Uri ProviderBaseUri { get; init; } = new("https://api.2captcha.com/");

    public int MaxParallelTasks { get; init; } = 2;

    public int RequestsPerWindow { get; init; } = 10;

    public TimeSpan RateLimitWindow { get; init; } = TimeSpan.FromMinutes(1);

    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(120);

    public TimeSpan PollingInterval { get; init; } = TimeSpan.FromSeconds(5);

    public int MaxRetryAttempts { get; init; } = 3;

    public TimeSpan InitialRetryDelay { get; init; } = TimeSpan.FromSeconds(2);

    public double RetryBackoffFactor { get; init; } = 2.0;

    public bool AllowCaptchaInProd { get; init; }

    public bool AllowExternalSolver { get; init; }

    public IReadOnlyCollection<string> AllowedDomains { get; init; } = Array.Empty<string>();

    public TimeSpan CaptchaTimeout { get; init; } = TimeSpan.FromMinutes(2);

    public string MockSolution { get; init; } = "mock-solution";

    public static CaptchaOptions FromEnvironment() {
        var options = new CaptchaOptions();

        var provider = Environment.GetEnvironmentVariable("CAPTCHA_PROVIDER");
        if (!string.IsNullOrWhiteSpace(provider)) {
            options = options with { Provider = provider.Trim() };
        }

        var apiKey = Environment.GetEnvironmentVariable("CAPTCHA_API_KEY");
        if (!string.IsNullOrWhiteSpace(apiKey)) {
            options = options with { ApiKey = apiKey.Trim() };
        }

        var providerUrl = Environment.GetEnvironmentVariable("CAPTCHA_PROVIDER_URL");
        if (!string.IsNullOrWhiteSpace(providerUrl) && Uri.TryCreate(providerUrl, UriKind.Absolute, out var uri)) {
            options = options with { ProviderBaseUri = uri };
        }

        var requestsPerWindow = ReadInt("CAPTCHA_REQUESTS_PER_WINDOW", options.RequestsPerWindow);
        if (requestsPerWindow == options.RequestsPerWindow) {
            requestsPerWindow = ReadInt("CAPTCHA_REQUESTS_PER_MINUTE", requestsPerWindow);
        }

        var pollingInterval = ReadTimeSpanSeconds("CAPTCHA_POLL_INTERVAL_SEC",
            ReadTimeSpanSeconds("CAPTCHA_POLLING_INTERVAL_SECONDS", options.PollingInterval));

        var timeout = ReadTimeSpanSeconds("CAPTCHA_TIMEOUT_SEC", options.CaptchaTimeout);

        var allowedDomains = ReadList("ALLOWED_CAPTCHA_DOMAINS");

        options = options with {
            MaxParallelTasks = ReadInt("CAPTCHA_MAX_PARALLEL_TASKS", options.MaxParallelTasks),
            RequestsPerWindow = requestsPerWindow,
            RateLimitWindow = ReadTimeSpanSeconds("CAPTCHA_RATE_LIMIT_WINDOW_SECONDS", options.RateLimitWindow),
            RequestTimeout = ReadTimeSpanSeconds("CAPTCHA_REQUEST_TIMEOUT_SECONDS", options.RequestTimeout),
            PollingInterval = pollingInterval,
            MaxRetryAttempts = ReadInt("CAPTCHA_MAX_RETRY_ATTEMPTS", options.MaxRetryAttempts),
            InitialRetryDelay = ReadTimeSpanSeconds("CAPTCHA_INITIAL_RETRY_DELAY_SECONDS", options.InitialRetryDelay),
            RetryBackoffFactor = ReadDouble("CAPTCHA_RETRY_BACKOFF", options.RetryBackoffFactor),
            AllowCaptchaInProd = ReadBool("ALLOW_CAPTCHA_IN_PROD", options.AllowCaptchaInProd),
            AllowExternalSolver = ReadBool("ALLOW_EXTERNAL_CAPTCHA_SOLVER", options.AllowExternalSolver),
            CaptchaTimeout = timeout,
            AllowedDomains = allowedDomains,
            MockSolution = ReadString("CAPTCHA_MOCK_RESPONSE", options.MockSolution)
        };

        return options;
    }

    private static IReadOnlyCollection<string> ReadList(string name) {
        var value = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrWhiteSpace(value)) {
            return Array.Empty<string>();
        }

        return value
            .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
    }

    private static string ReadString(string name, string current) {
        var value = Environment.GetEnvironmentVariable(name);
        return string.IsNullOrWhiteSpace(value) ? current : value.Trim();
    }

    private static int ReadInt(string name, int current) {
        var value = Environment.GetEnvironmentVariable(name);
        return int.TryParse(value, out var parsed) && parsed > 0 ? parsed : current;
    }

    private static double ReadDouble(string name, double current) {
        var value = Environment.GetEnvironmentVariable(name);
        return double.TryParse(value, out var parsed) && parsed > 0 ? parsed : current;
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

    private static TimeSpan ReadTimeSpanSeconds(string name, TimeSpan current) {
        var value = Environment.GetEnvironmentVariable(name);
        return double.TryParse(value, out var parsed) && parsed > 0
            ? TimeSpan.FromSeconds(parsed)
            : current;
    }
}
