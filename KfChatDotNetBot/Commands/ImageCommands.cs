using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using Humanizer;
using Raffinert.FuzzySharp;
using KfChatDotNetBot.Extensions;
using KfChatDotNetBot.Models;
using KfChatDotNetBot.Models.DbModels;
using KfChatDotNetBot.Services;
using KfChatDotNetBot.Settings;
using KfChatDotNetWsClient.Models.Events;
using Microsoft.EntityFrameworkCore;
using NLog;

namespace KfChatDotNetBot.Commands;

[DontDeleteInvocationMessage]
public class AddImageCommand : ICommand
{
    public List<Regex> Patterns => [
        new Regex(@"^admin image (?<key>\w+) add (?<url>\S+)(?:\s+(?<tags>.+))?$"),
        new Regex(@"^admin images (?<key>\w+) add (?<url>\S+)(?:\s+(?<tags>.+))?$"),
        new Regex(@"^admin image (?<key>\w+) add_nigger (?<url>\S+)(?:\s+(?<tags>.+))?$"),
        new Regex(@"^admin images (?<key>\w+) add_nigger (?<url>\S+)(?:\s+(?<tags>.+))?$")
    ];
    public string? HelpText => "Add an image to the image rotation specified";
    public UserRight RequiredRight => UserRight.TrueAndHonest;
    public TimeSpan Timeout => TimeSpan.FromSeconds(10);
    public RateLimitOptionsModel? RateLimitOptions => null;
    public bool WhisperCanInvoke => false;
    public async Task RunCommand(ChatBot botInstance, BotCommandMessageModel message, UserDbModel user, GroupCollection arguments,
        CancellationToken ctx)
    {
        await using var db = new ApplicationDbContext();
        var imageKeys = (await SettingsProvider.GetValueAsync(BuiltIn.Keys.BotImageAcceptableKeys)).JsonDeserialize<List<string>>();
        if (imageKeys == null) throw new InvalidOperationException($"{BuiltIn.Keys.BotImageAcceptableKeys} was null");
        var key = arguments["key"].Value;
        var url = arguments["url"].Value;
        var tags = arguments["tags"].Success ? arguments["tags"].Value.Trim() : null;
        var niggerMode = message.Message.Contains("add_nigger");
        if (!imageKeys.Contains(key))
        {
            await botInstance.SendChatMessageAsync(
                $"Key you specified is not supported. Available keys are: {string.Join(' ', imageKeys)}", true, whisperTo: user.KfUsername);
            return;
        }

        if (!niggerMode && await db.Images.AnyAsync(i => i.Key == key && i.Url == url, ctx))
        {
            await botInstance.SendChatMessageAsync("This image already exists in the database with this key", true, whisperTo: user.KfUsername);
            return;
        }

        var (result, error) = await ImageCompressor.CompressImageAsync(url, ct: ctx);
        if (error != null)
        {
            await botInstance.SendChatMessageAsync($"Failed to add image: {error}", true, whisperTo: user.KfUsername);
            return;
        }
        if (result == null)
        {
            await botInstance.SendChatMessageAsync("Failed to add image for an unknown reason", true, whisperTo: user.KfUsername);
            return;
        }

        await db.Images.AddAsync(new ImageDbModel { Key = key, Url = result, LastSeen = DateTimeOffset.MinValue, Tags = tags }, ctx);
        await db.SaveChangesAsync(ctx);
        await botInstance.SendChatMessageAsync(
            $"You added the image to the {key} carousel: [img]{result}[/img]", true, autoDeleteAfter: TimeSpan.FromSeconds(10));
    }
}
[DontDeleteInvocationMessage]

public class RemoveImageCommand : ICommand
{
    public List<Regex> Patterns => [
        new Regex(@"^admin image (?<key>\w+) remove (?<url>.+)$"),
        new Regex(@"^admin images (?<key>\w+) remove (?<url>.+)$"),
        new Regex(@"^admin image (?<key>\w+) delete (?<url>.+)$"),
        new Regex(@"^admin images (?<key>\w+) delete (?<url>.+)$")
    ];
    public string? HelpText => "Remove an image from the image rotation specified";
    public UserRight RequiredRight => UserRight.TrueAndHonest;
    public TimeSpan Timeout => TimeSpan.FromSeconds(10);
    public RateLimitOptionsModel? RateLimitOptions => null;
    public bool WhisperCanInvoke => false;
    public async Task RunCommand(ChatBot botInstance, BotCommandMessageModel message, UserDbModel user, GroupCollection arguments,
        CancellationToken ctx)
    {
        await using var db = new ApplicationDbContext();
        var imageKeys = (await SettingsProvider.GetValueAsync(BuiltIn.Keys.BotImageAcceptableKeys)).JsonDeserialize<List<string>>();
        if (imageKeys == null) throw new InvalidOperationException($"{BuiltIn.Keys.BotImageAcceptableKeys} was null");
        var key = arguments["key"].Value;
        var url = arguments["url"].Value;
        if (!imageKeys.Contains(key))
        {
            await botInstance.SendChatMessageAsync(
                $"Key you specified is not supported. Available keys are: {string.Join(' ', imageKeys)}", true, whisperTo: user.KfUsername);
            return;
        }

        var image = await db.Images.FirstOrDefaultAsync(i => i.Key == key && i.Url == url, ctx);
        if (image == null)
        {
            await botInstance.SendChatMessageAsync("This image isn't in the database with this key", true, whisperTo: user.KfUsername);
            return;
        }

        db.Images.Remove(image);
        await db.SaveChangesAsync(ctx);
        // await botInstance.SendChatMessageAsync("Removed image from database", true);
        await botInstance.SendChatMessageAsync(
            $"You removed the image from the {key} carousel: [img]{url}[/img]", true, autoDeleteAfter: TimeSpan.FromSeconds(10));
    }
}

public class ListImageCommand : ICommand
{
    public List<Regex> Patterns => [
        new Regex(@"^admin image (?<key>\w+) list$"),
        new Regex(@"^admin images (?<key>\w+) list$")
    ];
    public string? HelpText => "Remove an image from the image rotation specified";
    public UserRight RequiredRight => UserRight.TrueAndHonest;
    public TimeSpan Timeout => TimeSpan.FromSeconds(10);
    public RateLimitOptionsModel? RateLimitOptions => null;
    public bool WhisperCanInvoke => false;
    public async Task RunCommand(ChatBot botInstance, BotCommandMessageModel message, UserDbModel user, GroupCollection arguments,
        CancellationToken ctx)
    {
        await using var db = new ApplicationDbContext();
        var imageKeys = (await SettingsProvider.GetValueAsync(BuiltIn.Keys.BotImageAcceptableKeys))
            .JsonDeserialize<List<string>>();
        if (imageKeys == null) throw new InvalidOperationException($"{BuiltIn.Keys.BotImageAcceptableKeys} was null");
        var key = arguments["key"].Value;
        if (!imageKeys.Contains(key))
        {
            await botInstance.SendChatMessageAsync(
                $"Key you specified is not supported. Available keys are: {string.Join(' ', imageKeys)}", true, whisperTo: user.KfUsername);
            return;
        }

        var images = db.Images.Where(i => i.Key == key);
        if (await images.CountAsync(cancellationToken: ctx) > 20 && await Zipline.IsZiplineEnabled())
        {
            var content = string.Empty;
            foreach (var image in images)
            {
                content += image.Url + Environment.NewLine;
            }

            var paste = await Zipline.Upload(content, new MediaTypeHeaderValue("text/plain"), "1d", ctx);
            await botInstance.SendChatMessageAsync($"List of images for {key}: {paste}", true, whisperTo: user.KfUsername);
            return;
        }

        var i = 0;
        var result = $"List of images for {key}:";
        foreach (var image in images)
        {
            i++;
            var ts = DateTimeOffset.UtcNow - image.LastSeen;
            result += $"[br]{i}: {image.Url} (Last seen {ts.TotalDays:N0}d{ts.Hours:N0}h{ts.Minutes:N0}m{ts.Seconds:N0}s ago)";
        }

        await botInstance.SendChatMessagesAsync(result.FancySplitMessage(partSeparator: "[br]"),
            bypassSeshDetect: true, whisperTo: user.KfUsername);
    }
}

public class ManageImageKeyCommand : ICommand
{
    public List<Regex> Patterns => [
        new Regex(@"^admin imagekey add (?<key>\w+)$"),
        new Regex(@"^admin imagekey remove (?<key>\w+)$"),
        new Regex(@"^admin imagekey delete (?<key>\w+)$"),
        new Regex(@"^admin imageskey add (?<key>\w+)$"),
        new Regex(@"^admin imageskey remove (?<key>\w+)$"),
        new Regex(@"^admin imageskey delete (?<key>\w+)$")
    ];
    public string? HelpText => "Add or remove an acceptable image key from the BotImageAcceptableKeys setting";
    public UserRight RequiredRight => UserRight.Admin;
    public TimeSpan Timeout => TimeSpan.FromSeconds(10);
    public RateLimitOptionsModel? RateLimitOptions => null;
    public bool WhisperCanInvoke => false;
    public async Task RunCommand(ChatBot botInstance, BotCommandMessageModel message, UserDbModel user, GroupCollection arguments,
        CancellationToken ctx)
    {
        var imageKeys = (await SettingsProvider.GetValueAsync(BuiltIn.Keys.BotImageAcceptableKeys)).JsonDeserialize<List<string>>();
        if (imageKeys == null) throw new InvalidOperationException($"{BuiltIn.Keys.BotImageAcceptableKeys} was null");
        var key = arguments["key"].Value.ToLower();
        var isAdd = message.Message.Contains(" add ");

        if (isAdd)
        {
            if (imageKeys.Contains(key))
            {
                await botInstance.SendChatMessageAsync($"Key \"{key}\" is already in the acceptable keys list", true, whisperTo: user.KfUsername);
                return;
            }
            imageKeys.Add(key);
            await SettingsProvider.SetValueAsync(BuiltIn.Keys.BotImageAcceptableKeys, JsonSerializer.Serialize(imageKeys));
            await botInstance.SendChatMessageAsync(
                $"Added key \"{key}\" to acceptable image keys. Current keys: {string.Join(' ', imageKeys)}", true, whisperTo: user.KfUsername);
        }
        else
        {
            if (!imageKeys.Contains(key))
            {
                await botInstance.SendChatMessageAsync(
                    $"Key \"{key}\" is not in the acceptable keys list. Current keys: {string.Join(' ', imageKeys)}", true, whisperTo: user.KfUsername);
                return;
            }
            imageKeys.Remove(key);
            await SettingsProvider.SetValueAsync(BuiltIn.Keys.BotImageAcceptableKeys, JsonSerializer.Serialize(imageKeys));
            await botInstance.SendChatMessageAsync(
                $"Removed key \"{key}\" from acceptable image keys. Current keys: {string.Join(' ', imageKeys)}", true, whisperTo: user.KfUsername);
        }
    }
}

[AllowAdditionalMatches]
[DontDeleteInvocationMessage]
public class GetRandomImage : ICommand
{
    public List<Regex> Patterns => [
        new Regex(@"^(?<key>\w+)(?:\s+(?<search>.+))?")
    ];
    public string? HelpText => "Get a random image";
    public UserRight RequiredRight => UserRight.Loser;
    public TimeSpan Timeout => TimeSpan.FromMinutes(10);
    public RateLimitOptionsModel? RateLimitOptions => new()
    {
        Window = TimeSpan.FromSeconds(30),
        MaxInvocations = 4,
        Flags = RateLimitFlags.Global | RateLimitFlags.NoResponse
    };
    public bool WhisperCanInvoke => false;
    public async Task RunCommand(ChatBot botInstance, BotCommandMessageModel message, UserDbModel user, GroupCollection arguments,
        CancellationToken ctx)
    {
        await using var db = new ApplicationDbContext();
        var key = arguments["key"].Value.ToLower();
        var searchTerm = arguments["search"].Success ? arguments["search"].Value.Trim() : null;
        var images = db.Images.Where(i => i.Key == key);
        if (!await images.AnyAsync(ctx))
        {
            RateLimitService.RemoveMostRecentEntry(user, this);
            return;
        }

        if (key == "sloppa" && user.UserRight < UserRight.TrueAndHonest)
        {
            await botInstance.SendWhisperAsync(user.KfId, $"{user.FormatUsername()}, sloppa requires at least {UserRight.TrueAndHonest.Humanize()}");
            return;
        }
        var settings = await SettingsProvider.GetMultipleValuesAsync([
            BuiltIn.Keys.BotImageRandomSliceDivideBy, BuiltIn.Keys.BotImagePigCubeSelfDestruct,
            BuiltIn.Keys.BotImageInvertedCubeUrl, BuiltIn.Keys.BotImagePigCubeSelfDestructMin,
            BuiltIn.Keys.BotImagePigCubeSelfDestructMax, BuiltIn.Keys.BotImageInvertedPigCubeSelfDestructDelay,
            BuiltIn.Keys.BotImageChinkSelfDestruct, BuiltIn.Keys.BotImageChinkSelfDestructDelay
        ]);

        ImageDbModel image;
        if (!string.IsNullOrEmpty(searchTerm))
        {
            var allImages = await images.ToListAsync(ctx);
            var scored = allImages
                .Select(i => (Image: i, Score: Math.Max(
                    Fuzz.PartialRatio(searchTerm.ToLower(), i.Url.ToLower()),
                    i.Tags != null ? Fuzz.PartialRatio(searchTerm.ToLower(), i.Tags.ToLower()) : 0)))
                .OrderByDescending(x => x.Score)
                .ToList();
            if (scored.Count == 0 || scored[0].Score < 50)
            {
                RateLimitService.RemoveMostRecentEntry(user, this);
                await botInstance.SendChatMessageAsync($"No image in {key} matched \"{searchTerm}\"", true, whisperTo: user.KfUsername);
                return;
            }
            var bestScore = scored[0].Score;
            var candidates = scored.Where(x => x.Score == bestScore).Select(x => x.Image).ToList();
            image = candidates[new Random().Next(0, candidates.Count)];
        }
        else
        {
            var divideBy = settings[BuiltIn.Keys.BotImageRandomSliceDivideBy].ToType<int>();
            var limit = 1;
            var count = await images.CountAsync(ctx);
            if (count > divideBy)
            {
                limit = count / divideBy;
            }

            // EF with SQLite can't sort on dates as it's just TEXT
            var selection = (await images.ToListAsync(ctx)).OrderBy(i => i.LastSeen).Take(limit).ToList();
            // MaxValue is never returned by Next so you don't need to -1 for indexing
            image = selection[new Random().Next(0, selection.Count)];
        }
        image.LastSeen = DateTimeOffset.UtcNow;
        db.Images.Update(image);
        await db.SaveChangesAsync(ctx);
        TimeSpan? timeToDeletion = null;
        if (key == "pigcube" && settings[BuiltIn.Keys.BotImagePigCubeSelfDestruct].ToBoolean())
        {
            timeToDeletion = TimeSpan.FromMilliseconds(image.Url == settings[BuiltIn.Keys.BotImageInvertedCubeUrl].Value
                ? settings[BuiltIn.Keys.BotImageInvertedPigCubeSelfDestructDelay].ToType<int>()
                : new Random().Next(settings[BuiltIn.Keys.BotImagePigCubeSelfDestructMin].ToType<int>(),
                    settings[BuiltIn.Keys.BotImagePigCubeSelfDestructMax].ToType<int>()));
        }
        else if (key is "chink" or "sloppa" && settings[BuiltIn.Keys.BotImageChinkSelfDestruct].ToBoolean())
        {
            RateLimitService.AddEntry(user, this, message.MessageRawHtmlDecoded);
            timeToDeletion = TimeSpan.FromMilliseconds(settings[BuiltIn.Keys.BotImageChinkSelfDestructDelay].ToType<int>());
        }
        await botInstance.SendChatMessageAsync($"[img]{image.Url}[/img]", true, autoDeleteAfter: timeToDeletion);
    }
}