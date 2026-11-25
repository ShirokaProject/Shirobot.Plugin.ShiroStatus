using System.Reflection;
using System.Globalization;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using ShiroBot.SDK.Plugin;
using ShiroStatus.SystemInfo;

namespace ShiroStatus.Cards;

/// <summary>#moshiro 机器状态卡片：系统监视器风格，占用用分段指示条，其余为“标签 — 数值”行；主题色取自机器人头像。</summary>
public sealed class MachineCardViewModel
{
    public string HostName { get; init; } = "shiro-mac-mini";
    public string OsText { get; init; } = "macOS 27.2 · arm64";
    public MachineOsKind OsKind { get; init; } = MachineOsKind.MacOS;
    public string OsGlyph => OsKind switch
    {
        MachineOsKind.MacOS => MachineIcons.Apple,
        MachineOsKind.Windows => MachineIcons.Windows,
        _ => MachineIcons.Linux,
    };
    /// <summary>
    /// 徽标尺寸取偶数配合 34px 方块，避免布局取整把它挤偏；方块徽标显得更大，略缩一点。
    /// 苹果下半身更重，上移 1px 让视觉重心落在方块中心。
    /// </summary>
    public double OsGlyphSize => OsKind switch { MachineOsKind.Windows => 14, MachineOsKind.MacOS => 16, _ => 18 };
    public Avalonia.Thickness OsGlyphMargin => OsKind == MachineOsKind.MacOS ? new(0, 0, 0, 2) : default;
    public string SystemUptimeText { get; init; } = "1d 4h 12min";

    /// <summary>主题色：OS 方块、分区标题和正常水位的指示条。只存颜色，画刷在渲染线程上创建。</summary>
    public Color ThemeColor { get; init; } = AvatarAccent.Fallback;
    public IBrush Theme => new SolidColorBrush(ThemeColor);

    /// <summary>CPU、内存与每块磁盘各一行。</summary>
    public IReadOnlyList<MachineMeter> Meters { get; init; } =
    [
        MachineMeter.Create("CPU", 0.12, "10 核"),
        MachineMeter.Create("内存", 0.78, "12.4 / 16 GB"),
        MachineMeter.Create("系统盘", 0.97, "221 / 228 GB"),
    ];

    /// <summary>两列排布的规格。</summary>
    public IReadOnlyList<MachineDetail> Specs { get; init; } =
    [
        new("处理器", "Apple M4", MachineIcons.Cpu),
        new("GPU", "Apple M4 · 8 核", MachineIcons.Gpu),
        new("进程", "612 个", MachineIcons.Processes),
        new("内核", "Darwin 27.2.0", MachineIcons.Kernel),
        new("运行时", ".NET 11.0.0", MachineIcons.Runtime),
    ];

    /// <summary>当前消息所在的适配器：适配器、平台、实例、账号，两列排布。</summary>
    public IReadOnlyList<MachineDetail> AdapterDetails { get; init; } =
    [
        new("适配器", "OneBot v0.2.4", MachineIcons.Adapter),
        new("平台", "qq / onebot-v11", MachineIcons.Platform),
        new("实例", "主号 (qq-main)", MachineIcons.Instance),
        new("账号", "白花 (3889513577)", MachineIcons.Account),
    ];
    public bool HasAdapterDetails => AdapterDetails.Count > 0;
    public string AdapterCountText { get; init; } = "共 2 个实例";

    /// <summary>ShiroBot 自身：版本、运行、进程内存、消息。</summary>
    public IReadOnlyList<MachineDetail> ShiroStats { get; init; } =
    [
        new("版本", "1.0.4", MachineIcons.Version),
        new("已运行", "2d 3h 14min", MachineIcons.Uptime),
        new("进程内存", "182 MB", MachineIcons.Memory),
        new("消息", "12,345", MachineIcons.Message),
    ];
    public bool HasShiroStats => ShiroStats.Count > 0;

    public string GeneratedText { get; init; } = "2026-11-26 00:38";

    /// <summary>左下角：发命令的人的头像 | ShiroBot 小图标 + “ShiroBot Status v版本”。</summary>
    public string BrandText { get; } = "ShiroBot Status v" +
        (typeof(MachineCardViewModel).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "?");

    /// <summary>头像原始字节；Bitmap 在渲染线程上解码。</summary>
    public byte[]? UserAvatarBytes { get; init; }
    public Bitmap? UserAvatar => _userAvatar ??= DecodeOrNull(UserAvatarBytes);
    private Bitmap? _userAvatar;
    public bool HasUserAvatar => UserAvatarBytes is { Length: > 0 } && UserAvatar is not null;

    private static Bitmap? DecodeOrNull(byte[]? bytes)
    {
        if (bytes is not { Length: > 0 }) return null;
        try { return Bitmap.DecodeToWidth(new MemoryStream(bytes), 64); }
        catch (Exception) { return null; }
    }

    /// <summary>每张卡片在渲染线程上用缓存的 PNG 字节各自解码，避免跨线程共用同一个 Bitmap。</summary>
    public Bitmap? Mascot => _mascot ??= MascotBytes.Value is { } bytes ? new Bitmap(new MemoryStream(bytes)) : null;
    private Bitmap? _mascot;

    private static readonly Lazy<byte[]?> MascotBytes = new(() =>
    {
        using var stream = typeof(MachineCardViewModel).Assembly.GetManifestResourceStream("ShiroStatus.Assets.shirobana.png");
        if (stream is null) return null;
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    });

    internal static MachineCardViewModel Create(SystemSnapshot machine, HostStatus? host, DateTimeOffset now, AdapterReport? adapter = null,
        Color? themeColor = null, byte[]? userAvatar = null)
    {
        var theme = themeColor ?? AvatarAccent.Fallback;
        var runtime = host?.Runtime;
        var arch = runtime?.Architecture ?? System.Runtime.InteropServices.RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant();
        var os = !string.IsNullOrWhiteSpace(runtime?.OperatingSystem) ? runtime.OperatingSystem : machine.KernelDescription;

        var meters = new List<MachineMeter>
        {
            MachineMeter.Create("CPU", machine.CpuUsage, $"{machine.LogicalCores} 核", theme, MachineIcons.Usage),
            MachineMeter.Create("内存", machine.MemoryTotalBytes > 0 ? machine.MemoryUsedRatio : null,
                machine.MemoryTotalBytes > 0 ? $"{FormatBytes(machine.MemoryUsedBytes, false)} / {FormatBytes(machine.MemoryTotalBytes)}" : "--", theme, MachineIcons.Memory),
        };
        if (machine.SwapTotalBytes > 0)
            meters.Add(MachineMeter.Create("Swap", machine.SwapUsedRatio,
                $"{FormatBytes(machine.SwapUsedBytes, false)} / {FormatBytes(machine.SwapTotalBytes)}", theme, MachineIcons.Swap));
        meters.AddRange(machine.Disks.Select(disk => MachineMeter.Create(disk.Name, disk.UsedRatio,
            $"{FormatBytes(disk.UsedBytes, false)} / {FormatBytes(disk.TotalBytes)}", theme, MachineIcons.Disk)));

        var specs = new List<MachineDetail>();
        if (!string.IsNullOrWhiteSpace(machine.CpuModel)) specs.Add(new("处理器", machine.CpuModel, MachineIcons.Cpu));
        if (!string.IsNullOrWhiteSpace(machine.Gpu)) specs.Add(new("GPU", machine.Gpu, MachineIcons.Gpu));
        specs.Add(new("内核", machine.KernelDescription, MachineIcons.Kernel));
        if (machine.ProcessCount > 0)
            specs.Add(new("进程", machine.ProcessCount.ToString("N0", CultureInfo.InvariantCulture) + " 个", MachineIcons.Processes));
        if (!string.IsNullOrWhiteSpace(runtime?.Framework)) specs.Add(new("运行时", runtime.Framework, MachineIcons.Runtime));

        var shiro = new List<MachineDetail>();
        if (host is not null)
        {
            shiro.Add(new("版本", host.Version, MachineIcons.Version));
            shiro.Add(new("已运行", FormatUptime(host.Uptime), MachineIcons.Uptime));
            shiro.Add(new("进程内存", FormatBytes(host.Runtime.MemoryBytes), MachineIcons.Memory));
            shiro.Add(new("消息", host.MessageCount.ToString("N0", CultureInfo.InvariantCulture), MachineIcons.Message));
        }

        var adapterDetails = new List<MachineDetail>();
        if (adapter?.Instance is { } instance)
        {
            adapterDetails.Add(new("适配器", StatusReport.AdapterTitle(instance), MachineIcons.Adapter));
            var protocol = string.Join(" / ", new[] { instance.Platform, instance.Protocol }.Where(v => !string.IsNullOrWhiteSpace(v)));
            if (protocol.Length > 0) adapterDetails.Add(new("平台", protocol, MachineIcons.Platform));
            adapterDetails.Add(new("实例", string.IsNullOrWhiteSpace(instance.Name) || instance.Name == instance.Id ? instance.Id : $"{instance.Name} ({instance.Id})", MachineIcons.Instance));
        }

        if (adapter?.Self is { } self)
            adapterDetails.Add(new("账号", string.IsNullOrWhiteSpace(self.Name) || self.Name == self.Id ? self.Id : $"{self.Name} ({self.Id})", MachineIcons.Account));

        return new MachineCardViewModel
        {
            ThemeColor = theme,
            UserAvatarBytes = userAvatar,
            AdapterDetails = adapterDetails,
            AdapterCountText = adapter is { Instances.Count: > 0 } ? $"共 {adapter.Instances.Count} 个实例" : string.Empty,
            HostName = machine.HostName,
            OsText = $"{os} · {arch}",
            OsKind = OperatingSystem.IsMacOS() ? MachineOsKind.MacOS : OperatingSystem.IsWindows() ? MachineOsKind.Windows : MachineOsKind.Linux,
            SystemUptimeText = FormatUptime(machine.SystemUptime),
            Meters = meters,
            Specs = specs,
            ShiroStats = shiro,
            GeneratedText = now.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture),
        };
    }

    internal static string FormatBytes(long bytes, bool unit = true)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB", "PB"];
        double value = Math.Max(0, bytes);
        var index = 0;
        while (value >= 1024 && index < units.Length - 1) { value /= 1024; index++; }
        var number = value.ToString(value >= 100 || index == 0 ? "0" : "0.#", CultureInfo.InvariantCulture);
        return unit ? $"{number} {units[index]}" : number;
    }

    /// <summary>"3h 14min"；超过一天加上天数："2d 3h 14min"。数字和单位之间不留空格。</summary>
    internal static string FormatUptime(TimeSpan uptime)
    {
        if (uptime < TimeSpan.Zero) uptime = TimeSpan.Zero;
        if (uptime.TotalDays >= 1) return $"{uptime.Days}d {uptime.Hours}h {uptime.Minutes}min";
        if (uptime.TotalHours >= 1) return $"{uptime.Hours}h {uptime.Minutes}min";
        return $"{uptime.Minutes}min";
    }
}

public enum MachineOsKind { MacOS, Windows, Linux }

/// <summary>一行分段指示条：亮起的格数对应占用，颜色随占用由绿到琥珀到红。</summary>
public sealed class MachineMeter
{
    public const int SegmentCount = 24;

    public string Label { get; init; } = string.Empty;
    public string Icon { get; init; } = string.Empty;
    public string PercentText { get; init; } = "--";
    public string Caption { get; init; } = string.Empty;
    public IReadOnlyList<MachineSegment> Segments { get; init; } = [];
    /// <summary>只存颜色；画刷在绑定时（渲染线程上）才创建，模型可以在任意线程构造。</summary>
    public Color AccentColor { get; init; } = Colors.Gray;
    public IBrush Accent => new SolidColorBrush(AccentColor);

    /// <summary>正常水位用主题色，70% 起琥珀、90% 起红，告警色不随主题变化。</summary>
    public static MachineMeter Create(string label, double? ratio, string caption, Color? normal = null, string icon = "")
    {
        var level = Math.Clamp(ratio ?? 0, 0, 1);
        var lit = ratio is null ? 0 : Math.Max(1, (int)Math.Round(level * SegmentCount));
        var accent = LevelColor(level, normal ?? AvatarAccent.Fallback);
        return new MachineMeter
        {
            Label = label,
            Icon = icon,
            Caption = caption,
            PercentText = ratio is null ? "--" : Math.Round(level * 100).ToString(CultureInfo.InvariantCulture) + "%",
            AccentColor = accent,
            Segments = Enumerable.Range(0, SegmentCount).Select(index => new MachineSegment(index < lit, accent)).ToArray(),
        };
    }

    internal static Color LevelColor(double ratio, Color normal) => ratio switch
    {
        >= 0.9 => Color.Parse("#F04848"),
        >= 0.7 => Color.Parse("#F5A524"),
        _ => normal,
    };
}

public sealed record MachineSegment(bool IsLit, Color Color)
{
    public IBrush Fill => new SolidColorBrush(Color);
}

public sealed record MachineDetail(string Label, string Value, string Icon = "");

/// <summary>系统徽标（Simple Icons）与信息图标（Material Design Icons），均为 24x24。</summary>
public static class MachineIcons
{
    public const string Usage = "M12,16A3,3 0 0,1 9,13C9,11.88 9.61,10.9 10.5,10.39L20.21,4.77L14.68,14.35C14.18,15.33 13.17,16 12,16M12,3C13.81,3 15.5,3.5 16.97,4.32L14.87,5.53C14,5.19 13,5 12,5A8,8 0 0,0 4,13C4,15.21 4.89,17.21 6.34,18.65H6.35C6.74,19.04 6.74,19.67 6.35,20.06C5.96,20.45 5.32,20.45 4.93,20.07V20.07C3.12,18.26 2,15.76 2,13A10,10 0 0,1 12,3M22,13C22,15.76 20.88,18.26 19.07,20.07V20.07C18.68,20.45 18.05,20.45 17.66,20.06C17.27,19.67 17.27,19.04 17.66,18.65V18.65C19.11,17.2 20,15.21 20,13C20,12 19.81,11 19.46,10.1L20.67,8C21.5,9.5 22,11.18 22,13Z";
    public const string Cpu = "M9,3V5H7A2,2 0 0,0 5,7V9H3V11H5V13H3V15H5V17A2,2 0 0,0 7,19H9V21H11V19H13V21H15V19H17A2,2 0 0,0 19,17V15H21V13H19V11H21V9H19V7A2,2 0 0,0 17,5H15V3H13V5H11V3M8,9H11.5V10.5H8.5V11.25H10.5A1,1 0 0,1 11.5,12.25V14A1,1 0 0,1 10.5,15H8A1,1 0 0,1 7,14V10A1,1 0 0,1 8,9M12.5,9H14V11H15.5V9H17V15H15.5V12.5H12.5M8.5,12.75V13.5H10V12.75";
    public const string Gpu = "M2,7V8.5H3V17H4.5V7C3.7,7 2.8,7 2,7M6,7V7L6,16H7V17H14V16H22V7H6M17.5,9A2.5,2.5 0 0,1 20,11.5A2.5,2.5 0 0,1 17.5,14A2.5,2.5 0 0,1 15,11.5A2.5,2.5 0 0,1 17.5,9Z";
    public const string Kernel = "M12,18.54L19.37,12.8L21,14.07L12,21.07L3,14.07L4.62,12.81L12,18.54M12,16L3,9L12,2L21,9L12,16M12,4.53L6.26,9L12,13.47L17.74,9L12,4.53Z";
    public const string Processes = "M16,20H20V16H16M16,14H20V10H16M10,8H14V4H10M16,8H20V4H16M10,14H14V10H10M4,14H8V10H4M4,20H8V16H4M10,20H14V16H10M4,8H8V4H4V8Z";
    public const string Runtime = "M8,3A2,2 0 0,0 6,5V9A2,2 0 0,1 4,11H3V13H4A2,2 0 0,1 6,15V19A2,2 0 0,0 8,21H10V19H8V14A2,2 0 0,0 6,12A2,2 0 0,0 8,10V5H10V3M16,3A2,2 0 0,1 18,5V9A2,2 0 0,0 20,11H21V13H20A2,2 0 0,0 18,15V19A2,2 0 0,1 16,21H14V19H16V14A2,2 0 0,1 18,12A2,2 0 0,1 16,10V5H14V3H16Z";
    public const string Adapter = "M16 7V3H14V7H10V3H8V7C7 7 6 8 6 9V14.5L9.5 18V21H14.5V18L18 14.5V9C18 8 17 7 16 7M16 13.67L13.09 16.59L12.67 17H11.33L10.92 16.59L8 13.67V9.09C8 9.06 8.06 9 8.09 9H15.92C15.95 9 16 9.06 16 9.09V13.67Z";
    public const string Platform = "M16.36,14C16.44,13.34 16.5,12.68 16.5,12C16.5,11.32 16.44,10.66 16.36,10H19.74C19.9,10.64 20,11.31 20,12C20,12.69 19.9,13.36 19.74,14M14.59,19.56C15.19,18.45 15.65,17.25 15.97,16H18.92C17.96,17.65 16.43,18.93 14.59,19.56M14.34,14H9.66C9.56,13.34 9.5,12.68 9.5,12C9.5,11.32 9.56,10.65 9.66,10H14.34C14.43,10.65 14.5,11.32 14.5,12C14.5,12.68 14.43,13.34 14.34,14M12,19.96C11.17,18.76 10.5,17.43 10.09,16H13.91C13.5,17.43 12.83,18.76 12,19.96M8,8H5.08C6.03,6.34 7.57,5.06 9.4,4.44C8.8,5.55 8.35,6.75 8,8M5.08,16H8C8.35,17.25 8.8,18.45 9.4,19.56C7.57,18.93 6.03,17.65 5.08,16M4.26,14C4.1,13.36 4,12.69 4,12C4,11.31 4.1,10.64 4.26,10H7.64C7.56,10.66 7.5,11.32 7.5,12C7.5,12.68 7.56,13.34 7.64,14M12,4.03C12.83,5.23 13.5,6.57 13.91,8H10.09C10.5,6.57 11.17,5.23 12,4.03M18.92,8H15.97C15.65,6.75 15.19,5.55 14.59,4.44C16.43,5.07 17.96,6.34 18.92,8M12,2C6.47,2 2,6.5 2,12A10,10 0 0,0 12,22A10,10 0 0,0 22,12A10,10 0 0,0 12,2Z";
    public const string Instance = "M2 4.6V9.4C2 10.3 2.5 11 3.2 11H20.9C21.5 11 22.1 10.3 22.1 9.4V4.6C22 3.7 21.5 3 20.8 3H3.2C2.5 3 2 3.7 2 4.6M10 8V6H9V8H10M5 8H7V6H5V8M20 9H4V5H20V9M2 14.6V19.4C2 20.3 2.5 21 3.2 21H20.9C21.5 21 22.1 20.3 22.1 19.4V14.6C22.1 13.7 21.6 13 20.9 13H3.2C2.5 13 2 13.7 2 14.6M10 18V16H9V18H10M5 18H7V16H5V18M20 19H4V15H20V19Z";
    public const string Account = "M12,2A10,10 0 0,0 2,12A10,10 0 0,0 12,22A10,10 0 0,0 22,12A10,10 0 0,0 12,2M7.07,18.28C7.5,17.38 10.12,16.5 12,16.5C13.88,16.5 16.5,17.38 16.93,18.28C15.57,19.36 13.86,20 12,20C10.14,20 8.43,19.36 7.07,18.28M18.36,16.83C16.93,15.09 13.46,14.5 12,14.5C10.54,14.5 7.07,15.09 5.64,16.83C4.62,15.5 4,13.82 4,12C4,7.59 7.59,4 12,4C16.41,4 20,7.59 20,12C20,13.82 19.38,15.5 18.36,16.83M12,6C10.06,6 8.5,7.56 8.5,9.5C8.5,11.44 10.06,13 12,13C13.94,13 15.5,11.44 15.5,9.5C15.5,7.56 13.94,6 12,6M12,11A1.5,1.5 0 0,1 10.5,9.5A1.5,1.5 0 0,1 12,8A1.5,1.5 0 0,1 13.5,9.5A1.5,1.5 0 0,1 12,11Z";
    public const string Version = "M21.41 11.58L12.41 2.58A2 2 0 0 0 11 2H4A2 2 0 0 0 2 4V11A2 2 0 0 0 2.59 12.42L11.59 21.42A2 2 0 0 0 13 22A2 2 0 0 0 14.41 21.41L21.41 14.41A2 2 0 0 0 22 13A2 2 0 0 0 21.41 11.58M13 20L4 11V4H11L20 13M6.5 5A1.5 1.5 0 1 1 5 6.5A1.5 1.5 0 0 1 6.5 5Z";
    public const string Uptime = "M12,20A7,7 0 0,1 5,13A7,7 0 0,1 12,6A7,7 0 0,1 19,13A7,7 0 0,1 12,20M19.03,7.39L20.45,5.97C20,5.46 19.55,5 19.04,4.56L17.62,6C16.07,4.74 14.12,4 12,4A9,9 0 0,0 3,13A9,9 0 0,0 12,22C17,22 21,17.97 21,13C21,10.88 20.26,8.93 19.03,7.39M11,14H13V8H11M15,1H9V3H15V1Z";
    public const string Memory = "M17,17H7V7H17M21,11V9H19V7C19,5.89 18.1,5 17,5H15V3H13V5H11V3H9V5H7C5.89,5 5,5.89 5,7V9H3V11H5V13H3V15H5V17A2,2 0 0,0 7,19H9V21H11V19H13V21H15V19H17A2,2 0 0,0 19,17V15H21V13H19V11M13,13H11V11H13M15,9H9V15H15V9Z";
    public const string Message = "M20,2A2,2 0 0,1 22,4V16A2,2 0 0,1 20,18H6L2,22V4C2,2.89 2.9,2 4,2H20M4,4V17.17L5.17,16H20V4H4M6,7H18V9H6V7M6,11H15V13H6V11Z";
    public const string Swap = "M21,9L17,5V8H10V10H17V13M7,11L3,15L7,19V16H14V14H7V11Z";
    public const string Disk = "M6,2H18A2,2 0 0,1 20,4V20A2,2 0 0,1 18,22H6A2,2 0 0,1 4,20V4A2,2 0 0,1 6,2M12,4A6,6 0 0,0 6,10C6,13.31 8.69,16 12.1,16L11.22,13.77C10.95,13.29 11.11,12.68 11.59,12.4L12.45,11.9C12.93,11.63 13.54,11.79 13.82,12.27L15.74,14.69C17.12,13.59 18,11.9 18,10A6,6 0 0,0 12,4M12,9A1,1 0 0,1 13,10A1,1 0 0,1 12,11A1,1 0 0,1 11,10A1,1 0 0,1 12,9M7,18A1,1 0 0,0 6,19A1,1 0 0,0 7,20A1,1 0 0,0 8,19A1,1 0 0,0 7,18M12.09,13.27L14.58,19.58L17.17,18.08L12.95,12.77L12.09,13.27Z";

    public const string Apple = "M12.152 6.896c-.948 0-2.415-1.078-3.96-1.04-2.04.027-3.91 1.183-4.961 3.014-2.117 3.675-.546 9.103 1.519 12.09 1.013 1.454 2.208 3.09 3.792 3.039 1.52-.065 2.09-.987 3.935-.987 1.831 0 2.35.987 3.96.948 1.637-.026 2.676-1.48 3.676-2.948 1.156-1.688 1.636-3.325 1.662-3.415-.039-.013-3.182-1.221-3.22-4.857-.026-3.04 2.48-4.494 2.597-4.559-1.429-2.09-3.623-2.324-4.39-2.376-2-.156-3.675 1.09-4.61 1.09zM15.53 3.83c.843-1.012 1.4-2.427 1.245-3.83-1.207.052-2.662.805-3.532 1.818-.78.896-1.454 2.338-1.273 3.714 1.338.104 2.715-.688 3.559-1.701";
    public const string Windows = "M0,0H11.377V11.372H0ZM12.623,0H24V11.372H12.623ZM0,12.623H11.377V24H0Zm12.623,0H24V24H12.623";
    public const string Linux = "M12.504 0c-.155 0-.315.008-.48.021-4.226.333-3.105 4.807-3.17 6.298-.076 1.092-.3 1.953-1.05 3.02-.885 1.051-2.127 2.75-2.716 4.521-.278.832-.41 1.684-.287 2.489a.424.424 0 00-.11.135c-.26.268-.45.6-.663.839-.199.199-.485.267-.797.4-.313.136-.658.269-.864.68-.09.189-.136.394-.132.602 0 .199.027.4.055.536.058.399.116.728.04.97-.249.68-.28 1.145-.106 1.484.174.334.535.47.94.601.81.2 1.91.135 2.774.6.926.466 1.866.67 2.616.47.526-.116.97-.464 1.208-.946.587-.003 1.23-.269 2.26-.334.699-.058 1.574.267 2.577.2.025.134.063.198.114.333l.003.003c.391.778 1.113 1.132 1.884 1.071.771-.06 1.592-.536 2.257-1.306.631-.765 1.683-1.084 2.378-1.503.348-.199.629-.469.649-.853.023-.4-.2-.811-.714-1.376v-.097l-.003-.003c-.17-.2-.25-.535-.338-.926-.085-.401-.182-.786-.492-1.046h-.003c-.059-.054-.123-.067-.188-.135a.357.357 0 00-.19-.064c.431-1.278.264-2.55-.173-3.694-.533-1.41-1.465-2.638-2.175-3.483-.796-1.005-1.576-1.957-1.56-3.368.026-2.152.236-6.133-3.544-6.139zm.529 3.405h.013c.213 0 .396.062.584.198.19.135.33.332.438.533.105.259.158.459.166.724 0-.02.006-.04.006-.06v.105a.086.086 0 01-.004-.021l-.004-.024a1.807 1.807 0 01-.15.706.953.953 0 01-.213.335.71.71 0 00-.088-.042c-.104-.045-.198-.064-.284-.133a1.312 1.312 0 00-.22-.066c.05-.06.146-.133.183-.198.053-.128.082-.264.088-.402v-.02a1.21 1.21 0 00-.061-.4c-.045-.134-.101-.2-.183-.333-.084-.066-.167-.132-.267-.132h-.016c-.093 0-.176.03-.262.132a.8.8 0 00-.205.334 1.18 1.18 0 00-.09.4v.019c.002.089.008.179.02.267-.193-.067-.438-.135-.607-.202a1.635 1.635 0 01-.018-.2v-.02a1.772 1.772 0 01.15-.768c.082-.22.232-.406.43-.533a.985.985 0 01.594-.2zm-2.962.059h.036c.142 0 .27.048.399.135.146.129.264.288.344.465.09.199.14.4.153.667v.004c.007.134.006.2-.002.266v.08c-.03.007-.056.018-.083.024-.152.055-.274.135-.393.2.012-.09.013-.18.003-.267v-.015c-.012-.133-.04-.2-.082-.333a.613.613 0 00-.166-.267.248.248 0 00-.183-.064h-.021c-.071.006-.13.04-.186.132a.552.552 0 00-.12.27.944.944 0 00-.023.33v.015c.012.135.037.2.08.334.046.134.098.2.166.268.01.009.02.018.034.024-.07.057-.117.07-.176.136a.304.304 0 01-.131.068 2.62 2.62 0 01-.275-.402 1.772 1.772 0 01-.155-.667 1.759 1.759 0 01.08-.668 1.43 1.43 0 01.283-.535c.128-.133.26-.2.418-.2zm1.37 1.706c.332 0 .733.065 1.216.399.293.2.523.269 1.052.468h.003c.255.136.405.266.478.399v-.131a.571.571 0 01.016.47c-.123.31-.516.643-1.063.842v.002c-.268.135-.501.333-.775.465-.276.135-.588.292-1.012.267a1.139 1.139 0 01-.448-.067 3.566 3.566 0 01-.322-.198c-.195-.135-.363-.332-.612-.465v-.005h-.005c-.4-.246-.616-.512-.686-.71-.07-.268-.005-.47.193-.6.224-.135.38-.271.483-.336.104-.074.143-.102.176-.131h.002v-.003c.169-.202.436-.47.839-.601.139-.036.294-.065.466-.065zm2.8 2.142c.358 1.417 1.196 3.475 1.735 4.473.286.534.855 1.659 1.102 3.024.156-.005.33.018.513.064.646-1.671-.546-3.467-1.089-3.966-.22-.2-.232-.335-.123-.335.59.534 1.365 1.572 1.646 2.757.13.535.16 1.104.021 1.67.067.028.135.06.205.067 1.032.534 1.413.938 1.23 1.537v-.043c-.06-.003-.12 0-.18 0h-.016c.151-.467-.182-.825-1.065-1.224-.915-.4-1.646-.336-1.77.465-.008.043-.013.066-.018.135-.068.023-.139.053-.209.064-.43.268-.662.669-.793 1.187-.13.533-.17 1.156-.205 1.869v.003c-.02.334-.17.838-.319 1.35-1.5 1.072-3.58 1.538-5.348.334a2.645 2.645 0 00-.402-.533 1.45 1.45 0 00-.275-.333c.182 0 .338-.03.465-.067a.615.615 0 00.314-.334c.108-.267 0-.697-.345-1.163-.345-.467-.931-.995-1.788-1.521-.63-.4-.986-.87-1.15-1.396-.165-.534-.143-1.085-.015-1.645.245-1.07.873-2.11 1.274-2.763.107-.065.037.135-.408.974-.396.751-1.14 2.497-.122 3.854a8.123 8.123 0 01.647-2.876c.564-1.278 1.743-3.504 1.836-5.268.048.036.217.135.289.202.218.133.38.333.59.465.21.201.477.335.876.335.039.003.075.006.11.006.412 0 .73-.134.997-.268.29-.134.52-.334.74-.4h.005c.467-.135.835-.402 1.044-.7zm2.185 8.958c.037.6.343 1.245.882 1.377.588.134 1.434-.333 1.791-.765l.211-.01c.315-.007.577.01.847.268l.003.003c.208.199.305.53.391.876.085.4.154.78.409 1.066.486.527.645.906.636 1.14l.003-.007v.018l-.003-.012c-.015.262-.185.396-.498.595-.63.401-1.746.712-2.457 1.57-.618.737-1.37 1.14-2.036 1.191-.664.053-1.237-.2-1.574-.898l-.005-.003c-.21-.4-.12-1.025.056-1.69.176-.668.428-1.344.463-1.897.037-.714.076-1.335.195-1.814.12-.465.308-.797.641-.984l.045-.022zm-10.814.049h.01c.053 0 .105.005.157.014.376.055.706.333 1.023.752l.91 1.664.003.003c.243.533.754 1.064 1.189 1.637.434.598.77 1.131.729 1.57v.006c-.057.744-.48 1.148-1.125 1.294-.645.135-1.52.002-2.395-.464-.968-.536-2.118-.469-2.857-.602-.369-.066-.61-.2-.723-.4-.11-.2-.113-.602.123-1.23v-.004l.002-.003c.117-.334.03-.752-.027-1.118-.055-.401-.083-.71.043-.94.16-.334.396-.4.69-.533.294-.135.64-.202.915-.47h.002v-.002c.256-.268.445-.601.668-.838.19-.201.38-.336.663-.336zm7.159-9.074c-.435.201-.945.535-1.488.535-.542 0-.97-.267-1.28-.466-.154-.134-.28-.268-.373-.335-.164-.134-.144-.333-.074-.333.109.016.129.134.199.2.096.066.215.2.36.333.292.2.68.467 1.167.467.485 0 1.053-.267 1.398-.466.195-.135.445-.334.648-.467.156-.136.149-.267.279-.267.128.016.034.134-.147.332a8.097 8.097 0 01-.69.468zm-1.082-1.583V5.64c-.006-.02.013-.042.029-.05.074-.043.18-.027.26.004.063 0 .16.067.15.135-.006.049-.085.066-.135.066-.055 0-.092-.043-.141-.068-.052-.018-.146-.008-.163-.065zm-.551 0c-.02.058-.113.049-.166.066-.047.025-.086.068-.14.068-.05 0-.13-.02-.136-.068-.01-.066.088-.133.15-.133.08-.031.184-.047.259-.005.019.009.036.03.03.05v.02h.003z";
}
