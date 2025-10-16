using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Core.Services.Captcha;

public sealed class MockCaptchaProvider : ICaptchaProvider {
    private readonly CaptchaOptions _options;
    private readonly ILogger _logger;

    public MockCaptchaProvider(CaptchaOptions options, ILogger logger) {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public Task<string> SolveAsync(CancellationToken cancellationToken, CaptchaRequest request) {
        if (request == null) {
            throw new ArgumentNullException(nameof(request));
        }

        _logger.LogInformation("Returning mock captcha response for {RequestType}.", request.Type);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(_options.MockSolution);
    }
}
