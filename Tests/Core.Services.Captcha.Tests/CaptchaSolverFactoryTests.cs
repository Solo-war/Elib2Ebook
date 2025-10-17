using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Core.Services.Captcha;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Core.Services.Captcha.Tests;

public class CaptchaSolverFactoryTests {
    [Fact]
    public async Task ManualSolver_UsesPrompt() {
        var prompt = new TestPrompt("token");
        var solver = new ManualCaptchaSolver(prompt, NullLogger.Instance);
        var context = new CaptchaContext { PageUrl = new Uri("https://example.com"), CaptchaType = "recaptcha" };

        var result = await solver.SolveAsync(CancellationToken.None, context);
        result.Should().Be("token");
        prompt.Calls.Should().Be(1);
    }

    [Fact]
    public void ManualSolver_ThrowsWhenCancelled() {
        var prompt = new TestPrompt(null);
        var solver = new ManualCaptchaSolver(prompt, NullLogger.Instance);
        var context = new CaptchaContext { PageUrl = new Uri("https://example.com") };

        Func<Task> act = () => solver.SolveAsync(CancellationToken.None, context);
        act.Should().Throw<CaptchaProviderException>();
    }

    [Fact]
    public void ExternalSolver_DisabledWithoutFlag() {
        var options = new CaptchaOptions {
            Provider = "external",
            AllowExternalSolver = false,
            AllowedDomains = new[] { "example.com" }
        };

        Action act = () => CaptchaSolverFactory.Create(options, NullLogger.Instance, null);
        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public async Task ExternalSolver_ValidatesDomain() {
        var options = new CaptchaOptions {
            Provider = "external",
            AllowExternalSolver = true,
            AllowedDomains = new[] { "allowed.com" },
            ApiKey = "key"
        };

        var solver = (ExternalCaptchaSolver)CaptchaSolverFactory.Create(options, NullLogger.Instance, null, new HttpClient { BaseAddress = options.ProviderBaseUri });
        var context = new CaptchaContext { PageUrl = new Uri("https://blocked.com") };

        Func<Task> act = () => solver.SolveAsync(CancellationToken.None, context);
        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    private sealed class TestPrompt : ICaptchaPrompt {
        private readonly string? _result;

        public TestPrompt(string? result) {
            _result = result;
        }

        public int Calls { get; private set; }

        public Task<string?> RequestSolutionAsync(CancellationToken cancellationToken, CaptchaContext context) {
            Calls++;
            return Task.FromResult<string?>(_result);
        }
    }
}
