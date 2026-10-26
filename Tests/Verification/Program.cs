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
var context = DispatchProxy.Create<IBotContext, TestProxy>();
((TestProxy)(object)context).Call = (method, _) => method.Name switch
{
    "get_Message" => messages,
    "GetHostStatus" => status,
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
        Segments = [new TextSegment("#shiro")]
    };
    Check(await router.DispatchAsync("#shiro", message), name + " must handle #shiro.");
    Check(!await router.DispatchAsync("#shiro extra", message), "Extra arguments must not match the exact command.");
    Check(!await router.DispatchAsync("hello #shiro", message), "Ordinary messages must not match.");
}
Check(replies.Count == 2, "Each matching message must get exactly one reply.");
const string expected = "==== ShiroBot 状态 ====\n版本: 1.0.2 (abcdef)\n环境: macOS 27.0.1 arm64 && .NET 10.0.13\n运行时长: 19天 20小时 25分钟\n内存占用: 128.5 MB\n接收消息: 256 条";
Check(replies.All(r => r == expected), "All fields must come from the host snapshot.");
var directRouter = (CommandRouter<MessageEvent>)typeof(PluginBase).GetProperty("DirectCommands", flags)!.GetValue(plugin)!;
var directMessage = new MessageEvent
{
    Platform = "qq-official", SelfId = "bot", MessageId = "2", Sender = new User("user"),
    Channel = Channel.Direct("user"), Segments = [new TextSegment("#shiro")]
};
status = status! with { Uptime = TimeSpan.FromMinutes(9), MessageCount = 257, Runtime = status.Runtime with { MemoryBytes = 0 } };
await directRouter.DispatchAsync("#shiro", directMessage);
Check(replies[^1].Contains("运行时长: 0天 0小时 9分钟\n内存占用: 0.0 MB\n接收消息: 257 条"), "Each command must read a fresh host snapshot, matching Dashboard uptime.");
status = null;
await directRouter.DispatchAsync("#shiro", directMessage);
Check(replies.Count == 3, "Old hosts with no status support must not emit guessed status.");
await plugin.OnUnload();
Console.WriteLine("PASS: group/direct exact routes, text replies, host snapshots, fresh uptime, old-host fallback, commit metadata, memory, message counts and uptime boundaries.");
Console.WriteLine(replies[2]);

public class TestProxy : DispatchProxy
{
    public Func<MethodInfo, object?[]?, object?> Call { get; set; } = null!;
    protected override object? Invoke(MethodInfo? method, object?[]? args) => Call(method!, args);
}
