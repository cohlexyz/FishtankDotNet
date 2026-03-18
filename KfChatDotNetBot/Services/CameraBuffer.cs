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
    private readonly string? _audioUrl;
    private readonly CancellationTokenSource _cts;
    private Process? _process;
    private Task? _readTask;
    private Task? _trimTask;
    private Task? _stderrTask;

    private readonly List<(DateTimeOffset Timestamp, byte[] Data)> _buffer = [];
    private readonly object _bufferLock = new();
    private long _totalBytes;
    private bool _stopping;
    private int _restartAttempt;

    private static readonly TimeSpan MaxBufferAge = TimeSpan.FromMinutes(8);
    private static readonly TimeSpan TrimInterval = TimeSpan.FromSeconds(5);
    private const int ReadChunkSize = 8192;
    private const int MaxRestartAttempts = 5;

    public CameraBuffer(string cameraName, string streamUrl, string ffmpegPath, CancellationToken ct, string? audioUrl = null)
    {
        CameraName = cameraName;
        StreamUrl = streamUrl;
        _ffmpegPath = ffmpegPath;
        _audioUrl = audioUrl;
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
    }

    /// <summary>
    /// Spawns an FFmpeg process and starts the read/stderr tasks and the process monitor.
    /// Does not create the trim task — that is done once in <see cref="Start"/>.
    /// </summary>
    private void SpawnProcess()
    {
        var processInfo = new ProcessStartInfo
        {
            FileName = _ffmpegPath,
            // -re is not used here: we want to read as fast as the live stream provides
            Arguments = _audioUrl != null
                ? $"-i \"{StreamUrl}\" -i \"{_audioUrl}\" -map 0:v -map 1:a -c copy -f mpegts pipe:1"
                : $"-i \"{StreamUrl}\" -c copy -f mpegts pipe:1",
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
        _stderrTask = Task.Run(DrainStderrAsync, _cts.Token);

        // Fire-and-forget: monitor the FFmpeg process for unexpected exit
        _ = Task.Run(MonitorProcessAsync, CancellationToken.None);
    }

    /// <summary>
    /// Spawns FFmpeg to read the live stream and begins buffering its output.
    /// </summary>
    public void Start()
    {
        SpawnProcess();
        _trimTask = Task.Run(TrimLoopAsync, _cts.Token);
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
        if (_stderrTask != null)
        {
            try { await _stderrTask.WaitAsync(TimeSpan.FromSeconds(5)); }
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
    /// Snapshots the current buffer to a temp .ts file on disk.
    /// Returns the file path, the cutoff timestamp, and the buffer duration.
    /// Caller is responsible for deleting the returned file after use.
    /// Pass the returned cutoff to <see cref="ResetBuffer"/> to clear only the saved data.
    /// </summary>
    public async Task<(string TsPath, DateTimeOffset Cutoff, TimeSpan Duration)> SnapshotToFileAsync(CancellationToken ct, TimeSpan? trimTo = null)
    {
        byte[] snapshot;
        DateTimeOffset cutoff;
        TimeSpan duration;
        lock (_bufferLock)
        {
            if (_buffer.Count == 0)
                throw new InvalidOperationException("Buffer is empty, nothing to save");

            cutoff = _buffer[^1].Timestamp;

            // Find the start index for the requested window
            var startIdx = 0;
            if (trimTo.HasValue)
            {
                var windowStart = cutoff - trimTo.Value;
                for (var i = 0; i < _buffer.Count; i++)
                {
                    if (_buffer[i].Timestamp >= windowStart)
                    {
                        startIdx = i;
                        break;
                    }
                }
            }

            duration = cutoff - _buffer[startIdx].Timestamp;
            var totalLen = 0L;
            for (var i = startIdx; i < _buffer.Count; i++)
                totalLen += _buffer[i].Data.Length;

            snapshot = new byte[totalLen];
            var offset = 0;
            for (var i = startIdx; i < _buffer.Count; i++)
            {
                Buffer.BlockCopy(_buffer[i].Data, 0, snapshot, offset, _buffer[i].Data.Length);
                offset += _buffer[i].Data.Length;
            }
        }

        var id = Guid.NewGuid().ToString("N")[..8];
        var tempTs = Path.Combine(Path.GetTempPath(), $"clip_{CameraName.Replace(' ', '_')}_{id}.ts");
        await File.WriteAllBytesAsync(tempTs, snapshot, ct);
        Logger.Info($"[CameraBuffer:{CameraName}] Snapshotted {snapshot.Length} bytes ({duration.TotalSeconds:N0}s) to {tempTs}");
        return (tempTs, cutoff, duration);
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

    private async Task DrainStderrAsync()
    {
        var token = _cts.Token;
        try
        {
            var reader = _process!.StandardError;
            while (!token.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(token);
                if (line == null) break;
                Logger.Debug($"[CameraBuffer:{CameraName}] FFmpeg: {line}");
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown
        }
        catch (Exception ex)
        {
            Logger.Debug($"[CameraBuffer:{CameraName}] StderrDrain exception: {ex.Message}");
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
        var processStartTime = DateTimeOffset.UtcNow;
        try
        {
            await _process.WaitForExitAsync();
        }
        catch
        {
            return;
        }

        if (_stopping) return;

        // If the process ran long enough before crashing, treat it as a fresh start
        if (DateTimeOffset.UtcNow - processStartTime > TimeSpan.FromMinutes(2))
            _restartAttempt = 0;

        var exitCode = -1;
        try { exitCode = _process.ExitCode; } catch { /* process may be disposed */ }
        Logger.Error($"[CameraBuffer:{CameraName}] FFmpeg died unexpectedly (exit code {exitCode})");

        if (_restartAttempt >= MaxRestartAttempts)
        {
            Logger.Error($"[CameraBuffer:{CameraName}] Max restart attempts ({MaxRestartAttempts}) reached, giving up");
            OnDied?.Invoke(this);
            return;
        }

        // Exponential backoff: 5, 10, 20, 40, 60 seconds (capped)
        var delaySeconds = Math.Min(5 * (1 << _restartAttempt), 60);
        Logger.Info($"[CameraBuffer:{CameraName}] Restarting in {delaySeconds}s (attempt {_restartAttempt + 1}/{MaxRestartAttempts})");
        _restartAttempt++;

        await Task.Delay(TimeSpan.FromSeconds(delaySeconds));
        if (_stopping) return;

        // Wait for previous read/stderr tasks to finish cleanly before spawning new ones
        if (_readTask != null)
            try { await _readTask.WaitAsync(TimeSpan.FromSeconds(5)); } catch { /* best effort */ }
        if (_stderrTask != null)
            try { await _stderrTask.WaitAsync(TimeSpan.FromSeconds(5)); } catch { /* best effort */ }

        _process?.Dispose();
        _process = null;

        try
        {
            SpawnProcess();
            Logger.Info($"[CameraBuffer:{CameraName}] Successfully restarted");
        }
        catch (Exception ex)
        {
            Logger.Error($"[CameraBuffer:{CameraName}] Failed to restart FFmpeg: {ex.Message}");
            OnDied?.Invoke(this);
        }
    }

    /// <summary>
    /// Clears buffer data up to (and including) the given cutoff timestamp,
    /// preserving any chunks that arrived after the cutoff so consecutive clips don't have gaps.
    /// If no cutoff is provided, clears the entire buffer.
    /// </summary>
    public void ResetBuffer(DateTimeOffset? cutoff = null)
    {
        lock (_bufferLock)
        {
            if (cutoff == null)
            {
                _buffer.Clear();
                _totalBytes = 0;
            }
            else
            {
                var removeCount = 0;
                for (var i = 0; i < _buffer.Count; i++)
                {
                    if (_buffer[i].Timestamp <= cutoff.Value)
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
        Logger.Info($"[CameraBuffer:{CameraName}] Buffer reset (cutoff: {cutoff?.ToString() ?? "all"})");
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        _cts.Dispose();
    }
}
