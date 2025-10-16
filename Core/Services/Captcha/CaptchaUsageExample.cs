using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Core.Services.Captcha;

public static class CaptchaUsageExample {
    public static async Task<string?> TrySolveForOwnSiteAsync(ILogger logger, CancellationToken cancellationToken) {
        if (Environment.GetEnvironmentVariable("ALLOW_CAPTCHA_IN_PROD")?.Equals("true", StringComparison.OrdinalIgnoreCase) != true) {
            logger.LogWarning("Captcha solving is disabled because ALLOW_CAPTCHA_IN_PROD is not set to true.");
            return null;
        }

        var service = CaptchaService.CreateFromEnvironment(logger);
        var request = new CaptchaRequest {
            Type = CaptchaType.RecaptchaV2,
            SiteUrl = "https://your-own-domain.example",
            SiteKey = "your-site-key"
        };

        return await service.SolveCaptchaAsync(cancellationToken, request).ConfigureAwait(false);
    }
}
