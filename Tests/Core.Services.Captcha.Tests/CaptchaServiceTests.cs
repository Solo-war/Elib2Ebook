using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Core.Services.Captcha;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Core.Services.Captcha.Tests;

public class CaptchaServiceTests {
    [Fact]
    public async Task SolveCaptchaAsync_ReturnsMockResponse() {
        var options = new CaptchaOptions { Provider = "mock", MockSolution = "ok" };
        var logger = NullLogger.Instance;
        var provider = new MockCaptchaProvider(options, logger);
        var service = new CaptchaService(provider, options, logger);

        var result = await service.SolveCaptchaAsync(CancellationToken.None, new CaptchaRequest { Type = CaptchaType.ImageBase64, ImageBase64 = "ZGF0YQ==" });

        result.Should().Be("ok");
    }

    [Fact]
    public async Task SolveCaptchaAsync_RespectsConcurrencyLimit() {
        var options = new CaptchaOptions {
            Provider = "mock",
            MockSolution = "ok",
            MaxParallelTasks = 2,
            RequestsPerWindow = 10
        };

        var logger = NullLogger.Instance;
        var provider = new TrackingProvider(delay: TimeSpan.FromMilliseconds(100));
        var service = new CaptchaService(provider, options, logger);

        var tasks = new[] {
            service.SolveCaptchaAsync(CancellationToken.None, new CaptchaRequest { Type = CaptchaType.ImageBase64, ImageBase64 = "a" }),
            service.SolveCaptchaAsync(CancellationToken.None, new CaptchaRequest { Type = CaptchaType.ImageBase64, ImageBase64 = "b" }),
            service.SolveCaptchaAsync(CancellationToken.None, new CaptchaRequest { Type = CaptchaType.ImageBase64, ImageBase64 = "c" })
        };

        await Task.WhenAll(tasks);

        provider.MaxObservedConcurrency.Should().BeLessOrEqualTo(options.MaxParallelTasks);
    }

    [Fact]
    public async Task SolveCaptchaAsync_EnforcesRateLimitWindow() {
        var options = new CaptchaOptions {
            Provider = "mock",
            MockSolution = "ok",
            MaxParallelTasks = 3,
            RequestsPerWindow = 1,
            RateLimitWindow = TimeSpan.FromMilliseconds(150)
        };

        var logger = NullLogger.Instance;
        var provider = new TimestampProvider();
        var service = new CaptchaService(provider, options, logger);

        var task1 = service.SolveCaptchaAsync(CancellationToken.None, new CaptchaRequest { Type = CaptchaType.ImageBase64, ImageBase64 = "a" });
        var task2 = service.SolveCaptchaAsync(CancellationToken.None, new CaptchaRequest { Type = CaptchaType.ImageBase64, ImageBase64 = "b" });

        await Task.WhenAll(task1, task2);

        provider.ExecutionTimes.Should().HaveCount(2);
        (provider.ExecutionTimes[1] - provider.ExecutionTimes[0]).Should().BeGreaterOrEqualTo(options.RateLimitWindow - TimeSpan.FromMilliseconds(10));
    }

    [Fact]
    public async Task SolveCaptchaAsync_RetriesTransientErrors() {
        var options = new CaptchaOptions {
            Provider = "mock",
            MockSolution = "ok",
            MaxRetryAttempts = 3,
            InitialRetryDelay = TimeSpan.FromMilliseconds(10),
            RetryBackoffFactor = 1
        };

        var logger = NullLogger.Instance;
        var provider = new FlakyProvider(failuresBeforeSuccess: 2);
        var service = new CaptchaService(provider, options, logger);

        var result = await service.SolveCaptchaAsync(CancellationToken.None, new CaptchaRequest { Type = CaptchaType.ImageBase64, ImageBase64 = "a" });

        result.Should().Be("solved");
        provider.Attempts.Should().Be(3);
    }

    [Fact]
    public async Task TwoCaptchaProvider_SucceedsWithMockHttp() {
        var handler = new StubHttpMessageHandler(new Dictionary<string, HttpResponseMessage> {
            ["POST in.php"] = CreateJsonResponse("{\"status\":1,\"request\":\"123\"}"),
            ["GET res.php?action=get&json=1&key=test-key&id=123"] = CreateJsonResponse("{\"status\":1,\"request\":\"token\"}")
        });

        var options = new CaptchaOptions {
            Provider = "2captcha",
            ApiKey = "test-key",
            PollingInterval = TimeSpan.FromMilliseconds(10),
            RequestTimeout = TimeSpan.FromSeconds(30)
        };

        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://api.example/") };
        var logger = NullLogger.Instance;
        var provider = new TwoCaptchaProvider(options, logger, httpClient);

        var result = await provider.SolveAsync(CancellationToken.None, new CaptchaRequest {
            Type = CaptchaType.RecaptchaV2,
            SiteKey = "site",
            SiteUrl = "https://example.com"
        });

        result.Should().Be("token");
    }

    [Fact]
    public async Task CreateFromEnvironment_UsesMockProvider() {
        Environment.SetEnvironmentVariable("CAPTCHA_PROVIDER", "mock");
        Environment.SetEnvironmentVariable("CAPTCHA_MOCK_RESPONSE", "integration-ok");
        Environment.SetEnvironmentVariable("ALLOW_CAPTCHA_IN_PROD", "true");

        try {
            var service = CaptchaService.CreateFromEnvironment(NullLogger.Instance);
            var result = await service.SolveCaptchaAsync(CancellationToken.None, new CaptchaRequest { Type = CaptchaType.ImageBase64, ImageBase64 = "Zg==" });
            result.Should().Be("integration-ok");
        } finally {
            Environment.SetEnvironmentVariable("CAPTCHA_PROVIDER", null);
            Environment.SetEnvironmentVariable("CAPTCHA_MOCK_RESPONSE", null);
            Environment.SetEnvironmentVariable("ALLOW_CAPTCHA_IN_PROD", null);
        }
    }

    private static HttpResponseMessage CreateJsonResponse(string content) {
        return new HttpResponseMessage(HttpStatusCode.OK) {
            Content = new StringContent(content)
        };
    }

    private sealed class TrackingProvider : ICaptchaProvider {
        private readonly TimeSpan _delay;
        private int _current;
        private readonly object _sync = new();

        public TrackingProvider(TimeSpan delay) {
            _delay = delay;
        }

        public int MaxObservedConcurrency { get; private set; }

        public async Task<string> SolveAsync(CancellationToken cancellationToken, CaptchaRequest request) {
            var current = Interlocked.Increment(ref _current);
            try {
                lock (_sync) {
                    MaxObservedConcurrency = Math.Max(MaxObservedConcurrency, current);
                }
                await Task.Delay(_delay, cancellationToken);
                return "ok";
            } finally {
                Interlocked.Decrement(ref _current);
            }
        }
    }

    private sealed class TimestampProvider : ICaptchaProvider {
        private readonly object _sync = new();
        public List<DateTimeOffset> ExecutionTimes { get; } = new();

        public async Task<string> SolveAsync(CancellationToken cancellationToken, CaptchaRequest request) {
            lock (_sync) {
                ExecutionTimes.Add(DateTimeOffset.UtcNow);
            }
            await Task.Delay(10, cancellationToken);
            return "ok";
        }
    }

    private sealed class FlakyProvider : ICaptchaProvider {
        private int _failuresRemaining;

        public FlakyProvider(int failuresBeforeSuccess) {
            _failuresRemaining = failuresBeforeSuccess;
        }

        public int Attempts { get; private set; }

        public Task<string> SolveAsync(CancellationToken cancellationToken, CaptchaRequest request) {
            Attempts++;
            if (Interlocked.Decrement(ref _failuresRemaining) >= 0) {
                throw new CaptchaProviderException("temporary", true);
            }

            return Task.FromResult("solved");
        }
    }

    private sealed class StubHttpMessageHandler : HttpMessageHandler {
        private readonly ConcurrentDictionary<string, Queue<HttpResponseMessage>> _responses;

        public StubHttpMessageHandler(IDictionary<string, HttpResponseMessage> responses) {
            _responses = new ConcurrentDictionary<string, Queue<HttpResponseMessage>>();
            foreach (var kvp in responses) {
                _responses[kvp.Key] = new Queue<HttpResponseMessage>(new[] { kvp.Value });
            }
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) {
            var key = $"{request.Method} {request.RequestUri!.PathAndQuery.TrimStart('/')}";

            if (!_responses.TryGetValue(key, out var queue) || queue.Count == 0) {
                throw new InvalidOperationException($"No stubbed response for {key}");
            }

            var response = queue.Dequeue();
            return Task.FromResult(response);
        }
    }
}
