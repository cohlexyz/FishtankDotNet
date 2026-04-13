
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using KfChatDotNetBot.Services;
using KfChatDotNetBot.Settings;
using NLog;
using SixLabors.ImageSharp;

public static class ImageCompressor
{
    private static readonly Logger _logger = LogManager.GetCurrentClassLogger();

    public static async Task<(string? result, string? error)> CompressImageAsync(string imageUrl, string user, CancellationToken ct, int quality = 50, string key = "")
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

        // check the image width for key == quote (they can't be wider than 400px)
        if (key == "quote")
        {
            using var img = Image.Load(data);
            if (img.Width > 470 && user != "Gaunt King Ithan Rilph") // this retard gets a pass
            {
                _logger.Debug($"Quote image is {img.Width}px wide, rejecting (max 470px for quotes)");
                return (null, $"Image is too wide ({img.Width}px) for a quote - maximum is 470px, otherwise it won't be readable. Try resizing the browser window to make it narrower before taking the screenshot.");
            }
        }

        if (data.Length <= 2_700_000)
        {
            // just upload as-is without compression
            using var ms = new MemoryStream(data);
            var url = await Zipline.Upload(ms, new MediaTypeHeaderValue("image/webp"), ct: ct);
            return (url, null);
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
            for (int attempt = 1; outputBytes.Length > 2_700_000 && attempt <= 5; attempt++)
            {
                retryQuality = Math.Max(10, retryQuality - 10);
                retryResize = attempt switch
                {
                    1 => "220x>",
                    2 => "200x>",
                    3 => "180x>",
                    4 => "160x>",
                    _ => "150x>"
                };
                var retryFrameStep = attempt switch
                {
                    1 => 1,
                    2 => 2,
                    3 => 2,
                    4 => 3,
                    _ => 4
                };
                var retryInput = retryFrameStep > 1 ? $"{tempInput}[0-9999:{retryFrameStep}]" : tempInput;
                _logger.Info($"Still over 2 MB, retry {attempt}/5 (quality={retryQuality}, resize={retryResize}, frameStep={retryFrameStep})");

                var retryPsi = new ProcessStartInfo("convert")
                {
                    ArgumentList = { retryInput, "-coalesce", "-resize", retryResize, "-quality", retryQuality.ToString(), tempOutput },
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