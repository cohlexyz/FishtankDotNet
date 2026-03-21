
using System.Net;
using System.Net.Http.Headers;
using KfChatDotNetBot.Services;
using KfChatDotNetBot.Settings;
using NLog;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Webp;
using SixLabors.ImageSharp.Processing;

public static class ImageCompressor
{
    private static readonly Logger _logger = LogManager.GetCurrentClassLogger();
    public static async Task<(string? result, string? error)> CompressImageAsync(string imageUrl, CancellationToken ct, int quality = 45)
    {
        var proxy = await SettingsProvider.GetValueAsync(BuiltIn.Keys.Proxy);
        var handler = new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            AllowAutoRedirect = true
        };
        if (proxy.Value != null)
        {
            handler.UseProxy = true;
            handler.Proxy = new WebProxy(proxy.Value);
            _logger.Debug($"Configured to use proxy {proxy.Value}");
        }

        using var client = new HttpClient(handler);
        byte[] data = await client.GetByteArrayAsync(imageUrl);

        _logger.Debug($"Image size: {data.Length / 1024.0:F2} KB");

        // If <= 1 MB, just save as-is
        if (data.Length <= 1_000_000)
        {
            return (imageUrl, null);
        }

        // Load image with ImageSharp
        using var image = Image.Load(data);

        // Resize if wider than 250px
        if (image.Width > 250)
        {
            image.Mutate(x => x.Resize(new ResizeOptions
            {
                Size = new Size(250, 0), // auto height
                Mode = ResizeMode.Max
            }));
        }

        // Save as WebP with quality 45
        var encoder = new WebpEncoder
        {
            Quality = quality
        };

        using var ms = new MemoryStream();
        await image.SaveAsync(ms, encoder);
        _logger.Debug($"Compressed image size: {ms.Length / 1024.0:F2} KB");

        if (ms.Length > 1_000_000)
        {
            var error = "Compressed image is still larger than 1 MB. You'll have to compress it yourself.";
            _logger.Warn(error);
            return (null, error);
        }

        var url = await Zipline.Upload(ms, new MediaTypeHeaderValue("image/webp"), ct: ct);
        return (url, null);
    }
}