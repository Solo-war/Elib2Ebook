using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Core.Services.Captcha;

namespace Elib2EbookCli;

internal sealed class ConsoleCaptchaPrompt : ICaptchaPrompt {
    public Task<string?> RequestSolutionAsync(CancellationToken cancellationToken, CaptchaContext context) {
        if (context == null) {
            throw new ArgumentNullException(nameof(context));
        }

        Console.WriteLine();
        Console.WriteLine("=== CAPTCHA DETECTED ===");
        Console.WriteLine($"URL: {context.PageUrl}");
        if (!string.IsNullOrWhiteSpace(context.CaptchaType)) {
            Console.WriteLine($"Type: {context.CaptchaType}");
        }

        if (!string.IsNullOrWhiteSpace(context.SiteKey)) {
            Console.WriteLine($"Site Key: {context.SiteKey}");
        }

        if (!string.IsNullOrWhiteSpace(context.HtmlPreview)) {
            Console.WriteLine("--- HTML Preview (sanitized) ---");
            Console.WriteLine(Sanitize(context.HtmlPreview));
            Console.WriteLine("-------------------------------");
        }

        Console.WriteLine("Please solve the captcha in your browser if required and paste the token below.");
        Console.Write("Captcha solution (leave empty to cancel): ");

        return Task.Run(() => ReadLineWithCancellation(cancellationToken), cancellationToken);
    }

    private static string? ReadLineWithCancellation(CancellationToken cancellationToken) {
        var builder = new StringBuilder();
        while (true) {
            if (Console.KeyAvailable) {
                var key = Console.ReadKey(intercept: true);
                if (key.Key == ConsoleKey.Enter) {
                    Console.WriteLine();
                    return builder.ToString();
                }

                if (key.Key == ConsoleKey.Backspace) {
                    if (builder.Length > 0) {
                        builder.Length--;
                        Console.Write("\b \b");
                    }

                    continue;
                }

                if (!char.IsControl(key.KeyChar)) {
                    builder.Append(key.KeyChar);
                    Console.Write('*');
                }
            }

            if (cancellationToken.IsCancellationRequested) {
                return null;
            }

            Thread.Sleep(50);
        }
    }

    private static string Sanitize(string html) {
        var cleaned = html.Replace('\n', ' ').Replace('\r', ' ');
        return cleaned.Length > 500 ? cleaned[..500] + "…" : cleaned;
    }
}
