using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Core.Services.Captcha;

public sealed class ExternalCaptchaSolver : ICaptchaSolver {
    private readonly CaptchaService _service;
    private readonly CaptchaOptions _options;
    private readonly ILogger _logger;

    public ExternalCaptchaSolver(CaptchaService service, CaptchaOptions options, ILogger logger) {
        _service = service ?? throw new ArgumentNullException(nameof(service));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<string> SolveAsync(CancellationToken cancellationToken, CaptchaContext context) {
        if (context == null) {
            throw new ArgumentNullException(nameof(context));
        }

        if (!_options.AllowExternalSolver) {
            throw new InvalidOperationException("External captcha solver is disabled by configuration.");
        }

        if (!IsDomainAllowed(context.PageUrl.Host)) {
            throw new InvalidOperationException($"External captcha solver is not allowed for domain {context.PageUrl.Host}.");
        }

        var request = new CaptchaRequest {
            Type = ResolveCaptchaType(context),
            SiteUrl = context.PageUrl.ToString(),
            SiteKey = context.SiteKey
        };

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        linked.CancelAfter(_options.CaptchaTimeout);

        _logger.LogInformation("Sending captcha to external provider for {Host}", context.PageUrl.Host);
        return await _service.SolveCaptchaAsync(linked.Token, request).ConfigureAwait(false);
    }

    private bool IsDomainAllowed(string host) {
        if (_options.AllowedDomains == null || _options.AllowedDomains.Count == 0) {
            return false;
        }

        return _options.AllowedDomains.Any(domain =>
            host.Equals(domain, StringComparison.OrdinalIgnoreCase) ||
            host.EndsWith($".{domain}", StringComparison.OrdinalIgnoreCase));
    }

    private static CaptchaType ResolveCaptchaType(CaptchaContext context) {
        var type = context.CaptchaType?.Trim().ToLowerInvariant();
        return type switch {
            "recaptcha" => CaptchaType.RecaptchaV2,
            "recaptcha_v2" => CaptchaType.RecaptchaV2,
            "hcaptcha" => CaptchaType.RecaptchaV2,
            "image" => CaptchaType.ImageUrl,
            _ => CaptchaType.RecaptchaV2
        };
    }
}
