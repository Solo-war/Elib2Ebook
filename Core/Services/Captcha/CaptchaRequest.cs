using System.Collections.Generic;

namespace Core.Services.Captcha;

public sealed class CaptchaRequest {
    public CaptchaType Type { get; init; }

    public string? SiteUrl { get; init; }

    public string? SiteKey { get; init; }

    public string? ImageBase64 { get; init; }

    public string? ImageUrl { get; init; }

    public IDictionary<string, string>? AdditionalParameters { get; init; }

    public override string ToString() {
        var siteInfo = string.IsNullOrWhiteSpace(SiteUrl) ? string.Empty : $" siteUrl={SiteUrl}";
        var keyInfo = string.IsNullOrWhiteSpace(SiteKey) ? string.Empty : $" siteKey={SiteKey}";
        var imageInfo = Type == CaptchaType.ImageUrl && !string.IsNullOrWhiteSpace(ImageUrl)
            ? $" imageUrl={ImageUrl}"
            : string.Empty;

        return $"{Type}{siteInfo}{keyInfo}{imageInfo}".Trim();
    }
}
