using System;

namespace Core.Services.Captcha;

public sealed class ErrCaptchaRequiredException : Exception {
    public ErrCaptchaRequiredException(CaptchaContext context)
        : base("Captcha required.") {
        Context = context ?? throw new ArgumentNullException(nameof(context));
    }

    public CaptchaContext Context { get; }
}
