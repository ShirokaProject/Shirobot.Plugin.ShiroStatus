using System.Globalization;
using System.Reflection;
using ShiroBot.SDK.Plugin;

namespace ShiroStatus;

internal static class StatusReport
{
    internal static string Create(HostStatus status)
    {
        var runtime = status.Runtime;
        return string.Join('\n',
            "==== ShiroBot 状态 ====",
            $"版本: {FormatVersion(status.Version, Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion)}",
            $"环境: {runtime.OperatingSystem} {runtime.Architecture} && {runtime.Framework}",
            $"运行时长: {FormatUptime(status.Uptime)}",
            $"内存占用: {(runtime.MemoryBytes / 1024d / 1024d).ToString("F1", CultureInfo.InvariantCulture)} MB",
            $"接收消息: {status.MessageCount.ToString(CultureInfo.InvariantCulture)} 条");
    }

    private static string FormatVersion(string version, string? hostInformationalVersion)
    {
        // Match the host startup log: commit metadata follows '+'; show its first six characters.
        var parts = hostInformationalVersion?.Split('+', 2);
        var commit = parts is { Length: 2 } ? parts[1] : null;
        return string.IsNullOrWhiteSpace(commit)
            ? version
            : $"{version} ({commit[..Math.Min(6, commit.Length)]})";
    }

    private static string FormatUptime(TimeSpan uptime)
    {
        if (uptime < TimeSpan.Zero) uptime = TimeSpan.Zero;
        return $"{uptime.Days}天 {uptime.Hours}小时 {uptime.Minutes}分钟";
    }
}
