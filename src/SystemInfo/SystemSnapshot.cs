using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.RegularExpressions;

namespace ShiroStatus.SystemInfo;

internal sealed record DiskUsage(string Name, string MountPoint, long TotalBytes, long FreeBytes)
{
    public long UsedBytes => Math.Max(0, TotalBytes - FreeBytes);
    public double UsedRatio => TotalBytes > 0 ? Math.Clamp((double)UsedBytes / TotalBytes, 0, 1) : 0;
}

internal sealed record SystemSnapshot
{
    public string HostName { get; init; } = Environment.MachineName;
    public string CpuModel { get; init; } = string.Empty;
    public int LogicalCores { get; init; } = Environment.ProcessorCount;
    /// <summary>0–1，采样失败为 null。</summary>
    public double? CpuUsage { get; init; }
    /// <summary>1 / 5 / 15 分钟负载，Windows 为 null。</summary>
    public double[]? LoadAverage { get; init; }
    public long MemoryTotalBytes { get; init; }
    public long MemoryUsedBytes { get; init; }
    public double MemoryUsedRatio => MemoryTotalBytes > 0 ? Math.Clamp((double)MemoryUsedBytes / MemoryTotalBytes, 0, 1) : 0;
    public IReadOnlyList<DiskUsage> Disks { get; init; } = [];
    public TimeSpan SystemUptime { get; init; }
    public string KernelDescription { get; init; } = RuntimeInformation.OSDescription;
}

/// <summary>
/// 采集宿主机器信息。全部使用系统 API 或只读系统文件，单项失败只留空，不影响整体卡片。
/// </summary>
internal static partial class SystemSnapshotCollector
{
    private static readonly TimeSpan CpuSampleInterval = TimeSpan.FromMilliseconds(400);

    public static async Task<SystemSnapshot> CollectAsync(CancellationToken cancellationToken = default)
    {
        var cpuTask = SafeAsync(() => SampleCpuUsageAsync(cancellationToken));
        var (total, used) = Safe(ReadMemory, (0L, 0L));
        var snapshot = new SystemSnapshot
        {
            CpuModel = Safe(ReadCpuModel, string.Empty),
            LoadAverage = Safe(ReadLoadAverage, null),
            MemoryTotalBytes = total,
            MemoryUsedBytes = used,
            Disks = Safe(ReadDisks, []),
            SystemUptime = TimeSpan.FromMilliseconds(Environment.TickCount64),
            KernelDescription = Safe(ReadKernel, RuntimeInformation.OSDescription),
        };
        return snapshot with { CpuUsage = await cpuTask.ConfigureAwait(false) };
    }

    // ---------------- CPU ----------------

    private static async Task<double?> SampleCpuUsageAsync(CancellationToken cancellationToken)
    {
        if (ReadCpuTicks() is not { } first) return null;
        await Task.Delay(CpuSampleInterval, cancellationToken).ConfigureAwait(false);
        if (ReadCpuTicks() is not { } second) return null;
        var busy = second.Busy - first.Busy;
        var total = busy + (second.Idle - first.Idle);
        return total > 0 ? Math.Clamp((double)busy / total, 0, 1) : null;
    }

    private static (ulong Busy, ulong Idle)? ReadCpuTicks()
    {
        if (OperatingSystem.IsLinux())
        {
            // cpu  user nice system idle iowait irq softirq steal ...
            var line = File.ReadLines("/proc/stat").FirstOrDefault(l => l.StartsWith("cpu ", StringComparison.Ordinal));
            if (line is null) return null;
            var values = line.Split(' ', StringSplitOptions.RemoveEmptyEntries).Skip(1)
                .Select(v => ulong.Parse(v, CultureInfo.InvariantCulture)).ToArray();
            if (values.Length < 4) return null;
            var idle = values[3] + (values.Length > 4 ? values[4] : 0);
            var busy = values.Take(Math.Min(values.Length, 8)).Aggregate(0UL, (a, b) => a + b) - idle;
            return (busy, idle);
        }

        if (OperatingSystem.IsMacOS())
        {
            var info = new uint[4]; // user, system, idle, nice
            var count = 4u;
            if (Mac.host_statistics(Mac.mach_host_self(), Mac.HostCpuLoadInfo, info, ref count) != 0) return null;
            return ((ulong)info[0] + info[1] + info[3], info[2]);
        }

        if (OperatingSystem.IsWindows())
        {
            if (!Win.GetSystemTimes(out var idle, out var kernel, out var user)) return null;
            // kernel 时间包含 idle
            return (kernel + user - idle, idle);
        }

        return null;
    }

    private static string ReadCpuModel()
    {
        if (OperatingSystem.IsMacOS()) return Mac.SysctlString("machdep.cpu.brand_string") ?? string.Empty;
        if (OperatingSystem.IsWindows()) return Win.ReadCpuName();
        if (OperatingSystem.IsLinux())
        {
            foreach (var line in File.ReadLines("/proc/cpuinfo"))
            {
                var separator = line.IndexOf(':');
                if (separator <= 0) continue;
                var key = line[..separator].Trim();
                if (key is "model name" or "Model" or "Hardware" or "cpu model")
                    return line[(separator + 1)..].Trim();
            }
        }

        return string.Empty;
    }

    /// <summary>macOS 上 OSDescription 只给出系统版本，这里改用 Darwin 内核版本。</summary>
    private static string ReadKernel() =>
        OperatingSystem.IsMacOS() && Mac.SysctlString("kern.osrelease") is { Length: > 0 } release
            ? "Darwin " + release
            : RuntimeInformation.OSDescription;

    private static double[]? ReadLoadAverage()
    {
        var values = new double[3];
        var read = OperatingSystem.IsMacOS() ? Mac.getloadavg(values, 3)
            : OperatingSystem.IsLinux() ? Linux.getloadavg(values, 3)
            : 0;
        return read == 3 ? values : null;
    }

    // ---------------- 内存 ----------------

    private static (long Total, long Used) ReadMemory()
    {
        if (OperatingSystem.IsLinux())
        {
            var info = File.ReadLines("/proc/meminfo")
                .Select(line => line.Split(':', 2))
                .Where(parts => parts.Length == 2)
                .ToDictionary(parts => parts[0].Trim(), parts => long.TryParse(parts[1].Replace("kB", "").Trim(), out var kb) ? kb * 1024 : 0);
            var total = info.GetValueOrDefault("MemTotal");
            var available = info.TryGetValue("MemAvailable", out var a) ? a : info.GetValueOrDefault("MemFree");
            return (total, Math.Max(0, total - available));
        }

        if (OperatingSystem.IsWindows()) return Win.ReadMemory();

        if (OperatingSystem.IsMacOS())
        {
            var total = Mac.SysctlInt64("hw.memsize") ?? GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
            return (total, Math.Min(total, ReadMacUsedMemory()));
        }

        return (GC.GetGCMemoryInfo().TotalAvailableMemoryBytes, 0);
    }

    /// <summary>与“活动监视器”的“已使用内存”一致：App 内存 + 联动 + 被压缩。</summary>
    private static long ReadMacUsedMemory()
    {
        var output = RunProcess("/usr/bin/vm_stat");
        var pageSize = PageSizeRegex().Match(output) is { Success: true } m ? long.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) : 4096;
        long Pages(string label) => Regex.Match(output, "^" + Regex.Escape(label) + @":\s+(\d+)", RegexOptions.Multiline) is { Success: true } match
            ? long.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture) : 0;
        var app = Pages("Anonymous pages") - Pages("Pages purgeable");
        if (app <= 0) app = Pages("Pages active");
        return (app + Pages("Pages wired down") + Pages("Pages occupied by compressor")) * pageSize;
    }

    // ---------------- 磁盘 ----------------

    private static readonly string[] IgnoredFormats =
        ["tmpfs", "devtmpfs", "overlay", "squashfs", "proc", "sysfs", "devfs", "autofs", "nullfs", "fuse.snapfuse", "efivarfs", "ramfs"];

    private static readonly string[] IgnoredLinuxPrefixes = ["/boot", "/snap", "/run", "/dev", "/sys", "/proc", "/var/lib/docker", "/etc"];

    private static IReadOnlyList<DiskUsage> ReadDisks()
    {
        var disks = new List<DiskUsage>();
        var seen = new HashSet<(long, long)>();
        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                if (!drive.IsReady) continue;
                var mount = drive.RootDirectory.FullName;
                if (!IsUserVisibleDrive(drive, mount)) continue;
                var total = drive.TotalSize;
                var free = drive.AvailableFreeSpace;
                if (total < 1L << 30 || !seen.Add((total, free))) continue;
                disks.Add(new DiskUsage(DiskName(drive, mount), mount, total, free));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }

        return disks.OrderBy(d => d.MountPoint is "/" or "C:\\" ? 0 : 1).ThenBy(d => d.MountPoint, StringComparer.Ordinal).Take(4).ToArray();
    }

    private static bool IsUserVisibleDrive(DriveInfo drive, string mount)
    {
        if (OperatingSystem.IsWindows()) return drive.DriveType == DriveType.Fixed;
        if (IgnoredFormats.Contains(drive.DriveFormat, StringComparer.OrdinalIgnoreCase)) return false;
        if (OperatingSystem.IsMacOS()) return mount == "/" || (mount.StartsWith("/Volumes/", StringComparison.Ordinal) && drive.DriveType != DriveType.Network);
        return mount == "/" || !IgnoredLinuxPrefixes.Any(prefix => mount == prefix || mount.StartsWith(prefix + "/", StringComparison.Ordinal));
    }

    private static string DiskName(DriveInfo drive, string mount)
    {
        if (OperatingSystem.IsWindows())
        {
            var letter = mount.TrimEnd('\\');
            return string.IsNullOrWhiteSpace(drive.VolumeLabel) ? letter : $"{drive.VolumeLabel} ({letter})";
        }

        if (OperatingSystem.IsMacOS() && mount == "/") return "系统盘";
        return mount == "/" ? "根目录 /" : mount.StartsWith("/Volumes/", StringComparison.Ordinal) ? mount["/Volumes/".Length..] : mount;
    }

    // ---------------- 工具 ----------------

    private static string RunProcess(string fileName, params string[] arguments)
    {
        using var process = Process.Start(new ProcessStartInfo(fileName, arguments)
        {
            RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true,
        }) ?? throw new InvalidOperationException(fileName + " 无法启动");
        var output = process.StandardOutput.ReadToEnd();
        if (!process.WaitForExit(3000)) process.Kill();
        return output;
    }

    private static T Safe<T>(Func<T> read, T fallback)
    {
        try { return read(); }
        catch (Exception) { return fallback; }
    }

    private static async Task<T?> SafeAsync<T>(Func<Task<T?>> read) where T : struct
    {
        try { return await read().ConfigureAwait(false); }
        catch (OperationCanceledException) { throw; }
        catch (Exception) { return null; }
    }

    [GeneratedRegex(@"page size of (\d+) bytes")]
    private static partial Regex PageSizeRegex();

    [SupportedOSPlatform("macos")]
    private static class Mac
    {
        public const int HostCpuLoadInfo = 3;

        [DllImport("libSystem.dylib")] public static extern uint mach_host_self();
        [DllImport("libSystem.dylib")] public static extern int host_statistics(uint host, int flavor, uint[] info, ref uint count);
        [DllImport("libSystem.dylib")] public static extern int getloadavg(double[] loads, int count);
        [DllImport("libSystem.dylib", CharSet = CharSet.Ansi)]
        private static extern int sysctlbyname(string name, byte[]? oldp, ref nint oldlenp, nint newp, nint newlen);

        public static string? SysctlString(string name)
        {
            nint length = 0;
            if (sysctlbyname(name, null, ref length, 0, 0) != 0 || length <= 0) return null;
            var buffer = new byte[length];
            if (sysctlbyname(name, buffer, ref length, 0, 0) != 0) return null;
            return System.Text.Encoding.UTF8.GetString(buffer, 0, (int)length).TrimEnd('\0').Trim();
        }

        public static long? SysctlInt64(string name)
        {
            nint length = 8;
            var buffer = new byte[8];
            return sysctlbyname(name, buffer, ref length, 0, 0) == 0 && length == 8 ? BitConverter.ToInt64(buffer) : null;
        }
    }

    [SupportedOSPlatform("linux")]
    private static class Linux
    {
        [DllImport("libc")] public static extern int getloadavg(double[] loads, int count);
    }

    [SupportedOSPlatform("windows")]
    private static class Win
    {
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetSystemTimes(out ulong idle, out ulong kernel, out ulong user);

        [StructLayout(LayoutKind.Sequential)]
        private struct MemoryStatusEx
        {
            public uint Length;
            public uint MemoryLoad;
            public ulong TotalPhys;
            public ulong AvailPhys;
            public ulong TotalPageFile;
            public ulong AvailPageFile;
            public ulong TotalVirtual;
            public ulong AvailVirtual;
            public ulong AvailExtendedVirtual;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);

        public static (long Total, long Used) ReadMemory()
        {
            var status = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
            if (!GlobalMemoryStatusEx(ref status)) return (0, 0);
            return ((long)status.TotalPhys, (long)(status.TotalPhys - status.AvailPhys));
        }

        public static string ReadCpuName()
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
            return (key?.GetValue("ProcessorNameString") as string)?.Trim() ?? string.Empty;
        }
    }
}
