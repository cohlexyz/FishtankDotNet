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

public class ClipSaveCommand : ICommand
{
    public List<Regex> Patterns => [new Regex(@"^clip save (?<camera>.+)$")];
    public string? HelpText => "Save the buffer for a camera as a clip and upload it";
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
        await botInstance.SendChatMessageAsync($"Saving clip for {camera}...", true, autoDeleteAfter: TimeSpan.FromSeconds(30));
        var result = await clipService.SaveAsync(camera, FishtankCameras.Cameras, ctx);
        await botInstance.SendChatMessageAsync(result, true);
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