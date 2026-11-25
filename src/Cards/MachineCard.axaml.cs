using System.Globalization;
using Avalonia.Controls;
using Avalonia.Data.Converters;
using Avalonia.Markup.Xaml;
using Avalonia.Media;

namespace ShiroStatus.Cards;

public partial class MachineCard : UserControl
{
    public MachineCard() => AvaloniaXamlLoader.Load(this);
}

/// <summary>在渲染线程上把图标 path 字符串解析为 Geometry。</summary>
public sealed class GeometryConverter : IValueConverter
{
    public static readonly GeometryConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is string data && !string.IsNullOrWhiteSpace(data) ? StreamGeometry.Parse(data) : null;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
