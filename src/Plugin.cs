using ShiroBot.AvaloniaSdk;
using ShiroBot.SDK.Abstractions;
using ShiroBot.SDK.Core;
using ShiroBot.SDK.Models;
using ShiroBot.SDK.Plugin;
using ShiroStatus.Cards;
using ShiroStatus.SystemInfo;

[assembly: ShiroBotApiCompatibility("1.0", "1.0")]

namespace ShiroStatus;

[BotPlugin("ShiroStatus",
    Name = "ShiroStatus",
    Version = "0.2.2",
    Author = "ShirokaProject",
    GithubRepo = "ShirokaProject/Shirobot.Plugin.ShiroStatus",
    Description = "查询Shiro的运行状态与宿主机器信息",
    Category = PluginCategory.Utility,
    IsPluginSingleFile = true)]
public sealed class Plugin : PluginBase
{
    internal static readonly string[] StatusCommands = ["#shiro", "#Shiro", "#Shiro状态", "#shiro状态"];
    internal static readonly string[] MachineCommands = ["#moshiro", "#Moshiro"];

    protected override void ConfigureRoutes()
    {
        foreach (var command in StatusCommands)
        {
            GroupCommands.MapExact(command, HandleStatusAsync);
            DirectCommands.MapExact(command, HandleStatusAsync);
        }

        foreach (var command in MachineCommands)
        {
            GroupCommands.MapExact(command, HandleMachineAsync);
            DirectCommands.MapExact(command, HandleMachineAsync);
        }
    }

    private async Task HandleStatusAsync(MessageEvent message)
    {
        if (Context.GetHostStatus() is not { } status) return;
        await Context.Message.ReplyAsync(message, StatusReport.Create(status, SafeInstances())).ConfigureAwait(false);
    }

    private async Task HandleMachineAsync(MessageEvent message)
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var machine = await SystemSnapshotCollector.CollectAsync(timeout.Token).ConfigureAwait(false);
            var self = await TryGetSelfAsync().ConfigureAwait(false);
            var adapter = new AdapterReport(Context.AdapterInstance, self, SafeInstances());
            var avatarTask = AvatarAccent.DownloadAsync(
                AvatarAccent.ResolveAvatarUrl(message.Sender.AvatarUrl, Context.AdapterInstance?.Platform, message.Sender.Id), timeout.Token);
            var theme = await GetThemeColorAsync(self, timeout.Token).ConfigureAwait(false);
            var model = MachineCardViewModel.Create(machine, Context.GetHostStatus(), DateTimeOffset.Now, adapter, theme,
                await avatarTask.ConfigureAwait(false));
            var png = await Context.RenderControlPngAsync<MachineCard>(model, new ControlRenderOptions(RenderTheme.Auto), timeout.Token)
                .ConfigureAwait(false);
            await Context.Message.ReplyAsync(message, new ImageSegment("base64://" + Convert.ToBase64String(png))).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            BotLog.Warning($"ShiroStatus 机器状态卡片生成失败: {ex.GetType().Name}: {ex.Message}");
            await Context.Message.ReplyAsync(message, "机器状态卡片生成失败，请稍后重试。").ConfigureAwait(false);
        }
    }

    /// <summary>主题色取自机器人头像，每个账号只取一次并写入插件目录；失败时用默认色。</summary>
    private async Task<Avalonia.Media.Color?> GetThemeColorAsync(User? self, CancellationToken cancellationToken)
    {
        if (self is null) return null;
        var platform = Context.AdapterInstance?.Platform;
        var avatar = AvatarAccent.ResolveAvatarUrl(self.AvatarUrl, platform, self.Id);
        string? directory = null;
        try { directory = Context.PluginDirectory; }
        catch (Exception) { }
        return await AvatarAccent.GetAsync($"{platform}:{self.Id}", avatar, directory, cancellationToken).ConfigureAwait(false);
    }

    private async Task<User?> TryGetSelfAsync()
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            return await Context.User.GetSelfAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private IReadOnlyList<AdapterInstanceInfo> SafeInstances()
    {
        try { return Context.GetAdapterInstances() ?? []; }
        catch (Exception) { return []; }
    }
}
