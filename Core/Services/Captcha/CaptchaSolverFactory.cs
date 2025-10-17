using System;
using System.Net.Http;
using Microsoft.Extensions.Logging;

namespace Core.Services.Captcha;

public static class CaptchaSolverFactory {
    public static ICaptchaSolver Create(CaptchaOptions options, ILogger logger, ICaptchaPrompt? prompt = null, HttpClient? httpClient = null) {
        if (options == null) {
            throw new ArgumentNullException(nameof(options));
        }

        if (logger == null) {
            throw new ArgumentNullException(nameof(logger));
        }

        var provider = options.Provider?.Trim().ToLowerInvariant() ?? "mock";

        return provider switch {
            "mock" => new MockCaptchaSolver(options, logger),
            "manual" => CreateManualSolver(prompt, logger),
            "external" => CreateExternalSolver(options, logger, httpClient),
            "2captcha" => CreateExternalSolver(options, logger, httpClient),
            "anticaptcha" => CreateExternalSolver(options, logger, httpClient),
            _ => throw new NotSupportedException($"Unsupported captcha provider '{options.Provider}'.")
        };
    }

    private static ICaptchaSolver CreateManualSolver(ICaptchaPrompt? prompt, ILogger logger) {
        if (prompt == null) {
            throw new InvalidOperationException("Manual captcha solver requires a prompt implementation.");
        }

        return new ManualCaptchaSolver(prompt, logger);
    }

    private static ICaptchaSolver CreateExternalSolver(CaptchaOptions options, ILogger logger, HttpClient? httpClient) {
        if (!options.AllowExternalSolver) {
            throw new InvalidOperationException("External captcha solver is disabled. Set ALLOW_EXTERNAL_CAPTCHA_SOLVER=true to enable.");
        }

        if (options.AllowedDomains == null || options.AllowedDomains.Count == 0) {
            throw new InvalidOperationException("ALLOWED_CAPTCHA_DOMAINS must contain at least one domain for external solver.");
        }

        var provider = CaptchaProviderFactory.Create(options, logger, httpClient);
        var service = new CaptchaService(provider, options, logger);
        return new ExternalCaptchaSolver(service, options, logger);
    }
}
