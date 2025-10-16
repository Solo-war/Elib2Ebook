using System;

namespace Core.Services.Captcha;

public class CaptchaProviderException : Exception {
    public CaptchaProviderException(string message, bool isTransient)
        : base(message) {
        IsTransient = isTransient;
    }

    public CaptchaProviderException(string message, Exception innerException, bool isTransient)
        : base(message, innerException) {
        IsTransient = isTransient;
    }

    public bool IsTransient { get; }
}
