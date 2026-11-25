using System.Reflection;
using ShiroStatus;
using ShiroBot.SDK.Abstractions;
using ShiroBot.SDK.Models;
using ShiroBot.SDK.Plugin;

static void Check(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

foreach (var (uptime, expectedUptime) in new[]
{
    (new TimeSpan(12, 3, 4, 5), "12天 3小时 4分钟"),
    (TimeSpan.Zero, "0天 0小时 0分钟"),
    (TimeSpan.FromSeconds(-1), "0天 0小时 0分钟")
})
{
    var report = StatusReport.Create(new HostStatus { Uptime = uptime });
    Check(report.Contains($"运行时长: {expectedUptime}\n"), "Report must handle multi-day, zero and negative uptime.");
}

// Exercise optional host commit metadata without exposing formatting helpers in the plugin API.
var formatVersion = typeof(StatusReport).GetMethod("FormatVersion", BindingFlags.Static | BindingFlags.NonPublic)!
    .CreateDelegate<Func<string, string?, string>>();
Check(formatVersion("1.0.2", "1.0.2+abcdef123456") == "1.0.2 (abcdef)", "Commit must use the host build's short hash.");
Check(formatVersion("1.0.2", null) == "1.0.2", "Missing commit must not produce an empty suffix.");
Check(formatVersion("1.0.2", "1.0.2+") == "1.0.2", "Empty commit must be omitted.");

HostStatus? status = new()
{
    Version = "1.0.2",
    Uptime = new TimeSpan(19, 20, 25, 42),
    MessageCount = 256,
    Runtime = new HostRuntimeInfo
    {
        OperatingSystem = "macOS 27.0.1", Architecture = "arm64", Framework = ".NET 10.0.13",
        MemoryBytes = 134742016
    }
};
var replies = new List<string>();
var messages = DispatchProxy.Create<IMessageContext, TestProxy>();
((TestProxy)(object)messages).Call = (method, arguments) =>
{
    Check(method.Name == "ReplyAsync", "Status must use the SDK text reply API.");
    var segments = arguments!.OfType<IEnumerable<MessageSegment>>().Single().ToArray();
    Check(segments.Length == 0, "Text reply must have no additional rich segments.");
    replies.Add(arguments!.OfType<string>().Single());
    return Task.FromResult(new SentMessage("reply"));
};
var users = DispatchProxy.Create<ShiroBot.SDK.Adapter.IUserService, TestProxy>();
((TestProxy)(object)users).Call = (method, _) => method.Name == "GetSelfAsync"
    ? Task.FromResult(new User("10001") { Name = "Shiro", AvatarUrl = "file:///nonexistent/avatar.png" })
    : throw new NotSupportedException(method.Name);
var adapterInstance = new AdapterInstanceInfo("qq-main", "主号", "ShiroBot.Adapter.OneBot", "OneBot", "1.2.3", "qq", "onebot-v11");
var context = DispatchProxy.Create<IBotContext, TestProxy>();
((TestProxy)(object)context).Call = (method, _) => method.Name switch
{
    "get_Message" => messages,
    "GetHostStatus" => status,
    "get_AdapterInstance" => adapterInstance,
    "get_User" => users,
    "GetAdapterInstances" => new[] { adapterInstance, adapterInstance with { Id = "qq-alt" }, new AdapterInstanceInfo("tg", "TG", "ShiroBot.TelegramAdapter", "Telegram", "0.3.0", "telegram", "bot-api") },
    _ => throw new NotSupportedException(method.Name)
};
var plugin = new Plugin();
await plugin.OnLoad(context);
var flags = BindingFlags.Instance | BindingFlags.NonPublic;
foreach (var name in new[] { "GroupCommands", "DirectCommands" })
{
    var router = (CommandRouter<MessageEvent>)typeof(PluginBase).GetProperty(name, flags)!.GetValue(plugin)!;
    var message = new MessageEvent
    {
        Platform = "qq-official", SelfId = "bot", MessageId = "1", Sender = new User("user"),
        Channel = name == "GroupCommands" ? Channel.Group("group") : Channel.Direct("user"),
        Segments = [new TextSegment("#Shiro状态")]
    };
    Check(await router.DispatchAsync("#Shiro状态", message), name + " must handle #Shiro状态.");
    Check(!await router.DispatchAsync("#Shiro状态 extra", message), "Extra arguments must not match the exact command.");
    Check(!await router.DispatchAsync("hello #Shiro状态", message), "Ordinary messages must not match.");
    Check(await router.DispatchAsync("#shiro", message), "The original #shiro command must keep working.");
}
Check(replies.Count == 4, "Each matching message must get exactly one reply.");
const string expected = "==== ShiroBot 状态 ====\n版本: 1.0.2 (abcdef)\n环境: macOS 27.0.1 arm64 && .NET 10.0.13\n运行时长: 19天 20小时 25分钟\n内存占用: 128.5 MB\n接收消息: 256 条" +
    "\n适配器: OneBot v1.2.3 ×2 · Telegram v0.3.0";
Check(replies.All(r => r == expected), "All fields must come from the host snapshot.");
var directRouter = (CommandRouter<MessageEvent>)typeof(PluginBase).GetProperty("DirectCommands", flags)!.GetValue(plugin)!;
var directMessage = new MessageEvent
{
    Platform = "qq-official", SelfId = "bot", MessageId = "2", Sender = new User("user"),
    Channel = Channel.Direct("user"), Segments = [new TextSegment("#shiro状态")]
};
status = status! with { Uptime = TimeSpan.FromMinutes(9), MessageCount = 257, Runtime = status.Runtime with { MemoryBytes = 0 } };
await directRouter.DispatchAsync("#shiro状态", directMessage);
Check(replies[^1].Contains("运行时长: 0天 0小时 9分钟\n内存占用: 0.0 MB\n接收消息: 257 条"), "Each command must read a fresh host snapshot, matching Dashboard uptime.");
status = null;
await directRouter.DispatchAsync("#shiro状态", directMessage);
Check(replies.Count == 5, "Old hosts with no status support must not emit guessed status.");
Check(await directRouter.DispatchAsync("#moshiro", directMessage), "#moshiro must be routed.");
Check(replies.Count == 6 && replies[^1] == "机器状态卡片生成失败，请稍后重试。", "Render failures must fall back to a short text reply.");
Check(ShiroStatus.Cards.AvatarAccent.ResolveAvatarUrl(null, "qq", "10001") == "https://q1.qlogo.cn/g?b=qq&nk=10001&s=100",
    "Personal QQ accounts fall back to the qlogo avatar by QQ number.");
Check(ShiroStatus.Cards.AvatarAccent.ResolveAvatarUrl(null, "qq-official", "5756337207113435206") is null,
    "QQ official bot ids are not QQ numbers and must not be turned into qlogo URLs.");
Check(ShiroStatus.Cards.AvatarAccent.ResolveAvatarUrl(null, "qq", "55C88F86C7E7BF9F9F53615CDAAA493F") is null,
    "Openids are not QQ numbers.");
Check(ShiroStatus.Cards.AvatarAccent.ResolveAvatarUrl("https://example.org/a.png", "qq-official", "x") == "https://example.org/a.png",
    "An adapter-provided avatar always wins.");
await plugin.OnUnload();
Console.WriteLine("PASS: #shiro/#Shiro状态/#moshiro routes, avatar fallback, adapter list line, text replies, host snapshots, fresh uptime, old-host fallback, commit metadata, memory, message counts and uptime boundaries.");
Console.WriteLine(replies[2]);

public class TestProxy : DispatchProxy
{
    public Func<MethodInfo, object?[]?, object?> Call { get; set; } = null!;
    protected override object? Invoke(MethodInfo? method, object?[]? args) => Call(method!, args);
}
