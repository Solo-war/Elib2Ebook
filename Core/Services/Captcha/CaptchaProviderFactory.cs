using System;
using System.Net.Http;
using Microsoft.Extensions.Logging;

namespace Core.Services.Captcha;

public static class CaptchaProviderFactory {
    public static ICaptchaProvider Create(CaptchaOptions options, ILogger logger, HttpClient? httpClient = null) {
        if (options == null) {
            throw new ArgumentNullException(nameof(options));
        }

        if (logger == null) {
            throw new ArgumentNullException(nameof(logger));
        }

        var providerName = options.Provider?.Trim().ToLowerInvariant() ?? "mock";
        if (providerName == "external") {
            providerName = "2captcha";
        }
        return providerName switch {
            "2captcha" => new TwoCaptchaProvider(options, logger, httpClient ?? CreateHttpClient(options)),
            "anticaptcha" => new TwoCaptchaProvider(options, logger, httpClient ?? CreateHttpClient(options)),
            "mock" => new MockCaptchaProvider(options, logger),
            _ => throw new NotSupportedException($"Unknown captcha provider '{options.Provider}'."),
        };
    }

    private static HttpClient CreateHttpClient(CaptchaOptions options) {
        var client = new HttpClient { BaseAddress = options.ProviderBaseUri };
        client.Timeout = options.RequestTimeout;
        return client;
    }
}
