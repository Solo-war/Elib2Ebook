using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using Core.Net.Politeness;
using Core.Services.Captcha;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Core.Services.Captcha.Tests;

public class PoliteHttpMessageHandlerTests {
    [Fact]
    public async Task RateLimit_EnforcesSpacingBetweenRequests() {
        var timestamps = new List<DateTimeOffset>();
        var inner = new RecordingHandler((_, _) => {
            timestamps.Add(DateTimeOffset.UtcNow);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("ok") };
        });

        var options = new PolitenessOptions {
            MaxRequestsPerSecondPerHost = 5,
            MinJitter = TimeSpan.Zero,
            MaxJitter = TimeSpan.Zero,
            MaxConcurrencyPerHost = 1,
            RespectRobotsTxt = false,
            RetryPolicy = new RetryPolicyOptions { MaxRetries = 0, InitialDelay = TimeSpan.Zero, BackoffFactor = 1, StatusCodes = new HashSet<HttpStatusCode>() }
        };

        var handler = new PoliteHttpMessageHandler(inner, options, new StubCaptchaSolver(), NullLogger.Instance);
        using var client = new HttpClient(handler);

        await client.GetAsync("https://example.com/page1");
        await client.GetAsync("https://example.com/page2");

        timestamps.Should().HaveCount(2);
        (timestamps[1] - timestamps[0]).Should().BeGreaterThanOrEqualTo(TimeSpan.FromMilliseconds(180));
    }

    [Fact]
    public async Task RetryAfter_IsRespected() {
        var attempts = 0;
        var inner = new RecordingHandler((request, _) => {
            attempts++;
            if (attempts == 1) {
                var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
                response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromMilliseconds(200));
                return response;
            }

            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("ok") };
        });

        var options = new PolitenessOptions {
            MaxRequestsPerSecondPerHost = 100,
            MinJitter = TimeSpan.Zero,
            MaxJitter = TimeSpan.Zero,
            MaxConcurrencyPerHost = 1,
            RespectRobotsTxt = false,
            RetryPolicy = new RetryPolicyOptions { MaxRetries = 3, InitialDelay = TimeSpan.FromMilliseconds(50), BackoffFactor = 1.2, StatusCodes = new HashSet<HttpStatusCode> { HttpStatusCode.TooManyRequests } }
        };

        var handler = new PoliteHttpMessageHandler(inner, options, new StubCaptchaSolver(), NullLogger.Instance);
        using var client = new HttpClient(handler);

        var before = DateTimeOffset.UtcNow;
        var response = await client.GetAsync("https://retry.example.com/");
        var after = DateTimeOffset.UtcNow;

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        attempts.Should().Be(2);
        (after - before).Should().BeGreaterThanOrEqualTo(TimeSpan.FromMilliseconds(180));
    }

    [Fact]
    public async Task RobotsTxt_DisallowThrows() {
        var inner = new RobotsAwareHandler();
        var options = new PolitenessOptions {
            MaxRequestsPerSecondPerHost = 100,
            MinJitter = TimeSpan.Zero,
            MaxJitter = TimeSpan.Zero,
            MaxConcurrencyPerHost = 1,
            RespectRobotsTxt = true,
            RetryPolicy = RetryPolicyOptions.Default
        };

        var handler = new PoliteHttpMessageHandler(inner, options, new StubCaptchaSolver(), NullLogger.Instance);
        using var client = new HttpClient(handler);

        await Assert.ThrowsAsync<InvalidOperationException>(() => client.GetAsync("https://robots.example.com/blocked"));
    }

    [Fact]
    public async Task CaptchaDetection_ReturnsErrorWhenSolverFails() {
        var solver = new FailingCaptchaSolver();
        var inner = new CaptchaHandler();
        var options = new PolitenessOptions {
            MaxRequestsPerSecondPerHost = 100,
            MinJitter = TimeSpan.Zero,
            MaxJitter = TimeSpan.Zero,
            MaxConcurrencyPerHost = 1,
            RespectRobotsTxt = false,
            RetryPolicy = RetryPolicyOptions.Default,
            MaxCaptchaRetries = 1
        };

        var handler = new PoliteHttpMessageHandler(inner, options, solver, NullLogger.Instance);
        using var client = new HttpClient(handler);

        await Assert.ThrowsAsync<ErrCaptchaRequiredException>(() => client.GetAsync("https://captcha.example.com/"));
    }

    [Fact]
    public async Task CaptchaDetection_SolvesAndRetries() {
        var solver = new RecordingCaptchaSolver("solved-token");
        var inner = new CaptchaSuccessHandler();
        var options = new PolitenessOptions {
            MaxRequestsPerSecondPerHost = 100,
            MinJitter = TimeSpan.Zero,
            MaxJitter = TimeSpan.Zero,
            MaxConcurrencyPerHost = 1,
            RespectRobotsTxt = false,
            RetryPolicy = RetryPolicyOptions.Default,
            MaxCaptchaRetries = 2
        };

        var handler = new PoliteHttpMessageHandler(inner, options, solver, NullLogger.Instance);
        using var client = new HttpClient(handler);

        var response = await client.GetAsync("https://captcha-success.example.com/");
        var body = await response.Content.ReadAsStringAsync();

        body.Should().Be("ok");
        solver.Contexts.Should().HaveCount(1);
        inner.Tokens.Should().Contain("solved-token");
    }

    private sealed class RecordingHandler : HttpMessageHandler {
        private readonly Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> _handler;

        public RecordingHandler(Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> handler) {
            _handler = handler;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) {
            return Task.FromResult(_handler(request, cancellationToken));
        }
    }

    private sealed class StubCaptchaSolver : ICaptchaSolver {
        public Task<string> SolveAsync(CancellationToken cancellationToken, CaptchaContext context) {
            return Task.FromResult("token");
        }
    }

    private sealed class RobotsAwareHandler : HttpMessageHandler {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) {
            if (request.RequestUri!.AbsolutePath.Equals("/robots.txt", StringComparison.OrdinalIgnoreCase)) {
                var content = "User-agent: *\nDisallow: /blocked\n";
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(content) });
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("ok") });
        }
    }

    private sealed class FailingCaptchaSolver : ICaptchaSolver {
        public Task<string> SolveAsync(CancellationToken cancellationToken, CaptchaContext context) {
            throw new CaptchaProviderException("failed", false);
        }
    }

    private sealed class CaptchaHandler : HttpMessageHandler {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) {
            var html = "<html><body><div class='g-recaptcha' data-sitekey='abc'></div></body></html>";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(html) });
        }
    }

    private sealed class RecordingCaptchaSolver : ICaptchaSolver {
        private readonly string _token;

        public RecordingCaptchaSolver(string token) {
            _token = token;
        }

        public List<CaptchaContext> Contexts { get; } = new();

        public Task<string> SolveAsync(CancellationToken cancellationToken, CaptchaContext context) {
            Contexts.Add(context);
            return Task.FromResult(_token);
        }
    }

    private sealed class CaptchaSuccessHandler : HttpMessageHandler {
        public List<string> Tokens { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) {
            if (request.Headers.TryGetValues("X-Captcha-Token", out var values)) {
                Tokens.AddRange(values);
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("ok") });
            }

            var html = "<html><body><div class='g-recaptcha' data-sitekey='abc'></div></body></html>";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(html) });
        }
    }
}
