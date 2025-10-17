using System;
using System.Threading;
using System.Threading.Tasks;
using Core.Net.Politeness;
using Microsoft.Extensions.Logging;

namespace Core.Services.Captcha;

public sealed class ManualCaptchaSolver : ICaptchaSolver {
    private readonly ICaptchaPrompt _prompt;
    private readonly ILogger _logger;

    public ManualCaptchaSolver(ICaptchaPrompt prompt, ILogger logger) {
        _prompt = prompt ?? throw new ArgumentNullException(nameof(prompt));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<string> SolveAsync(CancellationToken cancellationToken, CaptchaContext context) {
        if (context == null) {
            throw new ArgumentNullException(nameof(context));
        }

        _logger.LogInformation("Manual captcha input requested for {Url}", context.PageUrl);

        var solution = await _prompt.RequestSolutionAsync(cancellationToken, context).ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(solution)) {
            throw new CaptchaProviderException("Manual captcha solving cancelled by user.", false);
        }

        PolitenessMetrics.ManualCaptchaSolved.Add(1);
        return solution.Trim();
    }
}
