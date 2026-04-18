using System;
using System.ComponentModel;
using System.Globalization;
using System.Threading;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media;
using Avalonia.Threading;
using Babel.Player.Models.LanguageSupport;
using Babel.Player.Resources;

namespace Babel.Player.Services;

/// <summary>
/// Runtime-switchable UI language service.  Exposes an indexer so AXAML can
/// bind via <c>{Binding [KeyName], Source={x:Static local:LocalizationService.Instance}}</c>;
/// simpler one-shot lookups use the <see cref="Converters.LocalizeExtension"/>
/// markup extension.
/// </summary>
public sealed class LocalizationService : INotifyPropertyChanged
{
    private static readonly Lazy<LocalizationService> _lazy = new(() => new LocalizationService());

    /// <summary>Process-wide singleton.</summary>
    public static LocalizationService Instance => _lazy.Value;

    private CultureInfo _currentCulture = CultureInfo.InvariantCulture;

    private LocalizationService()
    {
    }

    /// <summary>Current resolved UI culture.</summary>
    public CultureInfo CurrentCulture => _currentCulture;

    /// <summary>Raised when the culture changes so XAML bindings refresh via <c>"Item[]"</c>.</summary>
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Raised after <see cref="SetCulture"/> applies a new culture.</summary>
    public event EventHandler<CultureInfo>? CultureChanged;

    /// <summary>Bindable indexer: returns the localized string for <paramref name="key"/>.</summary>
    public string this[string key] =>
        Strings.ResourceManager.GetString(key, _currentCulture) ?? $"[{key}]";

    /// <summary>
    /// Applies a new UI culture.  Safe to call from any thread — XAML refresh
    /// and flow-direction updates are marshalled to the UI dispatcher.
    /// </summary>
    public void SetCulture(CultureInfo culture)
    {
        ArgumentNullException.ThrowIfNull(culture);

        if (Equals(_currentCulture, culture))
            return;

        _currentCulture = culture;
        Thread.CurrentThread.CurrentUICulture = culture;
        CultureInfo.DefaultThreadCurrentUICulture = culture;
        Strings.Culture = culture;

        void Apply()
        {
            ApplyFlowDirection(culture);
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
            CultureChanged?.Invoke(this, culture);
        }

        if (Dispatcher.UIThread.CheckAccess())
            Apply();
        else
            Dispatcher.UIThread.Post(Apply);
    }

    /// <summary>Resolves the effective app language from a saved setting.</summary>
    /// <remarks>
    /// <para>
    /// <paramref name="configuredLanguage"/> is either <c>"auto"</c> (track OS locale on
    /// each launch, matching the <c>Theme = "System"</c> sentinel pattern) or an
    /// ISO 639-1 code that appears in <see cref="NllbLanguageCatalog.IsoToFloresToken"/>.
    /// </para>
    /// <para>When auto-detection doesn't land on a supported code, falls back to <c>"en"</c>.</para>
    /// </remarks>
    public static string ResolveAppLanguage(string? configuredLanguage)
    {
        var trimmed = configuredLanguage?.Trim();
        if (!string.IsNullOrEmpty(trimmed) &&
            !string.Equals(trimmed, "auto", StringComparison.OrdinalIgnoreCase))
        {
            var canonical = trimmed.ToLowerInvariant();
            return NllbLanguageCatalog.IsoToFloresToken.ContainsKey(canonical) ? canonical : "en";
        }

        var osIso = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName?.ToLowerInvariant();
        if (!string.IsNullOrEmpty(osIso) && NllbLanguageCatalog.IsoToFloresToken.ContainsKey(osIso))
            return osIso;

        return "en";
    }

    private static void ApplyFlowDirection(CultureInfo culture)
    {
        var direction = IsRtlCulture(culture) ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;

        if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop)
            return;

        if (desktop.MainWindow is { } main)
            main.FlowDirection = direction;

        foreach (var window in desktop.Windows)
            window.FlowDirection = direction;
    }

    private static bool IsRtlCulture(CultureInfo culture) =>
        string.Equals(culture.TwoLetterISOLanguageName, "ar", StringComparison.OrdinalIgnoreCase);
}
