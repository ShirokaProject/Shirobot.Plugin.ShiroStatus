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
    /// <summary>当前进程数，读不到为 0。</summary>
    public int ProcessCount { get; init; }
    public long MemoryTotalBytes { get; init; }
    public long MemoryUsedBytes { get; init; }
    public double MemoryUsedRatio => MemoryTotalBytes > 0 ? Math.Clamp((double)MemoryUsedBytes / MemoryTotalBytes, 0, 1) : 0;
    public long SwapTotalBytes { get; init; }
    public long SwapUsedBytes { get; init; }
    public double SwapUsedRatio => SwapTotalBytes > 0 ? Math.Clamp((double)SwapUsedBytes / SwapTotalBytes, 0, 1) : 0;
    /// <summary>显卡型号，多块用 " / " 连接；读不到为空。</summary>
    public string Gpu { get; init; } = string.Empty;
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
        var (swapTotal, swapUsed) = Safe(ReadSwap, (0L, 0L));
        var snapshot = new SystemSnapshot
        {
            CpuModel = Safe(ReadCpuModel, string.Empty),
            ProcessCount = Safe(ReadProcessCount, 0),
            MemoryTotalBytes = total,
            MemoryUsedBytes = used,
            SwapTotalBytes = swapTotal,
            SwapUsedBytes = swapUsed,
            Gpu = GpuName.Value,
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
            string? implementer = null, part = null;
            foreach (var line in File.ReadLines("/proc/cpuinfo"))
            {
                var separator = line.IndexOf(':');
                if (separator <= 0) continue;
                var key = line[..separator].Trim();
                var value = line[(separator + 1)..].Trim();
                if (key is "model name" or "Model" or "Hardware" or "cpu model" && value.Length > 0) return value;
                if (key == "CPU implementer") implementer ??= value;
                else if (key == "CPU part") part ??= value;
            }

            // ARM 的 /proc/cpuinfo 没有型号：先看设备树（树莓派等开发板），再按厂商 / 核心编码推断
            const string deviceTree = "/sys/firmware/devicetree/base/model";
            if (File.Exists(deviceTree) && File.ReadAllText(deviceTree).Trim('\0', ' ', '\n') is { Length: > 0 } board) return board;
            if (implementer is not null) return ArmCpuName(implementer, part);
        }

        return string.Empty;
    }

    private static string ArmCpuName(string implementer, string? part)
    {
        var vendor = implementer.ToLowerInvariant() switch
        {
            "0x41" => "ARM", "0x42" => "Broadcom", "0x43" => "Cavium", "0x46" => "Fujitsu", "0x48" => "HiSilicon",
            "0x4e" => "NVIDIA", "0x50" => "Ampere", "0x51" => "Qualcomm", "0x61" => "Apple", "0x6d" => "Microsoft",
            "0xc0" => "Ampere", _ => "ARM",
        };
        var core = implementer.Equals("0x41", StringComparison.OrdinalIgnoreCase) ? part?.ToLowerInvariant() switch
        {
            "0xd03" => "Cortex-A53", "0xd04" => "Cortex-A35", "0xd05" => "Cortex-A55", "0xd07" => "Cortex-A57",
            "0xd08" => "Cortex-A72", "0xd09" => "Cortex-A73", "0xd0a" => "Cortex-A75", "0xd0b" => "Cortex-A76",
            "0xd0c" => "Neoverse N1", "0xd0d" => "Cortex-A77", "0xd41" => "Cortex-A78", "0xd40" => "Neoverse V1",
            "0xd49" => "Neoverse N2", "0xd4f" => "Neoverse V2", _ => null,
        } : null;
        return core is null ? $"{vendor} ARM64" : $"{vendor} {core}";
    }

    /// <summary>
    /// macOS 上 OSDescription 只给出系统版本，改用 Darwin 内核版本；
    /// Linux 上 OSDescription 是发行版名称（"Ubuntu 24.04.5"），内核版本从 /proc/sys/kernel/osrelease 读。
    /// </summary>
    private static string ReadKernel()
    {
        if (OperatingSystem.IsMacOS() && Mac.SysctlString("kern.osrelease") is { Length: > 0 } release) return "Darwin " + release;
        if (OperatingSystem.IsLinux() && File.Exists("/proc/sys/kernel/osrelease"))
            return "Linux " + File.ReadAllText("/proc/sys/kernel/osrelease").Trim();
        return RuntimeInformation.OSDescription.Trim();
    }

    private static int ReadProcessCount()
    {
        var processes = Process.GetProcesses();
        foreach (var process in processes) process.Dispose();
        return processes.Length;
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

    // ---------------- Swap ----------------

    private static (long Total, long Used) ReadSwap()
    {
        if (OperatingSystem.IsLinux())
        {
            long total = 0, free = 0;
            foreach (var line in File.ReadLines("/proc/meminfo"))
            {
                var parts = line.Split(':', 2);
                if (parts.Length != 2 || !long.TryParse(parts[1].Replace("kB", "").Trim(), out var kb)) continue;
                if (parts[0] == "SwapTotal") total = kb * 1024;
                else if (parts[0] == "SwapFree") free = kb * 1024;
            }

            return (total, Math.Max(0, total - free));
        }

        if (OperatingSystem.IsMacOS()) return Mac.ReadSwap();
        if (OperatingSystem.IsWindows()) return Win.ReadSwap();
        return (0, 0);
    }

    // ---------------- GPU ----------------

    /// <summary>显卡不会在运行中变化，只读一次。</summary>
    private static readonly Lazy<string> GpuName = new(() => Safe(ReadGpu, string.Empty));

    private static string ReadGpu()
    {
        if (OperatingSystem.IsMacOS())
        {
            // Apple Silicon / 新款 Intel Mac：IOAccelerator 上有 model 与 gpu-core-count
            var output = RunProcess("/usr/sbin/ioreg", "-rc", "IOAccelerator", "-d1");
            var names = new List<string>();
            foreach (Match match in IoregModelRegex().Matches(output))
            {
                var tail = output[match.Index..];
                var next = tail.IndexOf("+-o", 3, StringComparison.Ordinal);
                var block = next > 0 ? tail[..next] : tail;
                var cores = IoregCoresRegex().Match(block);
                names.Add(cores.Success ? $"{match.Groups[1].Value} · {cores.Groups[1].Value} 核" : match.Groups[1].Value);
            }

            return string.Join(" / ", names.Distinct());
        }

        if (OperatingSystem.IsWindows()) return Win.ReadGpuNames();

        if (OperatingSystem.IsLinux())
        {
            var nvidia = Safe(() => RunProcess("nvidia-smi", "--query-gpu=name", "--format=csv,noheader"), string.Empty);
            var names = nvidia.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
            if (names.Count == 0)
            {
                // 01:00.0 VGA compatible controller: NVIDIA Corporation GA104 [GeForce RTX 3070] (rev a1)
                foreach (var line in Safe(() => RunProcess("lspci"), string.Empty).Split('\n'))
                {
                    var match = LspciGpuRegex().Match(line);
                    if (match.Success) names.Add(RevisionRegex().Replace(match.Groups[1].Value, string.Empty).Trim());
                }
            }

            return string.Join(" / ", names.Distinct());
        }

        return string.Empty;
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
        // 根目录总是保留，即使在容器里是 overlay
        if (mount == "/") return true;
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

    [GeneratedRegex(@"""model"" = ""([^""]+)""")]
    private static partial Regex IoregModelRegex();

    [GeneratedRegex(@"""gpu-core-count"" = (\d+)")]
    private static partial Regex IoregCoresRegex();

    [GeneratedRegex(@"(?:VGA compatible controller|3D controller|Display controller):\s*(.+)$")]
    private static partial Regex LspciGpuRegex();

    [GeneratedRegex(@"\s*\(rev [0-9a-f]+\)$")]
    private static partial Regex RevisionRegex();

    [SupportedOSPlatform("macos")]
    private static class Mac
    {
        public const int HostCpuLoadInfo = 3;

        [DllImport("libSystem.dylib")] public static extern uint mach_host_self();
        [DllImport("libSystem.dylib")] public static extern int host_statistics(uint host, int flavor, uint[] info, ref uint count);
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

        /// <summary>vm.swapusage 是 xsw_usage：total / avail / used 三个 uint64，后跟页大小与加密标记。</summary>
        public static (long Total, long Used) ReadSwap()
        {
            nint length = 32;
            var buffer = new byte[32];
            if (sysctlbyname("vm.swapusage", buffer, ref length, 0, 0) != 0 || length < 24) return (0, 0);
            return ((long)BitConverter.ToUInt64(buffer, 0), (long)BitConverter.ToUInt64(buffer, 16));
        }

        public static long? SysctlInt64(string name)
        {
            nint length = 8;
            var buffer = new byte[8];
            return sysctlbyname(name, buffer, ref length, 0, 0) == 0 && length == 8 ? BitConverter.ToInt64(buffer) : null;
        }
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

        /// <summary>页面文件提交上限减去物理内存，近似为 swap 容量；已用部分同理。</summary>
        public static (long Total, long Used) ReadSwap()
        {
            var status = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
            if (!GlobalMemoryStatusEx(ref status) || status.TotalPageFile <= status.TotalPhys) return (0, 0);
            var total = (long)(status.TotalPageFile - status.TotalPhys);
            var committed = (long)(status.TotalPageFile - status.AvailPageFile);
            var ramUsed = (long)(status.TotalPhys - status.AvailPhys);
            return (total, Math.Clamp(committed - ramUsed, 0, total));
        }

        public static string ReadGpuNames()
        {
            // 显示适配器设备类
            using var root = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}");
            if (root is null) return string.Empty;
            var names = new List<string>();
            foreach (var name in root.GetSubKeyNames().Where(n => n.Length == 4 && n.All(char.IsDigit)))
            {
                using var key = root.OpenSubKey(name);
                if (key?.GetValue("DriverDesc") is string desc && !string.IsNullOrWhiteSpace(desc)
                    && !desc.Contains("Basic Display", StringComparison.OrdinalIgnoreCase)
                    && !desc.Contains("Remote Display", StringComparison.OrdinalIgnoreCase))
                    names.Add(desc.Trim());
            }

            return string.Join(" / ", names.Distinct());
        }

        public static string ReadCpuName()
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
            return (key?.GetValue("ProcessorNameString") as string)?.Trim() ?? string.Empty;
        }
    }
}
