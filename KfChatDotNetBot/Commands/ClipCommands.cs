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
        ["Dorm"] = "https://epyc.goran.jetzt/dmrm-5/index.m3u8",
        ["Director Mode"] = "https://epyc.goran.jetzt/dirc-5/index.m3u8",
        ["Confessional"] = "https://epyc.goran.jetzt/cfsl-5/index.m3u8",
        ["Balcony"] = "https://epyc.goran.jetzt/bkny-5/index.m3u8",
        ["Foyer"] = "https://epyc.goran.jetzt/foyr-5/index.m3u8",
        ["Closet"] = "https://epyc.goran.jetzt/dmcl-5/index.m3u8",
        ["Glassroom"] = "https://epyc.goran.jetzt/gsrm-5/index.m3u8",
        ["BRRR"] = "https://epyc.goran.jetzt/brrr-5/index.m3u8",
        ["Corridor"] = "https://epyc.goran.jetzt/codr-5/index.m3u8",
        ["Bar PTZ"] = "https://epyc.goran.jetzt/brpz-5/index.m3u8",
        ["Market Alternate"] = "https://epyc.goran.jetzt/mrke2-5/index.m3u8",
        ["Kitchen"] = "https://epyc.goran.jetzt/ktch-5/index.m3u8",
        ["Bar Alternate"] = "https://epyc.goran.jetzt/brrr2-5/index.m3u8",
        ["Dorm Alternate"] = "https://epyc.goran.jetzt/dmrm2-5/index.m3u8",
        ["Jacuzzi"] = "https://epyc.goran.jetzt/jckz-5/index.m3u8",
        ["Dining Room"] = "https://epyc.goran.jetzt/dnrm-5/index.m3u8",
        ["Market"] = "https://epyc.goran.jetzt/mrke-5/index.m3u8",
        ["Hallway Down"] = "https://epyc.goran.jetzt/hwdn-5/index.m3u8",
        ["Hallway Up"] = "https://epyc.goran.jetzt/hwup-5/index.m3u8",
    };
}

public class ClipStartCommand : ICommand
{
    public List<Regex> Patterns => [new Regex(@"^clip start (?<camera>.+)$")];
    public string? HelpText => "Start buffering a camera stream (max 3, oldest evicted)";
    public UserRight RequiredRight => UserRight.Clipper;
    public TimeSpan Timeout => TimeSpan.FromSeconds(30);
    public RateLimitOptionsModel? RateLimitOptions => null;

    public async Task RunCommand(ChatBot botInstance, MessageModel message, UserDbModel user, GroupCollection arguments, CancellationToken ctx)
    {
        var clipService = botInstance.BotServices.ClipService;
        if (clipService == null)
        {
            await botInstance.SendChatMessageAsync("Clip service is not initialized", true);
            return;
        }

        var camera = arguments["camera"].Value.Trim();
        var result = await clipService.StartAsync(camera, FishtankCameras.Cameras);
        await botInstance.SendChatMessageAsync(result, true);
    }
}

public class ClipSwitchCommand : ICommand
{
    public List<Regex> Patterns => [new Regex(@"^clip switch (?<camera>.+)$")];
    public string? HelpText => "Switch to a camera stream (alias for clip start)";
    public UserRight RequiredRight => UserRight.Clipper;
    public TimeSpan Timeout => TimeSpan.FromSeconds(30);
    public RateLimitOptionsModel? RateLimitOptions => null;

    public async Task RunCommand(ChatBot botInstance, MessageModel message, UserDbModel user, GroupCollection arguments, CancellationToken ctx)
    {
        var clipService = botInstance.BotServices.ClipService;
        if (clipService == null)
        {
            await botInstance.SendChatMessageAsync("Clip service is not initialized", true);
            return;
        }

        var camera = arguments["camera"].Value.Trim();
        var result = await clipService.StartAsync(camera, FishtankCameras.Cameras);
        await botInstance.SendChatMessageAsync(result, true);
    }
}

public class ClipStopCommand : ICommand
{
    public List<Regex> Patterns => [new Regex(@"^clip stop(?:\s+(?<camera>.+))?$")];
    public string? HelpText => "Stop buffering a camera (or all if no name given)";
    public UserRight RequiredRight => UserRight.Clipper;
    public TimeSpan Timeout => TimeSpan.FromSeconds(30);
    public RateLimitOptionsModel? RateLimitOptions => null;

    public async Task RunCommand(ChatBot botInstance, MessageModel message, UserDbModel user, GroupCollection arguments, CancellationToken ctx)
    {
        var clipService = botInstance.BotServices.ClipService;
        if (clipService == null)
        {
            await botInstance.SendChatMessageAsync("Clip service is not initialized", true);
            return;
        }

        var camera = arguments["camera"].Success ? arguments["camera"].Value.Trim() : null;
        var result = await clipService.StopAsync(camera, FishtankCameras.Cameras);
        await botInstance.SendChatMessageAsync(result, true);
    }
}

public class ClipBeginCommand : ICommand
{
    public List<Regex> Patterns => [new Regex(@"^clip begin (?<camera>.+)$")];
    public string? HelpText => "Set a marker at the current time for a camera (use with !clip save to clip from the marker)";
    public UserRight RequiredRight => UserRight.Clipper;
    public TimeSpan Timeout => TimeSpan.FromSeconds(10);
    public RateLimitOptionsModel? RateLimitOptions => null;

    public async Task RunCommand(ChatBot botInstance, MessageModel message, UserDbModel user, GroupCollection arguments, CancellationToken ctx)
    {
        var clipService = botInstance.BotServices.ClipService;
        if (clipService == null)
        {
            await botInstance.SendChatMessageAsync("Clip service is not initialized", true);
            return;
        }

        var camera = arguments["camera"].Value.Trim();
        var result = clipService.SetMarker(camera, FishtankCameras.Cameras);
        await botInstance.SendChatMessageAsync(result, true);
    }
}

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

    public async Task RunCommand(ChatBot botInstance, MessageModel message, UserDbModel user, GroupCollection arguments, CancellationToken ctx)
    {
        var clipService = botInstance.BotServices.ClipService;
        if (clipService == null)
        {
            await botInstance.SendChatMessageAsync("Clip service is not initialized", true);
            return;
        }

        if (!await Zipline.IsZiplineEnabled())
        {
            await botInstance.SendChatMessageAsync("Zipline is not configured", true);
            return;
        }

        var camera = arguments["camera"].Value.Trim();

        // Resolve the camera name so we can look up markers by canonical name
        var (resolvedCamera, _) = ClipService.FuzzyMatchCamera(camera, FishtankCameras.Cameras);

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
                    $"No marker set for {resolvedCamera}. Use !clip begin {resolvedCamera} first, or specify a duration (e.g. !clip save {resolvedCamera} 30s)", true);
                return;
            }

            var age = DateTimeOffset.UtcNow - marker.Value;
            if (age > TimeSpan.FromMinutes(8))
            {
                await botInstance.SendChatMessageAsync(
                    $"Marker for {resolvedCamera} is too old ({age.Humanize(2)}). The buffer only holds 8 minutes.", true);
                clipService.ClearMarker(resolvedCamera);
                return;
            }

            trimTo = age;
            markerCameraName = resolvedCamera;
        }
        else
        {
            // Camera didn't match — let SaveAsync handle the fuzzy match error
        }

        var trimLabel = trimTo.HasValue ? $" (last {trimTo.Value.Humanize(2)})" : "";
        var sent = await botInstance.SendChatMessageAsync($"Saving clip for {camera}{trimLabel}...", true);
        var gotUuid = await botInstance.WaitForChatMessageAsync(sent, TimeSpan.FromSeconds(10), ctx);

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

        var result = await clipService.SaveAsync(camera, FishtankCameras.Cameras, ctx, progress, trimTo);
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
        await botInstance.SendChatMessageAsync($"@{user.KfUsername}, here's your clip: {result}", true);
        if (gotUuid)
            await botInstance.KfClient.DeleteMessageAsync(sent.ChatMessageUuid!);
    }
}

public class ClipListCommand : ICommand
{
    public List<Regex> Patterns => [new Regex(@"^clip list$")];
    public string? HelpText => "List currently buffered cameras";
    public UserRight RequiredRight => UserRight.Clipper;
    public TimeSpan Timeout => TimeSpan.FromSeconds(10);
    public RateLimitOptionsModel? RateLimitOptions => null;

    public async Task RunCommand(ChatBot botInstance, MessageModel message, UserDbModel user, GroupCollection arguments, CancellationToken ctx)
    {
        var clipService = botInstance.BotServices.ClipService;
        if (clipService == null)
        {
            await botInstance.SendChatMessageAsync("Clip service is not initialized", true);
            return;
        }

        var buffers = clipService.GetActiveBuffers();
        if (buffers.Count == 0)
        {
            await botInstance.SendChatMessageAsync("No cameras are currently being buffered", true);
            return;
        }

        var lines = buffers.Select(b =>
            $"{b.Name}: {b.Duration.Humanize(2)} ({b.Bytes.Bytes().Humanize()})");
        await botInstance.SendChatMessageAsync($"Active buffers: {string.Join(" | ", lines)}", true);
    }
}

public class ClipCamerasCommand : ICommand
{
    public List<Regex> Patterns => [new Regex(@"^clip cameras$")];
    public string? HelpText => "List all available camera names";
    public UserRight RequiredRight => UserRight.Clipper;
    public TimeSpan Timeout => TimeSpan.FromSeconds(10);
    public RateLimitOptionsModel? RateLimitOptions => null;

    public async Task RunCommand(ChatBot botInstance, MessageModel message, UserDbModel user, GroupCollection arguments, CancellationToken ctx)
    {
        var names = string.Join(", ", FishtankCameras.Cameras.Keys);
        await botInstance.SendChatMessageAsync($"Available cameras: {names}", true);
    }
}

public class ClipQueueCommand : ICommand
{
    public List<Regex> Patterns => [new Regex(@"^clip queue$")];
    public string? HelpText => "Show the current clip encode/upload queue status";
    public UserRight RequiredRight => UserRight.Clipper;
    public TimeSpan Timeout => TimeSpan.FromSeconds(10);
    public RateLimitOptionsModel? RateLimitOptions => null;

    public async Task RunCommand(ChatBot botInstance, MessageModel message, UserDbModel user, GroupCollection arguments, CancellationToken ctx)
    {
        var clipService = botInstance.BotServices.ClipService;
        if (clipService == null)
        {
            await botInstance.SendChatMessageAsync("Clip service is not initialized", true);
            return;
        }

        var (current, pending) = clipService.GetQueueStatus();
        if (current == null && pending.Count == 0)
        {
            await botInstance.SendChatMessageAsync("Clip queue is empty", true);
            return;
        }

        var parts = new List<string>();
        if (current != null)
            parts.Add($"Processing: {current}");
        if (pending.Count > 0)
            parts.Add($"Queued: {string.Join(", ", pending)}");
        await botInstance.SendChatMessageAsync(string.Join(" | ", parts), true);
    }
}