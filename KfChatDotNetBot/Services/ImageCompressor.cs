
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using KfChatDotNetBot.Services;
using KfChatDotNetBot.Settings;
using NLog;

public static class ImageCompressor
{
    private static readonly Logger _logger = LogManager.GetCurrentClassLogger();

    public static async Task<(string? result, string? error)> CompressImageAsync(string imageUrl, CancellationToken ct, int quality = 50)
    {
        var settings = await SettingsProvider.GetMultipleValuesAsync([BuiltIn.Keys.Proxy, BuiltIn.Keys.KiwiFarmsDomain, BuiltIn.Keys.KiwiFarmsCookies]);
        var handler = new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            AllowAutoRedirect = true
        };
        if (settings[BuiltIn.Keys.Proxy].Value != null)
        {
            handler.UseProxy = true;
            handler.Proxy = new WebProxy(settings[BuiltIn.Keys.Proxy].Value);
            _logger.Debug($"Configured to use proxy {settings[BuiltIn.Keys.Proxy].Value}");
        }

        var uri = new Uri(imageUrl);
        var kfDomain = settings[BuiltIn.Keys.KiwiFarmsDomain].Value;
        if (kfDomain != null && uri.Host.Equals(kfDomain, StringComparison.OrdinalIgnoreCase))
        {
            _logger.Debug("Image URL is on KiwiFarms domain, attaching cookies");
            var cookieContainer = new CookieContainer();
            var cookies = settings[BuiltIn.Keys.KiwiFarmsCookies].JsonDeserialize<Dictionary<string, string>>();
            if (cookies != null)
            {
                foreach (var (name, value) in cookies)
                    cookieContainer.Add(new Cookie(name, value, "/", kfDomain));
            }
            handler.CookieContainer = cookieContainer;
            handler.UseCookies = true;
        }

        using var client = new HttpClient(handler);
        byte[] data = await client.GetByteArrayAsync(imageUrl, ct);

        _logger.Debug($"Image size: {data.Length / 1024.0:F2} KB");

        if (data.Length <= 2_000_000)
        {
            return (imageUrl, null);
        }

        var tempInput = Path.GetTempFileName();
        var tempOutput = Path.ChangeExtension(Path.GetTempFileName(), ".webp");
        try
        {
            await File.WriteAllBytesAsync(tempInput, data, ct);

            var psi = new ProcessStartInfo("convert")
            {
                ArgumentList = { tempInput, "-coalesce", "-resize", "250x>", "-quality", quality.ToString(), tempOutput },
                RedirectStandardError = true,
                UseShellExecute = false,
            };

            using var process = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start ImageMagick convert process");
            var stderr = await process.StandardError.ReadToEndAsync(ct);
            await process.WaitForExitAsync(ct);

            if (process.ExitCode != 0)
            {
                _logger.Error($"ImageMagick convert failed (exit {process.ExitCode}): {stderr}");
                return (null, $"ImageMagick failed: {stderr}");
            }

            var outputBytes = await File.ReadAllBytesAsync(tempOutput, ct);
            _logger.Debug($"Compressed image size: {outputBytes.Length / 1024.0:F2} KB");

            // Retry up to 3 more times with progressively reduced quality and size
            int retryQuality = quality;
            string retryResize = "250x>";
            for (int attempt = 1; outputBytes.Length > 2_000_000 && attempt <= 3; attempt++)
            {
                retryQuality = Math.Max(10, retryQuality - 10);
                retryResize = attempt switch
                {
                    1 => "220x>",
                    2 => "200x>",
                    _ => "190x>"
                };
                _logger.Info($"Still over 2 MB, retry {attempt}/3 (quality={retryQuality}, resize={retryResize})");

                var retryPsi = new ProcessStartInfo("convert")
                {
                    ArgumentList = { tempInput, "-coalesce", "-resize", retryResize, "-quality", retryQuality.ToString(), tempOutput },
                    RedirectStandardError = true,
                    UseShellExecute = false,
                };
                using var retryProcess = Process.Start(retryPsi) ?? throw new InvalidOperationException("Failed to start ImageMagick convert process");
                var retryStderr = await retryProcess.StandardError.ReadToEndAsync(ct);
                await retryProcess.WaitForExitAsync(ct);

                if (retryProcess.ExitCode != 0)
                {
                    _logger.Error($"ImageMagick retry {attempt} failed (exit {retryProcess.ExitCode}): {retryStderr}");
                    return (null, $"ImageMagick failed on retry {attempt}: {retryStderr}");
                }

                outputBytes = await File.ReadAllBytesAsync(tempOutput, ct);
                _logger.Debug($"Retry {attempt} compressed size: {outputBytes.Length / 1024.0:F2} KB");
            }

            if (outputBytes.Length > 2_000_000)
            {
                var error = $"Compressed image is still larger than 2 MB after 3 retries ({outputBytes.Length / 1024.0 / 1024.0:F2} MB). You'll have to compress it yourself.";
                _logger.Warn(error);
                return (null, error);
            }

            using var ms = new MemoryStream(outputBytes);
            var url = await Zipline.Upload(ms, new MediaTypeHeaderValue("image/webp"), ct: ct);
            return (url, null);
        }
        finally
        {
            if (File.Exists(tempInput)) File.Delete(tempInput);
            if (File.Exists(tempOutput)) File.Delete(tempOutput);
        }
    }
}