using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Core.Services.Captcha;

public sealed class CaptchaService {
    private readonly ICaptchaProvider _provider;
    private readonly CaptchaOptions _options;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _concurrencySemaphore;
    private readonly Queue<DateTimeOffset> _requestTimestamps = new();
    private readonly object _rateLock = new();
    private readonly TimeProvider _timeProvider;

    public CaptchaService(ICaptchaProvider provider, CaptchaOptions options, ILogger logger, TimeProvider? timeProvider = null) {
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _concurrencySemaphore = new SemaphoreSlim(Math.Max(1, options.MaxParallelTasks));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public static CaptchaService CreateFromEnvironment(ILogger logger) {
        if (logger == null) {
            throw new ArgumentNullException(nameof(logger));
        }

        var options = CaptchaOptions.FromEnvironment();
        var provider = CaptchaProviderFactory.Create(options, logger);
        return new CaptchaService(provider, options, logger);
    }

    public async Task<string> SolveCaptchaAsync(CancellationToken cancellationToken, CaptchaRequest request) {
        if (request == null) {
            throw new ArgumentNullException(nameof(request));
        }

        if (IsProductionEnvironment() && !_options.AllowCaptchaInProd) {
            throw new InvalidOperationException(
                "Captcha solving is disabled in production. Set ALLOW_CAPTCHA_IN_PROD=true only when you have explicit permission.");
        }

        await _concurrencySemaphore.WaitAsync(cancellationToken).ConfigureAwait(false);

        try {
            await WaitForRateLimitAsync(cancellationToken).ConfigureAwait(false);
            return await ExecuteWithRetryAsync(cancellationToken, request).ConfigureAwait(false);
        } finally {
            _concurrencySemaphore.Release();
        }
    }

    private async Task<string> ExecuteWithRetryAsync(CancellationToken cancellationToken, CaptchaRequest request) {
        var attempt = 0;
        var delay = _options.InitialRetryDelay;

        while (true) {
            cancellationToken.ThrowIfCancellationRequested();
            attempt++;

            try {
                _logger.LogInformation("Sending captcha solving request via {Provider}. Attempt {Attempt}. Type: {RequestType}",
                    _options.Provider, attempt, request.Type);

                var result = await _provider.SolveAsync(cancellationToken, request).ConfigureAwait(false);

                _logger.LogInformation("Captcha solved successfully using {Provider}.", _options.Provider);
                return result;
            } catch (CaptchaProviderException ex) when (ex.IsTransient && attempt < _options.MaxRetryAttempts) {
                _logger.LogWarning(ex, "Transient error while solving captcha via {Provider}. Retrying in {Delay}.",
                    _options.Provider, delay);
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                delay = TimeSpan.FromMilliseconds(delay.TotalMilliseconds * Math.Max(1, _options.RetryBackoffFactor));
            } catch (CaptchaProviderException ex) when (!ex.IsTransient) {
                _logger.LogError(ex, "Captcha provider returned a non-retryable error.");
                throw;
            } catch (Exception ex) when (attempt < _options.MaxRetryAttempts) {
                _logger.LogWarning(ex, "Unexpected error while solving captcha via {Provider}. Retrying in {Delay}.",
                    _options.Provider, delay);
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                delay = TimeSpan.FromMilliseconds(delay.TotalMilliseconds * Math.Max(1, _options.RetryBackoffFactor));
            }

            if (attempt >= _options.MaxRetryAttempts) {
                throw new CaptchaProviderException("Failed to solve captcha after maximum retry attempts.", false);
            }
        }
    }

    private async Task WaitForRateLimitAsync(CancellationToken cancellationToken) {
        if (_options.RequestsPerWindow <= 0) {
            return;
        }

        while (true) {
            cancellationToken.ThrowIfCancellationRequested();
            DateTimeOffset? nextWaitUntil = null;

            lock (_rateLock) {
                var now = _timeProvider.GetUtcNow();
                while (_requestTimestamps.Count > 0 && now - _requestTimestamps.Peek() > _options.RateLimitWindow) {
                    _requestTimestamps.Dequeue();
                }

                if (_requestTimestamps.Count < _options.RequestsPerWindow) {
                    _requestTimestamps.Enqueue(now);
                    return;
                }

                var oldest = _requestTimestamps.Peek();
                nextWaitUntil = oldest + _options.RateLimitWindow;
            }

            if (nextWaitUntil.HasValue) {
                var delay = nextWaitUntil.Value - _timeProvider.GetUtcNow();
                if (delay > TimeSpan.Zero) {
                    await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                } else {
                    await Task.Yield();
                }
            }
        }
    }

    private static bool IsProductionEnvironment() {
        static bool IsProduction(string? value) => value != null && value.Equals("Production", StringComparison.OrdinalIgnoreCase);

        return IsProduction(Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT")) ||
               IsProduction(Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT")) ||
               IsProduction(Environment.GetEnvironmentVariable("ENVIRONMENT"));
    }
}
