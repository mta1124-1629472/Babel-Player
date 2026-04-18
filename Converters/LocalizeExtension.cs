using System;
using Avalonia.Markup.Xaml;
using Babel.Player.Resources;

namespace Babel.Player.Converters;

/// <summary>
/// XAML markup extension that resolves a localization key against the
/// embedded <c>Strings</c> resource file at load time.  Usage:
/// <code>&lt;TextBlock Text="{local:Localize Section_Translation}" /&gt;</code>
/// <para>
/// This extension produces a <em>static</em> string (the value at the time
/// the view is loaded).  For strings that should refresh when the user
/// switches language at runtime, bind against
/// <c>{Binding [KeyName], Source={x:Static local:LocalizationService.Instance}}</c>
/// instead.
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

        return Strings.ResourceManager.GetString(Key, Strings.Culture) ?? $"[{Key}]";
    }
}
