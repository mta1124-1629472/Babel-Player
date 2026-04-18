using System;
using Avalonia.Data;
using Avalonia.Markup.Xaml;
using Babel.Player.Services;

namespace Babel.Player.Converters;

/// <summary>
/// XAML markup extension that creates a <em>live</em> binding to the
/// <see cref="LocalizationService"/> indexer so UI text refreshes when the
/// user switches language at runtime.  Usage:
/// <code>&lt;TextBlock Text="{local:Localize Section_Translation}" /&gt;</code>
/// <para>
/// Under the hood this returns a <see cref="Binding"/> targeting
/// <c>[KeyName]</c> on <see cref="LocalizationService.Instance"/>.
/// When <see cref="LocalizationService.SetCulture"/> fires
/// <c>PropertyChanged("Item[]")</c>, every bound property re-evaluates
/// through the indexer and picks up the new culture's string.
/// </para>
/// </summary>
public sealed class LocalizeExtension : MarkupExtension
{
    /// <summary>Creates a new markup extension for the given key.</summary>
    public LocalizeExtension()
    {
    }

    /// <summary>Convenience ctor so <c>{local:Localize KeyName}</c> parses.</summary>
    public LocalizeExtension(string key)
    {
        Key = key;
    }

    /// <summary>Resource key to look up in <c>Strings.resx</c>.</summary>
    public string? Key { get; set; }

    /// <inheritdoc />
    public override object ProvideValue(IServiceProvider serviceProvider)
    {
        if (string.IsNullOrEmpty(Key))
            return string.Empty;

        var binding = new Binding
        {
            Source = LocalizationService.Instance,
            Path = $"[{Key}]",
            Mode = BindingMode.OneWay,
        };

        return binding;
    }
}
