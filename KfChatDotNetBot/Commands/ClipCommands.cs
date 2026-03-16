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
        ["Dorm"] = "https://dp-cf-cdn.goran.jetzt/live+dmrm-5.mp4",
        ["Director Mode"] = "https://dp-cf-cdn.goran.jetzt/live+dirc-5.mp4",
        ["Confessional"] = "https://dp-cf-cdn.goran.jetzt/live+cfsl-5.mp4",
        ["Balcony"] = "https://dp-cf-cdn.goran.jetzt/live+bkny-5.mp4",
        ["Foyer"] = "https://dp-cf-cdn.goran.jetzt/live+foyr-5.mp4",
        ["Closet"] = "https://dp-cf-cdn.goran.jetzt/live+dmcl-5.mp4",
        ["Glassroom"] = "https://dp-cf-cdn.goran.jetzt/live+gsrm-5.mp4",
        ["BRRR"] = "https://dp-cf-cdn.goran.jetzt/live+brrr-5.mp4",
        ["Corridor"] = "https://dp-cf-cdn.goran.jetzt/live+codr-5.mp4",
        ["Bar PTZ"] = "https://dp-cf-cdn.goran.jetzt/live+brpz-5.mp4",
        ["Market Alternate"] = "https://dp-cf-cdn.goran.jetzt/live+mrke2-5.mp4",
        ["Kitchen"] = "https://dp-cf-cdn.goran.jetzt/live+ktch-5.mp4",
        ["Bar Alternate"] = "https://dp-cf-cdn.goran.jetzt/live+brrr2-5.mp4",
        ["Dorm Alternate"] = "https://dp-cf-cdn.goran.jetzt/live+dmrm2-5.mp4",
        ["Jacuzzi"] = "https://dp-cf-cdn.goran.jetzt/live+jckz-5.mp4",
        ["Dining Room"] = "https://dp-cf-cdn.goran.jetzt/live+dnrm-5.mp4",
        ["Market"] = "https://dp-cf-cdn.goran.jetzt/live+mrke-5.mp4",
        ["Hallway Down"] = "https://dp-cf-cdn.goran.jetzt/live+hwdn-5.mp4",
        ["Hallway Up"] = "https://dp-cf-cdn.goran.jetzt/live+hwup-5.mp4",
    };
}

public class ClipStartCommand : ICommand
{
    public List<Regex> Patterns => [new Regex(@"^clip start (?<camera>.+)$")];
    public string? HelpText => "Start buffering a camera stream (max 3, oldest evicted)";
    public UserRight RequiredRight => UserRight.TrueAndHonest;
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
    public UserRight RequiredRight => UserRight.TrueAndHonest;
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
    public UserRight RequiredRight => UserRight.TrueAndHonest;
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

public class ClipSaveCommand : ICommand
{
    public List<Regex> Patterns => [new Regex(@"^clip save (?<camera>.+)$")];
    public string? HelpText => "Save the buffer for a camera as a clip and upload it";
    public UserRight RequiredRight => UserRight.TrueAndHonest;
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
        await botInstance.SendChatMessageAsync($"Saving clip for {camera}...", true, autoDeleteAfter: TimeSpan.FromSeconds(30));
        var result = await clipService.SaveAsync(camera, FishtankCameras.Cameras, ctx);
        await botInstance.SendChatMessageAsync(result, true);
    }
}

public class ClipListCommand : ICommand
{
    public List<Regex> Patterns => [new Regex(@"^clip list$")];
    public string? HelpText => "List currently buffered cameras";
    public UserRight RequiredRight => UserRight.Loser;
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
    public UserRight RequiredRight => UserRight.Loser;
    public TimeSpan Timeout => TimeSpan.FromSeconds(10);
    public RateLimitOptionsModel? RateLimitOptions => null;

    public async Task RunCommand(ChatBot botInstance, MessageModel message, UserDbModel user, GroupCollection arguments, CancellationToken ctx)
    {
        var names = string.Join(", ", FishtankCameras.Cameras.Keys);
        await botInstance.SendChatMessageAsync($"Available cameras: {names}", true);
    }
}