using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Core.Services.Captcha;

public sealed class TwoCaptchaProvider : ICaptchaProvider {
    private readonly CaptchaOptions _options;
    private readonly ILogger _logger;
    private readonly HttpClient _httpClient;

    public TwoCaptchaProvider(CaptchaOptions options, ILogger logger, HttpClient httpClient) {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));

        if (string.IsNullOrWhiteSpace(_options.ApiKey)) {
            throw new ArgumentException("CAPTCHA_API_KEY must be provided for TwoCaptcha provider.");
        }
    }

    public async Task<string> SolveAsync(CancellationToken cancellationToken, CaptchaRequest request) {
        if (request == null) {
            throw new ArgumentNullException(nameof(request));
        }

        _logger.LogInformation("Submitting captcha task to {Provider}.", _options.Provider);

        var payload = await BuildPayloadAsync(request, cancellationToken).ConfigureAwait(false);
        var formContent = new FormUrlEncodedContent(payload);

        using var submissionRequest = new HttpRequestMessage(HttpMethod.Post, "in.php") { Content = formContent };
        using var response = await _httpClient.SendAsync(submissionRequest, cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode) {
            throw new CaptchaProviderException($"Provider responded with {(int)response.StatusCode}: {body}", true);
        }

        var submission = Deserialize<TwoCaptchaResponse>(body);
        if (submission.Status != 1) {
            throw new CaptchaProviderException($"Provider rejected captcha request: {submission.Request}", IsTransientError(submission.Request));
        }

        return await PollForSolutionAsync(submission.Request, cancellationToken).ConfigureAwait(false);
    }

    private async Task<string> PollForSolutionAsync(string requestId, CancellationToken cancellationToken) {
        while (true) {
            cancellationToken.ThrowIfCancellationRequested();

            var urlBuilder = new StringBuilder("res.php?action=get");
            urlBuilder.Append("&json=1");
            urlBuilder.Append("&key=").Append(Uri.EscapeDataString(_options.ApiKey!));
            urlBuilder.Append("&id=").Append(Uri.EscapeDataString(requestId));

            using var pollResponse = await _httpClient.GetAsync(urlBuilder.ToString(), cancellationToken).ConfigureAwait(false);
            var pollBody = await pollResponse.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            if (!pollResponse.IsSuccessStatusCode) {
                throw new CaptchaProviderException($"Provider responded with {(int)pollResponse.StatusCode} during polling: {pollBody}", true);
            }

            var poll = Deserialize<TwoCaptchaResponse>(pollBody);
            if (poll.Status == 1) {
                _logger.LogInformation("Captcha task {RequestId} solved by provider {Provider}.", requestId, _options.Provider);
                return poll.Request;
            }

            if (poll.Request == "CAPCHA_NOT_READY") {
                await Task.Delay(_options.PollingInterval, cancellationToken).ConfigureAwait(false);
                continue;
            }

            throw new CaptchaProviderException($"Provider returned error while polling: {poll.Request}", IsTransientError(poll.Request));
        }
    }

    private async Task<List<KeyValuePair<string, string>>> BuildPayloadAsync(CaptchaRequest request, CancellationToken cancellationToken) {
        var payload = new List<KeyValuePair<string, string>> {
            new("key", _options.ApiKey!),
            new("json", "1")
        };

        switch (request.Type) {
            case CaptchaType.ImageBase64:
                if (string.IsNullOrWhiteSpace(request.ImageBase64)) {
                    throw new ArgumentException("ImageBase64 captcha requires ImageBase64 data to be provided.");
                }

                payload.Add(new("method", "base64"));
                payload.Add(new("body", request.ImageBase64));
                break;
            case CaptchaType.ImageUrl:
                if (string.IsNullOrWhiteSpace(request.ImageUrl)) {
                    throw new ArgumentException("ImageUrl captcha requires ImageUrl to be provided.");
                }

                payload.Add(new("method", "base64"));
                var imageData = await DownloadImageAsync(request.ImageUrl, cancellationToken).ConfigureAwait(false);
                payload.Add(new("body", Convert.ToBase64String(imageData)));
                break;
            case CaptchaType.RecaptchaV2:
                EnsureSiteInfo(request);
                payload.Add(new("method", "userrecaptcha"));
                payload.Add(new("googlekey", request.SiteKey!));
                payload.Add(new("pageurl", request.SiteUrl!));
                break;
            case CaptchaType.RecaptchaV3:
                EnsureSiteInfo(request);
                payload.Add(new("method", "userrecaptcha"));
                payload.Add(new("googlekey", request.SiteKey!));
                payload.Add(new("pageurl", request.SiteUrl!));
                payload.Add(new("version", "v3"));
                if (request.AdditionalParameters != null) {
                    if (request.AdditionalParameters.TryGetValue("action", out var action) && !string.IsNullOrWhiteSpace(action)) {
                        payload.Add(new("action", action));
                    }

                    if (request.AdditionalParameters.TryGetValue("min_score", out var minScore) && !string.IsNullOrWhiteSpace(minScore)) {
                        payload.Add(new("min_score", minScore));
                    }
                }
                break;
            default:
                throw new NotSupportedException($"Captcha type {request.Type} is not supported by the TwoCaptcha provider.");
        }

        if (request.AdditionalParameters != null) {
            foreach (var (key, value) in request.AdditionalParameters) {
                if (key.Equals("action", StringComparison.OrdinalIgnoreCase) || key.Equals("min_score", StringComparison.OrdinalIgnoreCase)) {
                    continue;
                }

                payload.Add(new(key, value));
            }
        }

        return payload;
    }

    private async Task<byte[]> DownloadImageAsync(string imageUrl, CancellationToken cancellationToken) {
        if (!Uri.TryCreate(imageUrl, UriKind.Absolute, out var uri)) {
            throw new ArgumentException("ImageUrl captcha requires an absolute ImageUrl.");
        }

        try {
            using var imageResponse = await _httpClient.GetAsync(uri, cancellationToken).ConfigureAwait(false);
            imageResponse.EnsureSuccessStatusCode();
            return await imageResponse.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        } catch (Exception ex) {
            throw new CaptchaProviderException($"Failed to download captcha image from {imageUrl}", ex, false);
        }
    }

    private static void EnsureSiteInfo(CaptchaRequest request) {
        if (string.IsNullOrWhiteSpace(request.SiteKey) || string.IsNullOrWhiteSpace(request.SiteUrl)) {
            throw new ArgumentException("Recaptcha requires both SiteKey and SiteUrl to be provided.");
        }
    }

    private static bool IsTransientError(string? errorCode) {
        return errorCode != null && (errorCode.Contains("ERROR_NO_SLOT_AVAILABLE", StringComparison.OrdinalIgnoreCase) ||
                                     errorCode.Contains("ERROR_RECAPTCHA_TIMEOUT", StringComparison.OrdinalIgnoreCase));
    }

    private static T Deserialize<T>(string body) {
        try {
            var result = JsonSerializer.Deserialize<T>(body, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (result == null) {
                throw new CaptchaProviderException("Captcha provider returned an empty response.", true);
            }

            return result;
        } catch (JsonException ex) {
            throw new CaptchaProviderException("Failed to parse response from captcha provider.", ex, true);
        }
    }

    private sealed record TwoCaptchaResponse(int Status, string Request);
}
