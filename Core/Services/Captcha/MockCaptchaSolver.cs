using System;
using System.Threading;
using System.Threading.Tasks;
using Core.Net.Politeness;
using Microsoft.Extensions.Logging;

namespace Core.Services.Captcha;

public sealed class MockCaptchaSolver : ICaptchaSolver {
    private readonly CaptchaOptions _options;
    private readonly ILogger _logger;

    public MockCaptchaSolver(CaptchaOptions options, ILogger logger) {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public Task<string> SolveAsync(CancellationToken cancellationToken, CaptchaContext context) {
        if (context == null) {
            throw new ArgumentNullException(nameof(context));
        }

        _logger.LogInformation("Returning mock captcha token for {Url}", context.PageUrl);
        return Task.FromResult(_options.MockSolution);
    }
}
