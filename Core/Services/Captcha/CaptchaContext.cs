using System;

namespace Core.Services.Captcha;

public sealed record CaptchaContext {
    public required Uri PageUrl { get; init; }

    public string? SiteKey { get; init; }

    public string? CaptchaType { get; init; }

    public string? HtmlPreview { get; init; }

    public string? ScreenshotBase64 { get; init; }
}
