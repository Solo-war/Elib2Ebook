using System.Diagnostics.Metrics;

namespace Core.Net.Politeness;

internal static class PolitenessMetrics {
    private static readonly Meter Meter = new("Elib2Ebook.Net.Politeness", "1.0.0");

    public static readonly Counter<long> CaptchaDetected = Meter.CreateCounter<long>("captcha_detected_total");

    public static readonly Counter<long> ManualCaptchaSolved = Meter.CreateCounter<long>("manual_captcha_solved_total");

    public static readonly Counter<long> BlockedByRobots = Meter.CreateCounter<long>("blocked_by_robots_total");
}
