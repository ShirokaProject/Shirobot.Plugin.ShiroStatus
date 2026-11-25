using System.Globalization;
using System.Reflection;
using ShiroBot.SDK.Models;
using ShiroBot.SDK.Plugin;

namespace ShiroStatus;

/// <summary>当前消息所在的适配器实例、机器人账号与全部已加载实例，供 #moshiro 卡片使用。</summary>
internal sealed record AdapterReport(AdapterInstanceInfo? Instance, User? Self, IReadOnlyList<AdapterInstanceInfo> Instances);

internal static class StatusReport
{
    internal static string Create(HostStatus status, IReadOnlyList<AdapterInstanceInfo>? adapters = null)
    {
        var runtime = status.Runtime;
        var lines = new List<string>
        {
            "==== ShiroBot 状态 ====",
            $"版本: {FormatVersion(status.Version, Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion)}",
            $"环境: {runtime.OperatingSystem} {runtime.Architecture} && {runtime.Framework}",
            $"运行时长: {FormatUptime(status.Uptime)}",
            $"内存占用: {(runtime.MemoryBytes / 1024d / 1024d).ToString("F1", CultureInfo.InvariantCulture)} MB",
            $"接收消息: {status.MessageCount.ToString(CultureInfo.InvariantCulture)} 条",
        };
        if (adapters is { Count: > 0 }) lines.Add("适配器: " + FormatAdapterList(adapters));
        return string.Join('\n', lines);
    }

    /// <summary>"OneBot v1.2.3 ×2 · Telegram v0.3.0"：同一适配器的多个实例合并计数。</summary>
    internal static string FormatAdapterList(IReadOnlyList<AdapterInstanceInfo> adapters) =>
        string.Join(" · ", adapters
            .GroupBy(AdapterTitle, StringComparer.Ordinal)
            .Select(group => group.Count() > 1 ? $"{group.Key} ×{group.Count()}" : group.Key));

    internal static string AdapterTitle(AdapterInstanceInfo instance)
    {
        var name = string.IsNullOrWhiteSpace(instance.AdapterName) ? instance.PackageId : instance.AdapterName;
        return string.IsNullOrWhiteSpace(instance.Version) ? name : $"{name} v{instance.Version.TrimStart('v', 'V')}";
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
