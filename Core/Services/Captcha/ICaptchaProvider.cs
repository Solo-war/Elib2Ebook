using System.Threading;
using System.Threading.Tasks;

namespace Core.Services.Captcha;

public interface ICaptchaProvider {
    Task<string> SolveAsync(CancellationToken cancellationToken, CaptchaRequest request);
}
