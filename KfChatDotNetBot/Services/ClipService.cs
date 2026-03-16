using System.Net.Http.Headers;
using System.Text.RegularExpressions;
using Raffinert.FuzzySharp;
using KfChatDotNetBot.Settings;
using NLog;

namespace KfChatDotNetBot.Services;

/// <summary>
/// Manages up to 3 concurrent camera stream buffers in a deque (oldest-first eviction).
/// Provides fuzzy camera name matching, save-to-Zipline, and automatic dead buffer cleanup.
/// </summary>
public class ClipService
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    private const int MaxBuffers = 3;
    private const int FuzzyMatchThreshold = 60;

    private readonly LinkedList<CameraBuffer> _activeBuffers = new();
    private readonly Lock _lock = new();
    private readonly CancellationToken _ct;

    public ClipService(CancellationToken ct)
    {
        _ct = ct;
    }

    /// <summary>
    /// Start buffering a camera stream. If at capacity, the oldest buffer is evicted.
    /// Returns a status message describing what happened.
    /// </summary>
    public async Task<string> StartAsync(string cameraQuery, Dictionary<string, string> cameras)
    {
        var (matchedName, _) = FuzzyMatchCamera(cameraQuery, cameras);
        if (matchedName == null)
            return $"No camera matched \"{cameraQuery}\". Use !clip cameras to see available cameras.";

        var url = cameras[matchedName];

        lock (_lock)
        {
            // Check if already buffering this camera
            foreach (var buf in _activeBuffers)
            {
                if (buf.CameraName.Equals(matchedName, StringComparison.OrdinalIgnoreCase))
                    return $"{matchedName} is already being buffered";
            }
        }

        var ffmpegPath = (await SettingsProvider.GetValueAsync(BuiltIn.Keys.FFmpegBinaryPath)).Value ?? "ffmpeg";
        var (resolvedVideoUrl, resolvedAudioUrl) = await ResolveStreamsAsync(url, _ct);
        Logger.Info($"[ClipService] Resolved stream URL for {matchedName}: {resolvedVideoUrl}{(resolvedAudioUrl != null ? $" (audio: {resolvedAudioUrl})" : "")}");
        var buffer = new CameraBuffer(matchedName, resolvedVideoUrl, ffmpegPath, _ct, resolvedAudioUrl);
        buffer.OnDied = OnBufferDied;

        string? evictedName = null;
        lock (_lock)
        {
            if (_activeBuffers.Count >= MaxBuffers)
            {
                var oldest = _activeBuffers.First!.Value;
                evictedName = oldest.CameraName;
                _activeBuffers.RemoveFirst();
                // Stop async in background — don't block the caller
                _ = Task.Run(async () =>
                {
                    try { await oldest.StopAsync(); }
                    catch (Exception ex) { Logger.Error($"[ClipService] Error stopping evicted buffer {oldest.CameraName}: {ex.Message}"); }
                });
            }

            _activeBuffers.AddLast(buffer);
        }

        try
        {
            buffer.Start();
        }
        catch (Exception ex)
        {
            lock (_lock) { _activeBuffers.Remove(buffer); }
            return $"Failed to start buffering {matchedName}: {ex.Message}";
        }

        var msg = $"Now buffering {matchedName}";
        if (evictedName != null)
            msg += $" (evicted {evictedName})";
        return msg;
    }

    /// <summary>
    /// Stop buffering a specific camera, or all cameras if cameraQuery is null.
    /// Returns a status message.
    /// </summary>
    public async Task<string> StopAsync(string? cameraQuery, Dictionary<string, string>? cameras = null)
    {
        if (cameraQuery == null)
        {
            List<CameraBuffer> toStop;
            lock (_lock)
            {
                toStop = [.. _activeBuffers];
                _activeBuffers.Clear();
            }

            if (toStop.Count == 0)
                return "No cameras are currently being buffered";

            foreach (var buf in toStop)
            {
                try { await buf.StopAsync(); }
                catch (Exception ex) { Logger.Error($"[ClipService] Error stopping {buf.CameraName}: {ex.Message}"); }
            }

            return $"Stopped all {toStop.Count} camera buffer(s)";
        }

        // Stop specific camera
        CameraBuffer? target = null;
        lock (_lock)
        {
            // Try exact match on active buffer names first
            foreach (var buf in _activeBuffers)
            {
                if (buf.CameraName.Equals(cameraQuery, StringComparison.OrdinalIgnoreCase))
                {
                    target = buf;
                    break;
                }
            }

            // Fall back to fuzzy match against active buffer names
            if (target == null)
            {
                var best = _activeBuffers
                    .Select(b => (Buffer: b, Score: Fuzz.PartialRatio(cameraQuery.ToLowerInvariant(), b.CameraName.ToLowerInvariant())))
                    .OrderByDescending(x => x.Score)
                    .FirstOrDefault();

                if (best.Score >= FuzzyMatchThreshold)
                    target = best.Buffer;
            }

            if (target != null)
                _activeBuffers.Remove(target);
        }

        if (target == null)
            return $"No active buffer matched \"{cameraQuery}\"";

        await target.StopAsync();
        return $"Stopped buffering {target.CameraName}";
    }

    /// <summary>
    /// Saves the buffer for a camera and uploads it to Zipline.
    /// Returns the Zipline URL.
    /// </summary>
    public async Task<string> SaveAsync(string cameraQuery, Dictionary<string, string> cameras, CancellationToken ct)
    {
        CameraBuffer? target;
        lock (_lock)
        {
            // Exact match first
            target = _activeBuffers.FirstOrDefault(b =>
                b.CameraName.Equals(cameraQuery, StringComparison.OrdinalIgnoreCase));

            // Fuzzy match against active buffers
            if (target == null)
            {
                var best = _activeBuffers
                    .Select(b => (Buffer: b, Score: Fuzz.PartialRatio(cameraQuery.ToLowerInvariant(), b.CameraName.ToLowerInvariant())))
                    .OrderByDescending(x => x.Score)
                    .FirstOrDefault();

                if (best.Score >= FuzzyMatchThreshold)
                    target = best.Buffer;
            }
        }

        if (target == null)
            return $"No active buffer matched \"{cameraQuery}\". Use !clip list to see active buffers.";

        if (!target.IsRunning)
            return $"Buffer for {target.CameraName} is not running (FFmpeg may have died)";

        if (target.BufferDuration < TimeSpan.FromSeconds(5))
            return $"Buffer for {target.CameraName} is too short ({target.BufferDuration.TotalSeconds:N0}s). Wait a bit longer.";

        string webmPath;
        try
        {
            webmPath = await target.SaveToFileAsync(ct);
        }
        catch (Exception ex)
        {
            Logger.Error($"[ClipService] SaveToFileAsync failed for {target.CameraName}: {ex}");
            return $"Failed to save clip for {target.CameraName}: {ex.Message}";
        }

        try
        {
            await using var stream = File.OpenRead(webmPath);
            var filename = $"{target.CameraName.Replace(' ', '_')}_{DateTimeOffset.UtcNow:yyyyMMddHHmmss}.mp4";
            var url = await Zipline.Upload(stream, new MediaTypeHeaderValue("video/mp4"), "1d", ct, filename);
            if (url == null)
                return $"Zipline upload returned null for {target.CameraName} clip";

            Logger.Info($"[ClipService] Uploaded clip for {target.CameraName}: {url}");
            return url;
        }
        catch (Exception ex)
        {
            Logger.Error($"[ClipService] Zipline upload failed for {target.CameraName}: {ex}");
            return $"Failed to upload clip for {target.CameraName}: {ex.Message}";
        }
        finally
        {
            try { File.Delete(webmPath); }
            catch { /* best effort cleanup */ }
        }
    }

    /// <summary>
    /// Returns info about all active buffers.
    /// </summary>
    public List<(string Name, TimeSpan Duration, long Bytes)> GetActiveBuffers()
    {
        lock (_lock)
        {
            return _activeBuffers
                .Select(b => (b.CameraName, b.BufferDuration, b.BufferBytes))
                .ToList();
        }
    }

    /// <summary>
    /// Fuzzy-matches a query against camera dictionary keys.
    /// Returns (matchedKey, url) or (null, null) if no match.
    /// </summary>
    public static (string? Name, string? Url) FuzzyMatchCamera(string query, Dictionary<string, string> cameras)
    {
        var best = cameras.Keys
            .Select(name => (Name: name, Score: Fuzz.PartialRatio(query.ToLowerInvariant(), name.ToLowerInvariant())))
            .OrderByDescending(x => x.Score)
            .FirstOrDefault();

        if (best.Score >= FuzzyMatchThreshold)
            return (best.Name, cameras[best.Name]);

        return (null, null);
    }

    private static async Task<(string VideoUrl, string? AudioUrl)> ResolveStreamsAsync(string url, CancellationToken ct)
    {
        using var client = new HttpClient();
        string content;
        try
        {
            content = await client.GetStringAsync(url, ct);
        }
        catch (Exception ex)
        {
            Logger.Warn($"[ClipService] Could not fetch m3u8 to resolve best stream ({ex.Message}), using original URL");
            return (url, null);
        }

        if (!content.Contains("#EXT-X-STREAM-INF"))
            return (url, null);

        var baseUri = new Uri(url);
        var lines = content.Split('\n', StringSplitOptions.RemoveEmptyEntries);

        // Extract audio URI from #EXT-X-MEDIA:TYPE=AUDIO (prefer DEFAULT=YES)
        string? audioUri = null;
        foreach (var rawLine in lines)
        {
            var line = rawLine.Trim();
            if (!line.StartsWith("#EXT-X-MEDIA:", StringComparison.Ordinal)) continue;
            if (!line.Contains("TYPE=AUDIO", StringComparison.Ordinal)) continue;
            var uriMatch = Regex.Match(line, @"URI=""([^""]+)""");
            if (!uriMatch.Success) continue;
            var candidate = uriMatch.Groups[1].Value;
            if (!candidate.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                candidate = new Uri(baseUri, candidate).ToString();
            audioUri = candidate;
            // Prefer DEFAULT=YES but accept first found as fallback
            if (line.Contains("DEFAULT=YES", StringComparison.OrdinalIgnoreCase))
                break;
        }

        // Pick the variant with the highest BANDWIDTH
        string? bestVariantUrl = null;
        long bestBandwidth = -1;

        for (var i = 0; i < lines.Length - 1; i++)
        {
            var line = lines[i].Trim();
            if (!line.StartsWith("#EXT-X-STREAM-INF:", StringComparison.Ordinal)) continue;

            var bwMatch = Regex.Match(line, @"BANDWIDTH=(\d+)");
            if (!bwMatch.Success) continue;

            var bandwidth = long.Parse(bwMatch.Groups[1].Value);
            if (bandwidth <= bestBandwidth) continue;

            var nextLine = lines[i + 1].Trim();
            if (nextLine.StartsWith("#")) continue;

            bestBandwidth = bandwidth;
            bestVariantUrl = nextLine;
        }

        if (bestVariantUrl == null) return (url, audioUri);

        if (!bestVariantUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            bestVariantUrl = new Uri(baseUri, bestVariantUrl).ToString();

        return (bestVariantUrl, audioUri);
    }

    private void OnBufferDied(CameraBuffer buffer)
    {
        Logger.Error($"[ClipService] Camera buffer for {buffer.CameraName} died, removing from active list");
        lock (_lock)
        {
            _activeBuffers.Remove(buffer);
        }
    }
}
