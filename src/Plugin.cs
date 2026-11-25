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
    Version = "0.2.0",
    Author = "ShirokaProject",
    GithubRepo = "ShirokaProject/Shirobot.Plugin.ShiroStatus",
    Description = "查询Shiro的运行状态与宿主机器信息",
    Category = PluginCategory.Utility,
    IsPluginSingleFile = true)]
public sealed class Plugin : PluginBase
{
    internal static readonly string[] StatusCommands = ["#Shiro状态", "#shiro状态"];
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
        var adapter = new AdapterReport(Context.AdapterInstance, await TryGetSelfAsync().ConfigureAwait(false), SafeInstanceCount());
        await Context.Message.ReplyAsync(message, StatusReport.Create(status, adapter)).ConfigureAwait(false);
    }

    private async Task HandleMachineAsync(MessageEvent message)
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var machine = await SystemSnapshotCollector.CollectAsync(timeout.Token).ConfigureAwait(false);
            var model = MachineCardViewModel.Create(machine, Context.GetHostStatus(), DateTimeOffset.Now);
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

    private int SafeInstanceCount()
    {
        try { return Context.GetAdapterInstances()?.Count ?? 0; }
        catch (Exception) { return 0; }
    }
}
