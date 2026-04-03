using System;
using System.Globalization;
using Avalonia.Data.Converters;
using Babel.Player.Models;

namespace Babel.Player.Converters;

public sealed class ComputeProfileDisplayConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        ComputeProfile.Cpu => "CPU",
        ComputeProfile.Gpu => "GPU",
        ComputeProfile.Cloud => "Cloud",
        _ => value?.ToString() ?? string.Empty,
    };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

public sealed class GpuHostBackendDisplayConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        GpuHostBackend.ManagedVenv => "Managed local GPU",
        GpuHostBackend.DockerHost => "Docker GPU host",
        _ => value?.ToString() ?? string.Empty,
    };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
