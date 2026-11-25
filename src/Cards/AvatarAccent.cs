using System.Collections.Concurrent;
using System.Globalization;
using Avalonia.Media;
using SkiaSharp;

namespace ShiroStatus.Cards;

/// <summary>
/// 从机器人头像取主题色：缩到 32x32，按色相分桶，以饱和度×亮度加权取最突出的一桶，
/// 再把饱和度和亮度收进卡片在深浅两色下都可读的范围。每个账号只取一次，结果写进插件目录。
/// </summary>
internal static class AvatarAccent
{
    public static readonly Color Fallback = Color.Parse("#4F7CFF");

    private const string CacheFileName = "accent-colors.txt";
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(5) };
    private static readonly ConcurrentDictionary<string, Color> Cache = new();
    private static readonly SemaphoreSlim FileLock = new(1, 1);
    private static int _loaded;

    /// <summary>取不到头像或头像接近灰度时返回 null，由调用方使用默认色。</summary>
    public static async Task<Color?> GetAsync(string accountKey, string? avatarUrl, string? cacheDirectory, CancellationToken cancellationToken)
    {
        LoadCache(cacheDirectory);
        if (Cache.TryGetValue(accountKey, out var cached)) return cached;
        if (string.IsNullOrWhiteSpace(avatarUrl)) return null;

        try
        {
            var bytes = await Http.GetByteArrayAsync(avatarUrl, cancellationToken).ConfigureAwait(false);
            if (Extract(bytes) is not { } color) return null;
            Cache[accountKey] = color;
            await SaveCacheAsync(cacheDirectory).ConfigureAwait(false);
            return color;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            return null;
        }
    }

    /// <summary>下载头像原图，失败返回 null。</summary>
    public static async Task<byte[]?> DownloadAsync(string? url, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        try { return await Http.GetByteArrayAsync(url, cancellationToken).ConfigureAwait(false); }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested) { return null; }
    }

    internal static Color? Extract(byte[] imageBytes)
    {
        using var source = SKBitmap.Decode(imageBytes);
        if (source is null) return null;
        using var small = source.Resize(new SKImageInfo(32, 32), new SKSamplingOptions(SKFilterMode.Linear));
        if (small is null) return null;

        const int buckets = 24;
        var weight = new double[buckets];
        var sumR = new double[buckets];
        var sumG = new double[buckets];
        var sumB = new double[buckets];
        for (var y = 0; y < small.Height; y++)
        for (var x = 0; x < small.Width; x++)
        {
            var pixel = small.GetPixel(x, y);
            if (pixel.Alpha < 128) continue;
            pixel.ToHsv(out var hue, out var saturation, out var value);
            saturation /= 100f;
            value /= 100f;
            // 跳过接近灰、白、黑的像素：它们不代表头像的“颜色”
            if (saturation < 0.18f || value < 0.18f) continue;
            // 饱和度三次方加权，让眼睛、发饰这类鲜艳的点缀色胜过大面积的淡色
            var w = saturation * saturation * saturation * value;
            // 肤色（橙红色相、中低饱和、偏亮）面积大但不代表头像的“颜色”，大幅降权
            if (hue is >= 5f and <= 45f && saturation < 0.6f && value > 0.55f) w *= 0.1f;
            var index = (int)(hue / 360f * buckets) % buckets;
            weight[index] += w;
            sumR[index] += pixel.Red * w;
            sumG[index] += pixel.Green * w;
            sumB[index] += pixel.Blue * w;
        }

        // 相邻桶合并后取最大，避免一种颜色被切成两半
        var best = -1;
        var bestWeight = 0d;
        for (var i = 0; i < buckets; i++)
        {
            var merged = weight[i] + 0.5 * (weight[(i + buckets - 1) % buckets] + weight[(i + 1) % buckets]);
            if (merged > bestWeight) { bestWeight = merged; best = i; }
        }

        // 彩色像素太少（几乎是黑白头像）时放弃
        if (best < 0 || bestWeight < 0.3) return null;
        var raw = new SKColor(
            (byte)(sumR[best] / weight[best]), (byte)(sumG[best] / weight[best]), (byte)(sumB[best] / weight[best]));
        raw.ToHsl(out var h, out var s, out var l);
        var tuned = SKColor.FromHsl(h, Math.Clamp(s, 45f, 72f), Math.Clamp(l, 48f, 58f));
        return Color.FromRgb(tuned.Red, tuned.Green, tuned.Blue);
    }

    private static void LoadCache(string? directory)
    {
        if (Interlocked.Exchange(ref _loaded, 1) == 1 || directory is null) return;
        try
        {
            var path = Path.Combine(directory, CacheFileName);
            if (!File.Exists(path)) return;
            foreach (var line in File.ReadAllLines(path))
            {
                var parts = line.Split('\t');
                if (parts.Length == 2 && Color.TryParse(parts[1], out var color)) Cache.TryAdd(parts[0], color);
            }
        }
        catch (Exception) { }
    }

    private static async Task SaveCacheAsync(string? directory)
    {
        if (directory is null) return;
        await FileLock.WaitAsync().ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(directory);
            var lines = Cache.Select(pair => $"{pair.Key}\t#{pair.Value.R:X2}{pair.Value.G:X2}{pair.Value.B:X2}");
            await File.WriteAllLinesAsync(Path.Combine(directory, CacheFileName), lines).ConfigureAwait(false);
        }
        catch (Exception) { }
        finally
        {
            FileLock.Release();
        }
    }

    /// <summary>适配器没给头像时，QQ 平台按账号拼出官方头像地址。</summary>
    public static string? ResolveAvatarUrl(string? avatarUrl, string? platform, string? userId)
    {
        if (!string.IsNullOrWhiteSpace(avatarUrl)) return avatarUrl;
        if (string.IsNullOrWhiteSpace(userId) || !userId.All(char.IsAsciiDigit)) return null;
        return platform is not null && platform.Contains("qq", StringComparison.OrdinalIgnoreCase)
            ? string.Create(CultureInfo.InvariantCulture, $"https://q1.qlogo.cn/g?b=qq&nk={userId}&s=100")
            : null;
    }
}
