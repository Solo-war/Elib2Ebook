using System;
using System.Collections.Generic;
using System.Net;

namespace Core.Net.Politeness;

public sealed record RetryPolicyOptions {
    public static RetryPolicyOptions Default { get; } = new() {
        MaxRetries = 3,
        InitialDelay = TimeSpan.FromSeconds(2),
        BackoffFactor = 2.0,
        StatusCodes = new HashSet<HttpStatusCode> {
            HttpStatusCode.TooManyRequests,
            HttpStatusCode.ServiceUnavailable,
            HttpStatusCode.GatewayTimeout
        }
    };

    public int MaxRetries { get; init; }

    public TimeSpan InitialDelay { get; init; }

    public double BackoffFactor { get; init; }

    public ISet<HttpStatusCode> StatusCodes { get; init; } = new HashSet<HttpStatusCode>();
}
