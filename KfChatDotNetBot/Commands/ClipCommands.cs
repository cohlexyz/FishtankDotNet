using System.Text.RegularExpressions;
using Humanizer;
using KfChatDotNetBot.Models;
using KfChatDotNetBot.Models.DbModels;
using KfChatDotNetBot.Services;
using KfChatDotNetWsClient.Models.Events;

namespace KfChatDotNetBot.Commands;

public static class FishtankCameras
{
    public static readonly Dictionary<string, string> Cameras = new(StringComparer.OrdinalIgnoreCase)
    {
        {"Director Mode", "https://streams-e.fishtank.live/hls/live+dirc-5/5_2/index.m3u8?tkn="},
        {"Dorm", "https://streams-e.fishtank.live/hls/live+dmrm-5/5_2/index.m3u8?tkn="},
        {"Dorm Alternate", "https://streams-e.fishtank.live/hls/live+dmrm2-5/5_2/index.m3u8?tkn="},
        {"Closet", "https://streams-e.fishtank.live/hls/live+dmcl-5/5_2/index.m3u8?tkn="},
        {"Bar", "https://streams-e.fishtank.live/hls/live+brrr-5/5_2/index.m3u8?tkn="},
        {"Bar Alternate", "https://streams-e.fishtank.live/hls/live+brrr2-5/5_2/index.m3u8?tkn="},
        {"Kitchen", "https://streams-e.fishtank.live/hls/live+ktch-5/5_2/index.m3u8?tkn="},
        {"Cameraman", "https://streams-e.fishtank.live/hls/live+cameraman2-5/5_2/index.m3u8?tkn="},
        {"Hallway", "https://streams-e.fishtank.live/hls/live+hwdn-5/5_2/index.m3u8?tkn="},
        {"Jacuzzi", "https://streams-e.fishtank.live/hls/live+jckz-5/5_2/index.m3u8?tkn="},
        {"Bar PTZ", "https://streams-e.fishtank.live/hls/live+brpz-5/5_2/index.m3u8?tkn="},
        {"Dining Room", "https://streams-e.fishtank.live/hls/live+dnrm-5/5_2/index.m3u8?tkn="},
        {"Market", "https://streams-e.fishtank.live/hls/live+mrke-5/5_2/index.m3u8?tkn="},
        {"Market Alternate", "https://streams-e.fishtank.live/hls/live+mrke2-5/5_2/index.m3u8?tkn="},
        {"Foyer", "https://streams-e.fishtank.live/hls/live+foyr-5/5_2/index.m3u8?tkn="},
        {"Glassroom", "https://streams-e.fishtank.live/hls/live+gsrm-5/5_2/index.m3u8?tkn="},
        {"Computer Lab", "https://streams-e.fishtank.live/hls/live+bbcl-5/5_2/index.m3u8?tkn="},
        {"???", "https://streams-e.fishtank.live/hls/live+bare-5/5_2/index.m3u8?tkn="},
        {"Confessional", "https://streams-e.fishtank.live/hls/live+cfsl-5/5_2/index.m3u8?tkn="},
        {"Corridor", "https://streams-e.fishtank.live/hls/live+codr-5/5_2/index.m3u8?tkn="},
        {"East Wing", "https://streams-e.fishtank.live/hls/live+bkny-5/5_2/index.m3u8?tkn="},
        {"West Wing", "https://streams-e.fishtank.live/hls/live+hwup-5/5_2/index.m3u8?tkn="},
        {"???2", "https://streams-e.fishtank.live/hls/live+br3g-5/5_2/index.m3u8?tkn="},
        {"Jungle Room", "https://streams-e.fishtank.live/hls/live+br4j-5/5_2/index.m3u8?tkn="}
    };

    /// <summary>
    /// Returns the camera dictionary with the live stream token appended to each URL.
    /// </summary>
    public static Dictionary<string, string> GetCamerasWithToken(string? token)
    {
        var result = new Dictionary<string, string>(Cameras.Count, StringComparer.OrdinalIgnoreCase);
        var tkn = token ?? string.Empty;
        foreach (var (name, url) in Cameras)
        {
            result[name] = url + tkn;
        }
        return result;
    }
}

[DontDeleteInvocationMessage]
public class ClipStartCommand : ICommand
{
    public List<Regex> Patterns => [new Regex(@"^clip start (?<camera>.+)$")];
    public string? HelpText => "Start buffering a camera stream (max 3, oldest evicted)";
    public UserRight RequiredRight => UserRight.Clipper;
    public TimeSpan Timeout => TimeSpan.FromSeconds(30);
    public RateLimitOptionsModel? RateLimitOptions => null;

    public bool WhisperCanInvoke => true;

    public async Task RunCommand(ChatBot botInstance, BotCommandMessageModel message, UserDbModel user, GroupCollection arguments, CancellationToken ctx)
    {
        var clipService = botInstance.BotServices.ClipService;
        if (clipService == null)
        {
            await botInstance.SendChatMessageAsync("Clip service is not initialized", true);
            return;
        }

        var camera = arguments["camera"].Value.Trim();
        var cameras = FishtankCameras.GetCamerasWithToken(botInstance.BotServices.FishtankTokenService?.CurrentToken);
        var result = await clipService.StartAsync(camera, cameras);
        await botInstance.SendChatMessageAsync(result, true, autoDeleteAfter: TimeSpan.FromSeconds(10));
    }
}
[DontDeleteInvocationMessage]

public class ClipSwitchCommand : ICommand
{
    public List<Regex> Patterns => [new Regex(@"^clip switch (?<camera>.+)$")];
    public string? HelpText => "Switch to a camera stream (alias for clip start)";
    public UserRight RequiredRight => UserRight.Clipper;
    public TimeSpan Timeout => TimeSpan.FromSeconds(30);
    public RateLimitOptionsModel? RateLimitOptions => null;

    public bool WhisperCanInvoke => true;

    public async Task RunCommand(ChatBot botInstance, BotCommandMessageModel message, UserDbModel user, GroupCollection arguments, CancellationToken ctx)
    {
        var clipService = botInstance.BotServices.ClipService;
        if (clipService == null)
        {
            await botInstance.SendChatMessageAsync("Clip service is not initialized", true, whisperTo: user.KfId);
            return;
        }

        var camera = arguments["camera"].Value.Trim();
        var cameras = FishtankCameras.GetCamerasWithToken(botInstance.BotServices.FishtankTokenService?.CurrentToken);
        var result = await clipService.StartAsync(camera, cameras);
        await botInstance.SendChatMessageAsync(result, true, autoDeleteAfter: TimeSpan.FromSeconds(10));
    }
}

[DontDeleteInvocationMessage]

public class ClipStopCommand : ICommand
{
    public List<Regex> Patterns => [new Regex(@"^clip stop(?:\s+(?<camera>.+))?$")];
    public string? HelpText => "Stop buffering a camera (or all if no name given)";
    public UserRight RequiredRight => UserRight.Clipper;
    public TimeSpan Timeout => TimeSpan.FromSeconds(30);
    public RateLimitOptionsModel? RateLimitOptions => null;

    public bool WhisperCanInvoke => true;

    public async Task RunCommand(ChatBot botInstance, BotCommandMessageModel message, UserDbModel user, GroupCollection arguments, CancellationToken ctx)
    {
        var clipService = botInstance.BotServices.ClipService;
        if (clipService == null)
        {
            await botInstance.SendChatMessageAsync("Clip service is not initialized", true, whisperTo: user.KfId);
            return;
        }

        var camera = arguments["camera"].Success ? arguments["camera"].Value.Trim() : null;
        var cameras = FishtankCameras.GetCamerasWithToken(botInstance.BotServices.FishtankTokenService?.CurrentToken);
        var result = await clipService.StopAsync(camera, cameras);
        await botInstance.SendChatMessageAsync(result, true, autoDeleteAfter: TimeSpan.FromSeconds(10));
    }
}

[DontDeleteInvocationMessage]

public class ClipBeginCommand : ICommand
{
    public List<Regex> Patterns => [new Regex(@"^clip begin (?<camera>.+)$")];
    public string? HelpText => "Set a marker at the current time for a camera (use with !clip save to clip from the marker)";
    public UserRight RequiredRight => UserRight.Clipper;
    public TimeSpan Timeout => TimeSpan.FromSeconds(10);
    public RateLimitOptionsModel? RateLimitOptions => null;

    public bool WhisperCanInvoke => true;

    public async Task RunCommand(ChatBot botInstance, BotCommandMessageModel message, UserDbModel user, GroupCollection arguments, CancellationToken ctx)
    {
        var clipService = botInstance.BotServices.ClipService;
        if (clipService == null)
        {
            await botInstance.SendChatMessageAsync("Clip service is not initialized", true, whisperTo: user.KfId);
            return;
        }

        var camera = arguments["camera"].Value.Trim();
        var cameras = FishtankCameras.GetCamerasWithToken(botInstance.BotServices.FishtankTokenService?.CurrentToken);
        var result = clipService.SetMarker(camera, cameras);
        await botInstance.SendChatMessageAsync(result, true);
    }
}

[DontDeleteInvocationMessage]
public class ClipSaveCommand : ICommand
{
    public List<Regex> Patterns => [new Regex(@"^clip save (?<camera>.+?)(?:\s+(?<duration>\d+[smSM]))?$")];
    public string? HelpText => "Save the buffer for a camera as a clip and upload it (append e.g. 30s or 2m to trim, or use !clip begin to set a marker)";
    public UserRight RequiredRight => UserRight.Clipper;
    public TimeSpan Timeout => TimeSpan.FromMinutes(5);
    public RateLimitOptionsModel? RateLimitOptions => new()
    {
        Window = TimeSpan.FromSeconds(30),
        MaxInvocations = 2
    };

    public bool WhisperCanInvoke => true;

    public async Task RunCommand(ChatBot botInstance, BotCommandMessageModel message, UserDbModel user, GroupCollection arguments, CancellationToken ctx)
    {
        var clipService = botInstance.BotServices.ClipService;
        if (clipService == null)
        {
            await botInstance.SendChatMessageAsync("Clip service is not initialized", true, whisperTo: user.KfId);
            return;
        }

        if (!await Zipline.IsZiplineEnabled())
        {
            await botInstance.SendChatMessageAsync("Zipline is not configured", true, whisperTo: user.KfId);
            return;
        }

        var camera = arguments["camera"].Value.Trim();
        var cameras = FishtankCameras.GetCamerasWithToken(botInstance.BotServices.FishtankTokenService?.CurrentToken);

        // Resolve the camera name so we can look up markers by canonical name
        var (resolvedCamera, _) = ClipService.FuzzyMatchCamera(camera, cameras);

        TimeSpan? trimTo = null;
        string? markerCameraName = null;
        if (arguments["duration"].Success)
        {
            var durStr = arguments["duration"].Value;
            var amount = int.Parse(durStr[..^1]);
            trimTo = char.ToLower(durStr[^1]) == 'm' ? TimeSpan.FromMinutes(amount) : TimeSpan.FromSeconds(amount);
        }
        else if (resolvedCamera != null)
        {
            // No duration specified — check for a marker
            var marker = clipService.GetMarker(resolvedCamera);
            if (marker == null)
            {
                await botInstance.SendChatMessageAsync(
                    $"No marker set for {resolvedCamera}. Use !clip begin {resolvedCamera} first, or specify a duration (e.g. !clip save {resolvedCamera} 30s)", true, whisperTo: user.KfId);
                return;
            }

            // marker = pressTime + 10s, so pressTime = marker - 10s
            var pressAge = DateTimeOffset.UtcNow - (marker.Value - TimeSpan.FromSeconds(10));
            if (pressAge > TimeSpan.FromMinutes(8))
            {
                await botInstance.SendChatMessageAsync(
                    $"Marker for {resolvedCamera} is too old ({pressAge.Humanize(2)}). The buffer only holds 8 minutes.", true, whisperTo: user.KfId);
                clipService.ClearMarker(resolvedCamera);
                return;
            }

            // trimTo will be computed after waiting for stream data to arrive
            markerCameraName = resolvedCamera;
        }
        else
        {
            // Camera didn't match — let SaveAsync handle the fuzzy match error
        }

        var trimLabel = markerCameraName != null ? " (from marker)" : (trimTo.HasValue ? $" (last {trimTo.Value.Humanize(2)})" : "");
        var sent = await botInstance.SendChatMessageAsync($"Saving clip for {camera}{trimLabel}...", true);
        var gotUuid = await botInstance.WaitForChatMessageAsync(sent, TimeSpan.FromSeconds(10), ctx);

        // When using a marker, wait 10s for delayed stream data to arrive before snapshotting
        if (markerCameraName != null)
        {
            if (gotUuid)
                await botInstance.KfClient.EditMessageAsync(sent.ChatMessageUuid!,
                    $"Waiting for stream data to arrive for {camera}...");
            await Task.Delay(TimeSpan.FromSeconds(10), ctx);
            // trimTo = time elapsed from !clip begin press to !clip save press
            // marker = pressTime + 10s, UtcNow = saveTime + 10s → trimTo = saveTime - pressTime
            var currentMarker = clipService.GetMarker(markerCameraName);
            trimTo = currentMarker.HasValue
                ? DateTimeOffset.UtcNow - currentMarker.Value
                : TimeSpan.FromSeconds(10);
            if (trimTo <= TimeSpan.Zero)
                trimTo = TimeSpan.FromSeconds(2);
        }

        // Throttle edits to avoid spamming the server
        var lastEditPercent = -1;
        var lastStage = ClipStage.Queued;
        IProgress<ClipProgress>? progress = null;
        if (gotUuid)
        {
            progress = new Progress<ClipProgress>(p =>
            {
                var label = p.Stage switch
                {
                    ClipStage.Queued => $"Queued (position {p.QueuePosition})",
                    ClipStage.Encoding => "Encoding",
                    ClipStage.Uploading => "Uploading",
                    _ => "Processing"
                };

                // Always update on stage change; throttle within a stage to every 10%
                if (p.Stage == lastStage && p.PercentComplete / 10 == lastEditPercent / 10)
                    return;

                lastStage = p.Stage;
                lastEditPercent = p.PercentComplete;

                if (p.Stage == ClipStage.Queued)
                {
                    _ = botInstance.KfClient.EditMessageAsync(sent.ChatMessageUuid!,
                        $"Clip for {camera} queued (position {p.QueuePosition})...");
                    return;
                }

                var bar = new string('█', p.PercentComplete / 10) + new string('░', 10 - p.PercentComplete / 10);
                _ = botInstance.KfClient.EditMessageAsync(sent.ChatMessageUuid!,
                    $"{label} clip for {camera}... [{bar}] {p.PercentComplete}%");
            });
        }

        var result = await clipService.SaveAsync(camera, cameras, ctx, progress, trimTo);
        if (result.StartsWith("Error") || result.StartsWith("No active") || result.StartsWith("Buffer for") || result.StartsWith("Failed to") || result.StartsWith("Zipline"))
        {
            if (gotUuid)
                await botInstance.KfClient.EditMessageAsync(sent.ChatMessageUuid!, result);
            else
                await botInstance.SendChatMessageAsync(result, true);
            return;
        }

        // Clear the marker now that the clip was saved successfully
        if (markerCameraName != null)
            clipService.ClearMarker(markerCameraName);

        // Send the clip URL as a new message and delete the progress message
        // also send to normal chat
        await botInstance.SendChatMessageAsync($"@{user.KfUsername}, here's your clip: {result}", true);
        await botInstance.SendWhisperAsync(user.KfId, $"@{user.KfUsername}, here's your clip: {result}");
        if (gotUuid)
            await botInstance.KfClient.DeleteMessageAsync(sent.ChatMessageUuid!);
    }
}

[DontDeleteInvocationMessage]
public class ClipListCommand : ICommand
{
    public List<Regex> Patterns => [new Regex(@"^clip list$")];
    public string? HelpText => "List currently buffered cameras";
    public UserRight RequiredRight => UserRight.Clipper;
    public TimeSpan Timeout => TimeSpan.FromSeconds(10);
    public RateLimitOptionsModel? RateLimitOptions => null;

    public bool WhisperCanInvoke => true;

    public async Task RunCommand(ChatBot botInstance, BotCommandMessageModel message, UserDbModel user, GroupCollection arguments, CancellationToken ctx)
    {
        var clipService = botInstance.BotServices.ClipService;
        if (clipService == null)
        {
            await botInstance.SendChatMessageAsync("Clip service is not initialized", true, whisperTo: user.KfId);
            return;
        }

        var buffers = clipService.GetActiveBuffers();
        if (buffers.Count == 0)
        {
            await botInstance.SendChatMessageAsync("No cameras are currently being buffered", true, whisperTo: user.KfId);
            return;
        }

        var lines = buffers.Select(b =>
            $"{b.Name}: {b.Duration.Humanize(2)} ({b.Bytes.Bytes().Humanize()})");
        await botInstance.SendChatMessageAsync($"Active buffers: {string.Join(" | ", lines)}", true, autoDeleteAfter: TimeSpan.FromSeconds(10));
    }
}

public class ClipCamerasCommand : ICommand
{
    public List<Regex> Patterns => [new Regex(@"^clip cameras$")];
    public string? HelpText => "List all available camera names";
    public UserRight RequiredRight => UserRight.Clipper;
    public TimeSpan Timeout => TimeSpan.FromSeconds(10);
    public RateLimitOptionsModel? RateLimitOptions => null;

    public bool WhisperCanInvoke => true;

    public async Task RunCommand(ChatBot botInstance, BotCommandMessageModel message, UserDbModel user, GroupCollection arguments, CancellationToken ctx)
    {
        var cameras = FishtankCameras.GetCamerasWithToken(botInstance.BotServices.FishtankTokenService?.CurrentToken);
        var names = string.Join(", ", cameras.Keys);
        await botInstance.SendChatMessageAsync($"Available cameras: {names}", true, whisperTo: user.KfId);
    }
}

public class ClipQueueCommand : ICommand
{
    public List<Regex> Patterns => [new Regex(@"^clip queue$")];
    public string? HelpText => "Show the current clip encode/upload queue status";
    public UserRight RequiredRight => UserRight.Clipper;
    public TimeSpan Timeout => TimeSpan.FromSeconds(10);
    public RateLimitOptionsModel? RateLimitOptions => null;

    public bool WhisperCanInvoke => true;

    public async Task RunCommand(ChatBot botInstance, BotCommandMessageModel message, UserDbModel user, GroupCollection arguments, CancellationToken ctx)
    {
        var clipService = botInstance.BotServices.ClipService;
        if (clipService == null)
        {
            await botInstance.SendChatMessageAsync("Clip service is not initialized", true, whisperTo: user.KfId);
            return;
        }

        var (current, pending) = clipService.GetQueueStatus();
        if (current == null && pending.Count == 0)
        {
            await botInstance.SendChatMessageAsync("Clip queue is empty", true, whisperTo: user.KfId);
            return;
        }

        var parts = new List<string>();
        if (current != null)
            parts.Add($"Processing: {current}");
        if (pending.Count > 0)
            parts.Add($"Queued: {string.Join(", ", pending)}");
        await botInstance.SendChatMessageAsync(string.Join(" | ", parts), true, whisperTo: user.KfId);
    }
}