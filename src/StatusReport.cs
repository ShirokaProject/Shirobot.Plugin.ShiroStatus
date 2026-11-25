using System.Globalization;
using System.Reflection;
using ShiroBot.SDK.Models;
using ShiroBot.SDK.Plugin;

namespace ShiroStatus;

/// <summary>当前消息所在的适配器实例、机器人账号与已加载实例数。</summary>
internal sealed record AdapterReport(AdapterInstanceInfo? Instance, User? Self, int InstanceCount);

internal static class StatusReport
{
    internal static string Create(HostStatus status, AdapterReport? adapter = null)
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
        if (adapter is not null) lines.AddRange(FormatAdapter(adapter, status.PluginCount));
        return string.Join('\n', lines);
    }

    private static IEnumerable<string> FormatAdapter(AdapterReport adapter, int pluginCount)
    {
        yield return "==== 当前适配器 ====";
        if (adapter.Instance is { } instance)
        {
            var name = string.IsNullOrWhiteSpace(instance.AdapterName) ? instance.PackageId : instance.AdapterName;
            var version = string.IsNullOrWhiteSpace(instance.Version) ? string.Empty : " v" + instance.Version.TrimStart('v', 'V');
            var protocol = string.Join(" / ", new[] { instance.Platform, instance.Protocol }.Where(v => !string.IsNullOrWhiteSpace(v)));
            yield return $"适配器: {name}{version}" + (protocol.Length > 0 ? $" ({protocol})" : string.Empty);
            var instanceName = string.IsNullOrWhiteSpace(instance.Name) || instance.Name == instance.Id
                ? instance.Id : $"{instance.Name} ({instance.Id})";
            yield return $"实例: {instanceName}";
        }
        else
        {
            yield return "适配器: 未知";
        }

        if (adapter.Self is { } self)
            yield return string.IsNullOrWhiteSpace(self.Name) || self.Name == self.Id ? $"账号: {self.Id}" : $"账号: {self.Name} ({self.Id})";
        yield return $"实例总数: {adapter.InstanceCount.ToString(CultureInfo.InvariantCulture)} 个 · 插件: {pluginCount.ToString(CultureInfo.InvariantCulture)} 个";
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
