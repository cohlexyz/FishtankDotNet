using System.Diagnostics;
using NLog;

namespace KfChatDotNetBot.Services;

/// <summary>
/// Manages an FFmpeg process that pipes a live stream into an in-memory circular buffer.
/// The buffer retains the most recent 2 minutes of MPEG-TS data.
/// </summary>
public class CameraBuffer : IAsyncDisposable
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    public string CameraName { get; }
    public string StreamUrl { get; }
    public bool IsRunning => _readTask is { IsCompleted: false };

    public TimeSpan BufferDuration
    {
        get
        {
            lock (_bufferLock)
            {
                if (_buffer.Count == 0) return TimeSpan.Zero;
                return _buffer[^1].Timestamp - _buffer[0].Timestamp;
            }
        }
    }

    public long BufferBytes
    {
        get
        {
            lock (_bufferLock)
            {
                return _totalBytes;
            }
        }
    }

    /// <summary>
    /// Called when the FFmpeg process dies unexpectedly (not via StopAsync).
    /// The parameter is this CameraBuffer instance.
    /// </summary>
    public Action<CameraBuffer>? OnDied { get; set; }

    private readonly string _ffmpegPath;
    private readonly CancellationTokenSource _cts;
    private Process? _process;
    private Task? _readTask;
    private Task? _trimTask;

    private readonly List<(DateTimeOffset Timestamp, byte[] Data)> _buffer = [];
    private readonly object _bufferLock = new();
    private long _totalBytes;
    private bool _stopping;

    private static readonly TimeSpan MaxBufferAge = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan TrimInterval = TimeSpan.FromSeconds(5);
    private const int ReadChunkSize = 8192;

    public CameraBuffer(string cameraName, string streamUrl, string ffmpegPath, CancellationToken ct)
    {
        CameraName = cameraName;
        StreamUrl = streamUrl;
        _ffmpegPath = ffmpegPath;
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
    }

    /// <summary>
    /// Spawns FFmpeg to read the live stream and begins buffering its output.
    /// </summary>
    public void Start()
    {
        var processInfo = new ProcessStartInfo
        {
            FileName = _ffmpegPath,
            // -re is not used here: we want to read as fast as the live stream provides
            Arguments = $"-i \"{StreamUrl}\" -c copy -f mpegts pipe:1",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        _process = Process.Start(processInfo);
        if (_process == null)
        {
            Logger.Error($"[CameraBuffer:{CameraName}] Failed to start FFmpeg process");
            throw new InvalidOperationException($"Failed to start FFmpeg for {CameraName}");
        }

        Logger.Info($"[CameraBuffer:{CameraName}] FFmpeg started (PID {_process.Id}) for {StreamUrl}");

        _readTask = Task.Run(ReadLoopAsync, _cts.Token);
        _trimTask = Task.Run(TrimLoopAsync, _cts.Token);

        // Fire-and-forget: monitor the FFmpeg process for unexpected exit
        _ = Task.Run(MonitorProcessAsync, CancellationToken.None);
    }

    /// <summary>
    /// Stops the FFmpeg process and clears the buffer.
    /// </summary>
    public async Task StopAsync()
    {
        _stopping = true;
        await _cts.CancelAsync();

        if (_process is { HasExited: false })
        {
            try
            {
                _process.Kill(entireProcessTree: true);
            }
            catch (Exception ex)
            {
                Logger.Warn($"[CameraBuffer:{CameraName}] Exception killing FFmpeg: {ex.Message}");
            }
        }

        // Wait for tasks to wind down
        if (_readTask != null)
        {
            try { await _readTask.WaitAsync(TimeSpan.FromSeconds(5)); }
            catch { /* expected on cancellation */ }
        }
        if (_trimTask != null)
        {
            try { await _trimTask.WaitAsync(TimeSpan.FromSeconds(5)); }
            catch { /* expected on cancellation */ }
        }

        lock (_bufferLock)
        {
            _buffer.Clear();
            _totalBytes = 0;
        }

        _process?.Dispose();
        _process = null;
        Logger.Info($"[CameraBuffer:{CameraName}] Stopped");
    }

    /// <summary>
    /// Saves the current buffer to a temp file, re-muxes it to WebM with FFmpeg, and returns the output file path.
    /// Caller is responsible for deleting the returned file after use.
    /// </summary>
    public async Task<string> SaveToFileAsync(CancellationToken ct)
    {
        byte[] snapshot;
        lock (_bufferLock)
        {
            if (_buffer.Count == 0)
                throw new InvalidOperationException("Buffer is empty, nothing to save");

            var totalLen = _buffer.Sum(b => (long)b.Data.Length);
            snapshot = new byte[totalLen];
            var offset = 0;
            foreach (var (_, data) in _buffer)
            {
                Buffer.BlockCopy(data, 0, snapshot, offset, data.Length);
                offset += data.Length;
            }
        }

        var id = Guid.NewGuid().ToString("N")[..8];
        var tempTs = Path.Combine(Path.GetTempPath(), $"clip_{CameraName.Replace(' ', '_')}_{id}.ts");
        var tempWebm = Path.Combine(Path.GetTempPath(), $"clip_{CameraName.Replace(' ', '_')}_{id}.webm");

        try
        {
            await File.WriteAllBytesAsync(tempTs, snapshot, ct);

            var ffmpegArgs = $"-i \"{tempTs}\" -c:v libvpx-vp9 -c:a libopus -y \"{tempWebm}\"";
            var processInfo = new ProcessStartInfo
            {
                FileName = _ffmpegPath,
                Arguments = ffmpegArgs,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var remux = Process.Start(processInfo);
            if (remux == null)
            {
                Logger.Error($"[CameraBuffer:{CameraName}] Failed to start FFmpeg re-mux process");
                throw new InvalidOperationException("Failed to start FFmpeg re-mux");
            }

            await remux.WaitForExitAsync(ct);

            if (remux.ExitCode != 0)
            {
                var error = await remux.StandardError.ReadToEndAsync(ct);
                Logger.Error($"[CameraBuffer:{CameraName}] FFmpeg re-mux failed (exit {remux.ExitCode}): {error}");
                throw new InvalidOperationException($"FFmpeg re-mux failed with exit code {remux.ExitCode}");
            }

            Logger.Info($"[CameraBuffer:{CameraName}] Re-muxed {snapshot.Length} bytes TS -> WebM at {tempWebm}");
            return tempWebm;
        }
        finally
        {
            // Always clean up the intermediate .ts file
            try { File.Delete(tempTs); }
            catch { /* best effort */ }
        }
    }

    private async Task ReadLoopAsync()
    {
        var token = _cts.Token;
        try
        {
            var stream = _process!.StandardOutput.BaseStream;
            var chunk = new byte[ReadChunkSize];
            while (!token.IsCancellationRequested)
            {
                var bytesRead = await stream.ReadAsync(chunk.AsMemory(0, ReadChunkSize), token);
                if (bytesRead == 0)
                {
                    Logger.Debug($"[CameraBuffer:{CameraName}] FFmpeg stdout reached EOF");
                    break;
                }

                var copy = new byte[bytesRead];
                Buffer.BlockCopy(chunk, 0, copy, 0, bytesRead);

                lock (_bufferLock)
                {
                    _buffer.Add((DateTimeOffset.UtcNow, copy));
                    _totalBytes += bytesRead;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown
        }
        catch (Exception ex)
        {
            Logger.Error($"[CameraBuffer:{CameraName}] ReadLoop exception: {ex}");
        }
    }

    private async Task TrimLoopAsync()
    {
        var token = _cts.Token;
        try
        {
            while (!token.IsCancellationRequested)
            {
                await Task.Delay(TrimInterval, token);
                var cutoff = DateTimeOffset.UtcNow - MaxBufferAge;
                lock (_bufferLock)
                {
                    var removeCount = 0;
                    for (var i = 0; i < _buffer.Count; i++)
                    {
                        if (_buffer[i].Timestamp < cutoff)
                            removeCount++;
                        else
                            break;
                    }

                    if (removeCount > 0)
                    {
                        for (var i = 0; i < removeCount; i++)
                            _totalBytes -= _buffer[i].Data.Length;
                        _buffer.RemoveRange(0, removeCount);
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown
        }
    }

    private async Task MonitorProcessAsync()
    {
        if (_process == null) return;
        try
        {
            await _process.WaitForExitAsync();
        }
        catch
        {
            return;
        }

        if (_stopping) return;

        // FFmpeg exited on its own — this is unexpected
        var exitCode = -1;
        try { exitCode = _process.ExitCode; } catch { /* process may be disposed */ }
        Logger.Error($"[CameraBuffer:{CameraName}] FFmpeg died unexpectedly (exit code {exitCode})");

        try
        {
            var stderr = await _process.StandardError.ReadToEndAsync();
            if (!string.IsNullOrWhiteSpace(stderr))
                Logger.Error($"[CameraBuffer:{CameraName}] FFmpeg stderr: {stderr[..Math.Min(stderr.Length, 2000)]}");
        }
        catch { /* best effort */ }

        OnDied?.Invoke(this);
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        _cts.Dispose();
    }
}
