using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Threading;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media;
using Avalonia.Threading;
using Babel.Player.Resources;

namespace Babel.Player.Services;

/// <summary>
/// Runtime-switchable UI language service.  Exposes an indexer so AXAML can
/// bind via <c>{Binding [KeyName], Source={x:Static local:LocalizationService.Instance}}</c>.
/// The <see cref="Converters.LocalizeExtension"/> markup extension wraps this
/// indexer so <c>{local:Localize KeyName}</c> produces a live binding that
/// refreshes when <see cref="SetCulture"/> is called.
/// </summary>
public sealed class LocalizationService : INotifyPropertyChanged
{
    private static readonly Lazy<LocalizationService> _lazy = new(() => new LocalizationService());
    private static readonly string[] _supportedUiLanguages =
    [
        "ar", "de", "en", "es", "fr", "hi", "it", "ja",
        "ko", "nl", "pl", "pt", "ru", "sv", "tr", "zh",
    ];
    private static readonly HashSet<string> _supportedUiLanguageSet =
        new(_supportedUiLanguages, StringComparer.Ordinal);

    /// <summary>
    /// OS culture captured once at type-load time before any <see cref="SetCulture"/> call.
    /// Used by <see cref="ResolveAppLanguage"/> so <c>"auto"</c> always reflects the OS locale
    /// even after the user has switched app language in-session.
    /// </summary>
    private static readonly CultureInfo _osCulture = CultureInfo.CurrentUICulture;

    /// <summary>Process-wide singleton.</summary>
    public static LocalizationService Instance => _lazy.Value;

    private CultureInfo _currentCulture = CultureInfo.InvariantCulture;
    private int _applyVersion;

    private LocalizationService()
    {
    }

    /// <summary>Current resolved UI culture.</summary>
    public CultureInfo CurrentCulture => _currentCulture;

    /// <summary>Canonical UI language codes backed by localized resources.</summary>
    public static IReadOnlyList<string> SupportedUiLanguages => _supportedUiLanguages;

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
        var applyVersion = Interlocked.Increment(ref _applyVersion);
        CultureInfo.DefaultThreadCurrentCulture = culture;
        CultureInfo.DefaultThreadCurrentUICulture = culture;
        Strings.Culture = culture;

        void Apply()
        {
            if (applyVersion != Volatile.Read(ref _applyVersion) || !Equals(_currentCulture, culture))
                return;

            Thread.CurrentThread.CurrentCulture = culture;
            Thread.CurrentThread.CurrentUICulture = culture;
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
    /// canonical UI language code that appears in <see cref="SupportedUiLanguages"/>.
    /// </para>
    /// <para>When auto-detection doesn't land on a supported code, falls back to <c>"en"</c>.</para>
    /// </remarks>
    public static string ResolveAppLanguage(string? configuredLanguage)
    {
        var trimmed = configuredLanguage?.Trim();
        if (!string.IsNullOrEmpty(trimmed) &&
            !string.Equals(trimmed, "auto", StringComparison.OrdinalIgnoreCase))
        {
            return TryGetSupportedLanguage(trimmed) ?? "en";
        }

        var osIso = TryGetSupportedLanguage(_osCulture.Name)
            ?? TryGetSupportedLanguage(_osCulture.TwoLetterISOLanguageName);
        if (!string.IsNullOrEmpty(osIso))
            return osIso;

        return "en";
    }

    /// <summary>
    /// Applies the current culture's flow direction (LTR / RTL) to all open windows.
    /// Safe to call after a window is created later in startup, e.g. once
    /// <see cref="IClassicDesktopStyleApplicationLifetime.MainWindow"/> has been assigned,
    /// to catch any window that didn't exist when <see cref="SetCulture"/> first ran.
    /// </summary>
    public void ApplyFlowDirectionToOpenWindows() => ApplyFlowDirection(_currentCulture);

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

    private static string? TryGetSupportedLanguage(string? languageCode)
    {
        var canonical = CanonicalizeLanguageCode(languageCode);
        return canonical is not null && _supportedUiLanguageSet.Contains(canonical)
            ? canonical
            : null;
    }

    private static string? CanonicalizeLanguageCode(string? languageCode)
    {
        if (string.IsNullOrWhiteSpace(languageCode))
            return null;

        var trimmed = languageCode.Trim().Replace('_', '-');
        try
        {
            return CultureInfo.GetCultureInfo(trimmed).TwoLetterISOLanguageName.ToLowerInvariant();
        }
        catch (CultureNotFoundException)
        {
            var separator = trimmed.IndexOf('-');
            return (separator > 0 ? trimmed[..separator] : trimmed).ToLowerInvariant();
        }
    }
}
