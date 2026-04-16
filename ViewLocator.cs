using System;
using System.Reflection;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Media;
using Babel.Player.ViewModels;

namespace Babel.Player;

/// <summary>
/// Maps <see cref="ViewModelBase"/> types to views in <c>Babel.Player.Views</c> by naming convention
/// (<c>FooViewModel</c> → <c>Foo</c>).
/// </summary>
public sealed class ViewLocator : IDataTemplate
{
    private static readonly Assembly Assembly = Assembly.GetExecutingAssembly();
    private static readonly string? AssemblyName = Assembly.GetName().Name;

    public bool Match(object? data) => data is ViewModelBase;

    public Control? Build(object? param)
    {
        if (param is null)
            return null;

        var vmType = param.GetType();
        var name = vmType.Name;
        if (!name.EndsWith("ViewModel", StringComparison.Ordinal))
        {
            return new TextBlock
            {
                Text = $"No view mapping for {vmType.FullName}.",
                Foreground = Brushes.OrangeRed,
            };
        }

        var shortName = name[..^"ViewModel".Length];
        var viewFullName = $"Babel.Player.Views.{shortName}";
        var type = Assembly.GetType(viewFullName)
            ?? (AssemblyName is not null ? Type.GetType($"{viewFullName}, {AssemblyName}") : null);

        if (type is null)
        {
            return new TextBlock
            {
                Text = $"View not found: {viewFullName}",
                Foreground = Brushes.OrangeRed,
            };
        }

        try
        {
            return (Control)Activator.CreateInstance(type)!;
        }
        catch (Exception ex)
        {
            return new TextBlock
            {
                Text = $"View create failed: {type.Name} — {ex.Message}",
                Foreground = Brushes.OrangeRed,
            };
        }
    }
}
