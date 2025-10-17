using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Core.Services.Captcha;
using Microsoft.Extensions.Logging;

namespace Core.Net.Politeness;

public sealed class PoliteHttpMessageHandler : DelegatingHandler {
    private static readonly HttpRequestOptionsKey<bool> SkipPolitenessKey = new("Politeness.Skip");

    private readonly PolitenessOptions _options;
    private readonly ICaptchaSolver _captchaSolver;
    private readonly ILogger _logger;
    private readonly TimeProvider _timeProvider;
    private readonly ConcurrentDictionary<string, HostPolitenessState> _hostStates = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, RobotsCacheEntry> _robotsCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, CachedResponse> _responseCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Random _random = Random.Shared;

    public PoliteHttpMessageHandler(HttpMessageHandler innerHandler, PolitenessOptions options, ICaptchaSolver captchaSolver, ILogger logger, TimeProvider? timeProvider = null)
        : base(innerHandler ?? throw new ArgumentNullException(nameof(innerHandler))) {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _captchaSolver = captchaSolver ?? throw new ArgumentNullException(nameof(captchaSolver));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) {
        if (request == null) {
            throw new ArgumentNullException(nameof(request));
        }

        if (request.Options.TryGetValue(SkipPolitenessKey, out var skip) && skip) {
            return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }

        if (request.RequestUri == null) {
            return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }

        var host = request.RequestUri.Host;
        var hostState = _hostStates.GetOrAdd(host, _ => new HostPolitenessState(_options.MaxConcurrencyPerHost));
        await hostState.Concurrency.WaitAsync(cancellationToken).ConfigureAwait(false);

        try {
            await WaitForTurnAsync(hostState, cancellationToken).ConfigureAwait(false);
            await ApplyRobotsPolicyAsync(request, cancellationToken).ConfigureAwait(false);

            var attempt = 0;
            var cloned = await CloneRequestAsync(request).ConfigureAwait(false);
            while (true) {
                cancellationToken.ThrowIfCancellationRequested();
                attempt++;

                PrepareHeaders(cloned);
                ApplyCachingHeaders(cloned);

                var response = await base.SendAsync(cloned, cancellationToken).ConfigureAwait(false);
                response = await HandleCachingAsync(request, response).ConfigureAwait(false);

                if (await TryHandleCaptchaAsync(request, response, attempt, cancellationToken).ConfigureAwait(false) is { } retryRequest) {
                    response.Dispose();
                    cloned = retryRequest;
                    continue;
                }

                if (await TryScheduleRetryAsync(response, attempt, cancellationToken).ConfigureAwait(false)) {
                    response.Dispose();
                    cloned = await CloneRequestAsync(request).ConfigureAwait(false);
                    continue;
                }

                return response;
            }
        } finally {
            hostState.Concurrency.Release();
        }
    }

    private async Task WaitForTurnAsync(HostPolitenessState state, CancellationToken cancellationToken) {
        var now = _timeProvider.GetUtcNow();
        var minInterval = _options.MaxRequestsPerSecondPerHost > 0
            ? TimeSpan.FromSeconds(1d / _options.MaxRequestsPerSecondPerHost)
            : TimeSpan.Zero;

        var jitter = TimeSpan.FromMilliseconds(_random.Next((int)_options.MinJitter.TotalMilliseconds, (int)_options.MaxJitter.TotalMilliseconds + 1));

        var waitUntil = state.Reserve(now, minInterval + jitter);
        var delay = waitUntil - now;
        if (delay > TimeSpan.Zero) {
            _logger.LogDebug("Delaying request for {Delay} due to politeness policy.", delay);
            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task ApplyRobotsPolicyAsync(HttpRequestMessage request, CancellationToken cancellationToken) {
        if (!_options.RespectRobotsTxt) {
            return;
        }

        var uri = request.RequestUri!;
        var host = uri.Host;
        var entry = await GetRobotsAsync(uri, cancellationToken).ConfigureAwait(false);
        if (entry == null) {
            return;
        }

        if (entry.Rules.IsDisallowed(uri.AbsolutePath)) {
            _logger.LogWarning("Blocked by robots.txt for {Url}", uri);
            PolitenessMetrics.BlockedByRobots.Add(1);
            throw new InvalidOperationException($"Access to {uri} is disallowed by robots.txt.");
        }

        if (entry.Rules.CrawlDelay.HasValue) {
            var delay = entry.Rules.CrawlDelay.Value;
            if (delay > TimeSpan.Zero) {
                var state = _hostStates.GetOrAdd(host, _ => new HostPolitenessState(_options.MaxConcurrencyPerHost));
                state.Defer(delay);
            }
        }

        if (entry.Rules.Sitemaps.Count > 0) {
            _logger.LogDebug("Robots sitemap(s) for {Host}: {Sitemaps}", host, string.Join(",", entry.Rules.Sitemaps));
        }
    }

    private void PrepareHeaders(HttpRequestMessage request) {
        if (request.Headers.UserAgent.Count == 0) {
            var pool = _options.UserAgents.Count == 0 ? PolitenessOptions.DefaultUserAgents : _options.UserAgents;
            var index = _random.Next(pool.Count);
            var agent = pool[index];
            request.Headers.UserAgent.ParseAdd(agent);
        }

        if (!request.Headers.Contains("Accept-Language")) {
            request.Headers.TryAddWithoutValidation("Accept-Language", _options.AcceptLanguage);
        }

        if (!request.Headers.Contains("Accept")) {
            request.Headers.TryAddWithoutValidation("Accept", _options.Accept);
        }

        if (!request.Headers.Contains("DNT")) {
            request.Headers.TryAddWithoutValidation("DNT", _options.DntHeader);
        }
    }

    private async Task<HttpRequestMessage> TryHandleCaptchaAsync(HttpRequestMessage originalRequest, HttpResponseMessage response, int attempt, CancellationToken cancellationToken) {
        if (!IsHtml(response)) {
            return null;
        }

        var snapshot = await ContentSnapshot.CreateAsync(response).ConfigureAwait(false);
        var html = snapshot.ReadAsString();
        if (!CaptchaDetector.TryDetect(originalRequest.RequestUri!, html, out var context)) {
            snapshot.Restore(response);
            return null;
        }

        snapshot.Restore(response);
        PolitenessMetrics.CaptchaDetected.Add(1);
        _logger.LogWarning("Captcha detected at {Url}", context.PageUrl);

        if (attempt >= _options.MaxCaptchaRetries) {
            throw new ErrCaptchaRequiredException(context);
        }

        string token;
        try {
            token = await _captchaSolver.SolveAsync(cancellationToken, context).ConfigureAwait(false);
        } catch (CaptchaProviderException ex) {
            _logger.LogWarning(ex, "Captcha solver returned error.");
            throw new ErrCaptchaRequiredException(context);
        }

        _logger.LogInformation("Captcha solved for {Url}, repeating request.", context.PageUrl);
        return await CloneRequestAsync(originalRequest, token).ConfigureAwait(false);
    }

    private async Task<bool> TryScheduleRetryAsync(HttpResponseMessage response, int attempt, CancellationToken cancellationToken) {
        if (!_options.RetryPolicy.StatusCodes.Contains(response.StatusCode)) {
            return false;
        }

        if (attempt >= _options.RetryPolicy.MaxRetries) {
            return false;
        }

        var delay = GetRetryAfter(response);
        if (delay == null) {
            delay = TimeSpan.FromMilliseconds(_options.RetryPolicy.InitialDelay.TotalMilliseconds * Math.Pow(_options.RetryPolicy.BackoffFactor, attempt - 1));
        }

        if (delay > TimeSpan.Zero) {
            _logger.LogWarning("Retrying request in {Delay} due to {StatusCode}.", delay, response.StatusCode);
            await Task.Delay(delay.Value, cancellationToken).ConfigureAwait(false);
        }

        return true;
    }

    private static TimeSpan? GetRetryAfter(HttpResponseMessage response) {
        if (response.Headers.RetryAfter?.Delta != null) {
            return response.Headers.RetryAfter.Delta;
        }

        if (response.Headers.RetryAfter?.Date != null) {
            var delta = response.Headers.RetryAfter.Date.Value - DateTimeOffset.UtcNow;
            return delta > TimeSpan.Zero ? delta : TimeSpan.Zero;
        }

        return null;
    }

    private void ApplyCachingHeaders(HttpRequestMessage request) {
        if (request.Method != HttpMethod.Get) {
            return;
        }

        var key = request.RequestUri!.ToString();
        if (!_responseCache.TryGetValue(key, out var cached)) {
            return;
        }

        if (!string.IsNullOrWhiteSpace(cached.ETag)) {
            request.Headers.TryAddWithoutValidation("If-None-Match", cached.ETag);
        }

        if (cached.LastModified.HasValue) {
            request.Headers.IfModifiedSince = cached.LastModified;
        }
    }

    private async Task<HttpResponseMessage> HandleCachingAsync(HttpRequestMessage originalRequest, HttpResponseMessage response) {
        if (originalRequest.Method != HttpMethod.Get) {
            return response;
        }

        var key = originalRequest.RequestUri!.ToString();
        if (response.StatusCode == HttpStatusCode.NotModified && _responseCache.TryGetValue(key, out var cached)) {
            response.Dispose();
            return cached.ToHttpResponseMessage(originalRequest);
        }

        if (response.StatusCode != HttpStatusCode.OK) {
            return response;
        }

        if (!response.Headers.ETag.HasValue && !response.Content.Headers.LastModified.HasValue) {
            return response;
        }

        var snapshot = await ContentSnapshot.CreateAsync(response).ConfigureAwait(false);
        var copy = new CachedResponse(response, snapshot);
        _responseCache[key] = copy;
        snapshot.Restore(response);
        return response;
    }

    private static bool IsHtml(HttpResponseMessage response) {
        if (response.Content?.Headers?.ContentType?.MediaType is { } mediaType) {
            return mediaType.Contains("html", StringComparison.OrdinalIgnoreCase);
        }

        return false;
    }

    private async Task<HttpRequestMessage> CloneRequestAsync(HttpRequestMessage request, string? captchaToken = null) {
        var clone = new HttpRequestMessage(request.Method, request.RequestUri);
        clone.Version = request.Version;
        clone.VersionPolicy = request.VersionPolicy;

        foreach (var header in request.Headers) {
            clone.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        if (request.Content != null) {
            var ms = new MemoryStream();
            await request.Content.CopyToAsync(ms).ConfigureAwait(false);
            ms.Position = 0;
            var content = new StreamContent(ms);
            foreach (var header in request.Content.Headers) {
                content.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }

            clone.Content = content;
        }

        foreach (var option in request.Options) {
            clone.Options.Set(option.Key, option.Value);
        }

        if (!string.IsNullOrWhiteSpace(captchaToken)) {
            clone.Headers.Remove("X-Captcha-Token");
            clone.Headers.TryAddWithoutValidation("X-Captcha-Token", captchaToken);
        }

        return clone;
    }

    private async Task<RobotsCacheEntry?> GetRobotsAsync(Uri uri, CancellationToken cancellationToken) {
        var host = uri.Host;
        if (_robotsCache.TryGetValue(host, out var cached) && (_timeProvider.GetUtcNow() - cached.FetchedAt) <= _options.RobotsCacheTtl) {
            return cached;
        }

        try {
            var robotsUri = new Uri(uri.GetLeftPart(UriPartial.Authority) + "/robots.txt");
            using var message = new HttpRequestMessage(HttpMethod.Get, robotsUri);
            message.Options.Set(SkipPolitenessKey, true);
            var response = await base.SendAsync(message, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) {
                return null;
            }

            var content = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            var rules = RobotsRules.Parse(content);
            cached = new RobotsCacheEntry(_timeProvider.GetUtcNow(), rules);
            _robotsCache[host] = cached;
            return cached;
        } catch (Exception ex) {
            _logger.LogDebug(ex, "Failed to fetch robots.txt for {Host}", host);
            return null;
        }
    }

    private static class CaptchaDetector {
        private static readonly Regex SiteKeyRegex = new("data-sitekey=\"(?<key>[^\"]+)\"", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        public static bool TryDetect(Uri page, string html, out CaptchaContext context) {
            if (!ContainsCaptchaMarker(html)) {
                context = null!;
                return false;
            }

            var type = DetectType(html);
            var siteKey = SiteKeyRegex.Match(html).Groups["key"].Value;
            context = new CaptchaContext {
                PageUrl = page,
                CaptchaType = type,
                SiteKey = string.IsNullOrWhiteSpace(siteKey) ? null : siteKey,
                HtmlPreview = html.Length > 2048 ? html[..2048] : html
            };
            return true;
        }

        private static string DetectType(string html) {
            if (html.Contains("g-recaptcha", StringComparison.OrdinalIgnoreCase)) {
                return "recaptcha";
            }

            if (html.Contains("hcaptcha", StringComparison.OrdinalIgnoreCase)) {
                return "hcaptcha";
            }

            if (html.Contains("cf-chl-captcha", StringComparison.OrdinalIgnoreCase)) {
                return "cloudflare";
            }

            return "unknown";
        }

        private static bool ContainsCaptchaMarker(string html) {
            return html.Contains("g-recaptcha", StringComparison.OrdinalIgnoreCase) ||
                   html.Contains("hcaptcha", StringComparison.OrdinalIgnoreCase) ||
                   html.Contains("data-sitekey", StringComparison.OrdinalIgnoreCase) ||
                   html.Contains("cf-chl-captcha", StringComparison.OrdinalIgnoreCase);
        }
    }

    private sealed class HostPolitenessState {
        private readonly object _lock = new();
        private DateTimeOffset _nextAllowed;

        public HostPolitenessState(int concurrency) {
            Concurrency = new SemaphoreSlim(Math.Max(1, concurrency));
        }

        public SemaphoreSlim Concurrency { get; }

        public DateTimeOffset Reserve(DateTimeOffset now, TimeSpan delta) {
            lock (_lock) {
                if (_nextAllowed < now) {
                    _nextAllowed = now;
                }

                var scheduled = _nextAllowed;
                _nextAllowed = _nextAllowed + delta;
                return scheduled;
            }
        }

        public void Defer(TimeSpan delay) {
            lock (_lock) {
                _nextAllowed = _nextAllowed + delay;
            }
        }
    }

    private sealed class RobotsCacheEntry {
        public RobotsCacheEntry(DateTimeOffset fetchedAt, RobotsRules rules) {
            FetchedAt = fetchedAt;
            Rules = rules;
        }

        public DateTimeOffset FetchedAt { get; }

        public RobotsRules Rules { get; }
    }

    private sealed class RobotsRules {
        private readonly List<string> _disallow = new();

        private RobotsRules() { }

        public TimeSpan? CrawlDelay { get; private set; }

        public IReadOnlyList<string> Sitemaps { get; private set; } = new List<string>();

        public static RobotsRules Parse(string robots) {
            var rules = new RobotsRules();
            var lines = robots.Split('\n');
            var forUs = false;
            var sitemaps = new List<string>();

            foreach (var rawLine in lines) {
                var line = rawLine.Split('#')[0].Trim();
                if (string.IsNullOrWhiteSpace(line)) {
                    continue;
                }

                if (line.StartsWith("User-agent", StringComparison.OrdinalIgnoreCase)) {
                    var agent = line.Split(':', 2)[1].Trim();
                    forUs = agent == "*";
                } else if (forUs && line.StartsWith("Disallow", StringComparison.OrdinalIgnoreCase)) {
                    var value = line.Split(':', 2)[1].Trim();
                    if (!string.IsNullOrWhiteSpace(value)) {
                        rules._disallow.Add(value);
                    }
                } else if (forUs && line.StartsWith("Crawl-delay", StringComparison.OrdinalIgnoreCase)) {
                    var value = line.Split(':', 2)[1].Trim();
                    if (double.TryParse(value, out var seconds)) {
                        rules.CrawlDelay = TimeSpan.FromSeconds(seconds);
                    }
                } else if (line.StartsWith("Sitemap", StringComparison.OrdinalIgnoreCase)) {
                    var value = line.Split(':', 2)[1].Trim();
                    if (Uri.TryCreate(value, UriKind.Absolute, out var sitemap)) {
                        sitemaps.Add(sitemap.ToString());
                    }
                }
            }

            rules.Sitemaps = sitemaps;
            return rules;
        }

        public bool IsDisallowed(string path) {
            if (_disallow.Count == 0) {
                return false;
            }

            foreach (var pattern in _disallow) {
                if (pattern == "/") {
                    return true;
                }

                if (path.StartsWith(pattern, StringComparison.Ordinal)) {
                    return true;
                }
            }

            return false;
        }
    }

    private sealed class CachedResponse {
        private readonly ContentSnapshot _snapshot;
        private readonly HttpStatusCode _statusCode;
        private readonly string? _reasonPhrase;
        private readonly Version _version;
        private readonly Dictionary<string, IEnumerable<string>> _headers;

        public CachedResponse(HttpResponseMessage response, ContentSnapshot snapshot) {
            _snapshot = snapshot;
            _statusCode = response.StatusCode;
            _reasonPhrase = response.ReasonPhrase;
            _version = response.Version;
            _headers = response.Headers.ToDictionary(h => h.Key, h => h.Value.ToArray().AsEnumerable());
            ETag = response.Headers.ETag?.Tag;
            LastModified = response.Content.Headers.LastModified;
        }

        public string? ETag { get; }

        public DateTimeOffset? LastModified { get; }

        public HttpResponseMessage ToHttpResponseMessage(HttpRequestMessage request) {
            var message = new HttpResponseMessage(_statusCode) {
                ReasonPhrase = _reasonPhrase,
                Version = _version,
                RequestMessage = request
            };

            foreach (var header in _headers) {
                message.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }

            _snapshot.Restore(message);
            return message;
        }
    }

    private sealed class ContentSnapshot {
        private readonly byte[] _buffer;
        private readonly Encoding _encoding;
        private readonly Dictionary<string, IEnumerable<string>> _headers;

        private ContentSnapshot(byte[] buffer, Encoding encoding, Dictionary<string, IEnumerable<string>> headers) {
            _buffer = buffer;
            _encoding = encoding;
            _headers = headers;
        }

        public static async Task<ContentSnapshot> CreateAsync(HttpResponseMessage response) {
            var bytes = await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
            var encoding = ResolveEncoding(response.Content.Headers.ContentType?.CharSet);
            var headers = response.Content.Headers.ToDictionary(h => h.Key, h => h.Value.ToArray().AsEnumerable());
            return new ContentSnapshot(bytes, encoding, headers);
        }

        public string ReadAsString() => _encoding.GetString(_buffer);

        public void Restore(HttpResponseMessage response) {
            response.Content?.Dispose();
            var content = new ByteArrayContent(_buffer);
            foreach (var header in _headers) {
                content.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }

            if (content.Headers.ContentType == null) {
                content.Headers.ContentType = new MediaTypeHeaderValue("text/html") {
                    CharSet = _encoding.WebName
                };
            } else {
                content.Headers.ContentType.CharSet = _encoding.WebName;
            }

            response.Content = content;
        }

        private static Encoding ResolveEncoding(string? charset) {
            if (!string.IsNullOrWhiteSpace(charset)) {
                try {
                    return Encoding.GetEncoding(charset);
                } catch (ArgumentException) {
                    // ignore and fallback
                }
            }

            return Encoding.UTF8;
        }
    }
}
