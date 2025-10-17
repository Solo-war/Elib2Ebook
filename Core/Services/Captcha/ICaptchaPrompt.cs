using System.Threading;
using System.Threading.Tasks;

namespace Core.Services.Captcha;

public interface ICaptchaPrompt {
    Task<string?> RequestSolutionAsync(CancellationToken cancellationToken, CaptchaContext context);
}
