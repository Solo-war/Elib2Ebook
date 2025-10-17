using System.Threading;
using System.Threading.Tasks;

namespace Core.Services.Captcha;

public interface ICaptchaSolver {
    Task<string> SolveAsync(CancellationToken cancellationToken, CaptchaContext context);
}
