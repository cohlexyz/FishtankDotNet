using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using Raffinert.FuzzySharp;
using KfChatDotNetBot.Settings;
using NLog;

namespace KfChatDotNetBot.Services;

public enum ClipStage { Queued, Encoding, Uploading }

public record ClipProgress(ClipStage Stage, int PercentComplete, int QueuePosition = 0);

/// <summary>
/// Manages up to 3 concurrent camera stream buffers in a deque (oldest-first eviction).
/// Provides fuzzy camera name matching, save-to-Zipline, and automatic dead buffer cleanup.
/// </summary>
public class ClipService
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    private const int MaxBuffers = 3;
    private const int FuzzyMatchThreshold = 60;
    private const int StopFuzzyMatchThreshold = 80;

    private const int MaxMarkerMinutes = 8;

    private readonly LinkedList<CameraBuffer> _activeBuffers = new();
    private readonly Lock _lock = new();
    private readonly CancellationToken _ct;
    private readonly Channel<ClipJob> _clipQueue = Channel.CreateUnbounded<ClipJob>();
    private readonly Task _queueWorker;

    // Tracks what is currently encoding/uploading and what is waiting, for !clip queue
    private readonly Lock _queueStateLock = new();
    private string? _currentJobName;
    private readonly List<string> _pendingJobNames = [];

    // Per-camera markers set via !clip begin
    private readonly Dictionary<string, DateTimeOffset> _markers = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock _markerLock = new();

    private record ClipJob(
        string CameraName,
        string TsPath,
        CameraBuffer Buffer,
        DateTimeOffset Cutoff,
        TimeSpan Duration,
        CancellationToken Ct,
        IProgress<ClipProgress>? Progress,
        TaskCompletionSource<string> Result);

    public ClipService(CancellationToken ct)
    {
        _ct = ct;
        _queueWorker = Task.Run(() => ProcessQueueAsync(ct), ct);
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
                    .Select(b => (Buffer: b, Score: Fuzz.Ratio(cameraQuery.ToLowerInvariant(), b.CameraName.ToLowerInvariant())))
                    .OrderByDescending(x => x.Score)
                    .FirstOrDefault();

                if (best.Score >= StopFuzzyMatchThreshold)
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
    /// Sets a marker for the given camera at the current time minus 10 seconds.
    /// Returns a status message.
    /// </summary>
    public string SetMarker(string cameraQuery, Dictionary<string, string> cameras)
    {
        var (matchedName, _) = FuzzyMatchCamera(cameraQuery, cameras);
        if (matchedName == null)
            return $"No camera matched \"{cameraQuery}\". Use !clip cameras to see available cameras.";

        var markerTime = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(10);
        lock (_markerLock)
        {
            _markers[matchedName] = markerTime;
        }

        Logger.Info($"[ClipService] Marker set for {matchedName} at {markerTime:HH:mm:ss}");
        return $"Marker set for {matchedName}";
    }

    /// <summary>
    /// Gets the marker timestamp for a camera, or null if none is set.
    /// </summary>
    public DateTimeOffset? GetMarker(string cameraName)
    {
        lock (_markerLock)
        {
            return _markers.TryGetValue(cameraName, out var ts) ? ts : null;
        }
    }

    /// <summary>
    /// Clears the marker for a camera.
    /// </summary>
    public void ClearMarker(string cameraName)
    {
        lock (_markerLock)
        {
            _markers.Remove(cameraName);
        }
    }

    /// <summary>
    /// Snapshots the buffer for a camera to disk, then enqueues the encode+upload job.
    /// Returns the Zipline URL once the queued job completes.
    /// </summary>
    public async Task<string> SaveAsync(string cameraQuery, Dictionary<string, string> cameras, CancellationToken ct, IProgress<ClipProgress>? progress = null, TimeSpan? trimTo = null)
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

        // Snapshot buffer to disk immediately so the buffer can keep recording
        Logger.Info($"[ClipService] Snapshotting buffer for {target.CameraName}{(trimTo.HasValue ? $" (last {trimTo.Value.TotalSeconds:N0}s)" : "")}");
        string tsPath;
        DateTimeOffset cutoff;
        TimeSpan duration;
        try
        {
            (tsPath, cutoff, duration) = await target.SnapshotToFileAsync(ct, trimTo);
        }
        catch (Exception ex)
        {
            Logger.Error($"[ClipService] Snapshot failed for {target.CameraName}: {ex}");
            return $"Failed to save clip for {target.CameraName}: {ex.Message}";
        }

        // Enqueue the encode+upload job
        var tcs = new TaskCompletionSource<string>();
        var job = new ClipJob(target.CameraName, tsPath, target, cutoff, duration, ct, progress, tcs);

        int queueDepth;
        lock (_queueStateLock)
        {
            _pendingJobNames.Add(target.CameraName);
            // Depth = pending jobs not yet started (exclude the one we just added if worker is idle)
            queueDepth = _currentJobName != null ? _pendingJobNames.Count : _pendingJobNames.Count - 1;
        }

        if (queueDepth > 0)
            progress?.Report(new ClipProgress(ClipStage.Queued, 0, queueDepth));

        await _clipQueue.Writer.WriteAsync(job, ct);
        Logger.Info($"[ClipService] Enqueued clip for {target.CameraName} (queue depth: {queueDepth + 1})");

        return await tcs.Task;
    }

    /// <summary>
    /// Returns the current clip job name (encoding/uploading) and the list of queued job names.
    /// </summary>
    public (string? CurrentJob, IReadOnlyList<string> PendingJobs) GetQueueStatus()
    {
        lock (_queueStateLock)
        {
            return (_currentJobName, [.. _pendingJobNames]);
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

    private async Task ProcessQueueAsync(CancellationToken ct)
    {
        await foreach (var job in _clipQueue.Reader.ReadAllAsync(ct))
        {
            try
            {
                var result = await ProcessJobAsync(job);
                job.Result.TrySetResult(result);
            }
            catch (Exception ex)
            {
                Logger.Error($"[ClipService] Queue job failed for {job.CameraName}: {ex}");
                job.Result.TrySetResult($"Failed to process clip for {job.CameraName}: {ex.Message}");
            }
        }
    }

    private async Task<string> ProcessJobAsync(ClipJob job)
    {
        lock (_queueStateLock)
        {
            _currentJobName = job.CameraName;
            _pendingJobNames.Remove(job.CameraName);
        }

        string? mp4Path = null;
        try
        {
            var ffmpegPath = (await SettingsProvider.GetValueAsync(BuiltIn.Keys.FFmpegBinaryPath)).Value ?? "ffmpeg";

            // Encode .ts -> 720p H.264 MP4
            mp4Path = await EncodeToMp4Async(job.TsPath, job.CameraName, ffmpegPath, job.Duration, job.Progress, job.Ct);

            // Upload to Zipline
            await using var stream = File.OpenRead(mp4Path);
            var filename = $"{job.CameraName.Replace(' ', '_')}_{DateTimeOffset.UtcNow:yyyyMMddHHmmss}.mp4";
            Logger.Info($"[ClipService] Upload started for {job.CameraName} ({stream.Length} bytes)");

            IProgress<(long sent, long total)>? uploadProgress = null;
            if (job.Progress != null)
            {
                uploadProgress = new Progress<(long sent, long total)>(p =>
                {
                    if (p.total <= 0) return;
                    var pct = (int)(p.sent * 100 / p.total);
                    job.Progress.Report(new ClipProgress(ClipStage.Uploading, pct));
                });
            }

            var url = uploadProgress != null
                ? await Zipline.Upload(stream, new MediaTypeHeaderValue("video/mp4"), uploadProgress, "1h", job.Ct, filename)
                : await Zipline.Upload(stream, new MediaTypeHeaderValue("video/mp4"), "1h", job.Ct, filename);

            if (url == null)
                return $"Zipline upload returned null for {job.CameraName} clip";

            Logger.Info($"[ClipService] Uploaded clip for {job.CameraName}: {url}");
            job.Buffer.ResetBuffer(job.Cutoff);
            return url;
        }
        catch (Exception ex)
        {
            Logger.Error($"[ClipService] Clip processing failed for {job.CameraName}: {ex}");
            return $"Failed to process clip for {job.CameraName}: {ex.Message}";
        }
        finally
        {
            lock (_queueStateLock) { _currentJobName = null; }
            try { File.Delete(job.TsPath); } catch { /* best effort */ }
            if (mp4Path != null)
            {
                try { File.Delete(mp4Path); } catch { /* best effort */ }
            }
        }
    }

    private static async Task<string> EncodeToMp4Async(string tsPath, string cameraName, string ffmpegPath,
        TimeSpan duration, IProgress<ClipProgress>? progress, CancellationToken ct)
    {
        var id = Guid.NewGuid().ToString("N")[..8];
        var mp4Path = Path.Combine(Path.GetTempPath(), $"clip_{cameraName.Replace(' ', '_')}_{id}.mp4");

        var ffmpegArgs = $"-i \"{tsPath}\" -vf \"scale=-2:720,setpts=PTS-STARTPTS\" -af asetpts=PTS-STARTPTS " +
                         $"-c:v libx264 -preset veryfast -crf 26 -c:a aac -b:a 128k -threads 6 -progress pipe:1 -y \"{mp4Path}\"";

        var processInfo = new ProcessStartInfo
        {
            FileName = ffmpegPath,
            Arguments = ffmpegArgs,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var encode = System.Diagnostics.Process.Start(processInfo);
        if (encode == null)
            throw new InvalidOperationException("Failed to start FFmpeg encode");

        Logger.Info($"[ClipService] Encoding {cameraName}: {ffmpegArgs}");

        var totalMicroseconds = duration.TotalSeconds * 1_000_000;
        var progressTask = Task.Run(async () =>
        {
            var reader = encode.StandardOutput;
            while (await reader.ReadLineAsync(ct) is { } line)
            {
                if (!line.StartsWith("out_time_us=", StringComparison.Ordinal)) continue;
                if (!long.TryParse(line.AsSpan(12), out var us) || totalMicroseconds <= 0) continue;
                var pct = Math.Clamp(us / totalMicroseconds, 0, 1);
                progress?.Report(new ClipProgress(ClipStage.Encoding, (int)(pct * 100)));
            }
        }, ct);

        var stderrTask = encode.StandardError.ReadToEndAsync(ct);

        using var encodeCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        encodeCts.CancelAfter(TimeSpan.FromMinutes(5));
        await encode.WaitForExitAsync(encodeCts.Token);
        await progressTask;
        var stderr = await stderrTask;

        if (encode.ExitCode != 0)
        {
            try { File.Delete(mp4Path); } catch { /* best effort */ }
            Logger.Error($"[ClipService] FFmpeg encode failed for {cameraName} (exit {encode.ExitCode}): {stderr}");
            throw new InvalidOperationException($"FFmpeg encode failed with exit code {encode.ExitCode}");
        }

        progress?.Report(new ClipProgress(ClipStage.Encoding, 100));
        Logger.Info($"[ClipService] Encoded {cameraName} TS -> 720p MP4 at {mp4Path}");
        return mp4Path;
    }
}
