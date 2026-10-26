using ShiroBot.SDK.Core;
using ShiroBot.SDK.Models;
using ShiroBot.SDK.Plugin;

[assembly: ShiroBotApiCompatibility("1.0", "1.0")]

namespace ShiroStatus;

[BotPlugin("ShiroStatus",
    Name = "ShiroStatus",
    Version = "0.1.0",
    Author = "ShirokaProject",
    GithubRepo = "ShirokaProject/Shirobot.Plugin.ShiroStatus",
    Description = "查询Shiro的运行状态",
    Category = PluginCategory.Utility,
    IsPluginSingleFile = true)]
public sealed class Plugin : PluginBase
{
    protected override void ConfigureRoutes()
    {
        GroupCommands.MapExact("#shiro", HandleStatusAsync);
        DirectCommands.MapExact("#shiro", HandleStatusAsync);
    }

    private Task HandleStatusAsync(MessageEvent message)
    {
        if (Context.GetHostStatus() is not { } status) return Task.CompletedTask;
        return Context.Message.ReplyAsync(message, StatusReport.Create(status));
    }
}
