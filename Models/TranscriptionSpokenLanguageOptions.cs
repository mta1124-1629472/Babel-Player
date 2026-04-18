using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using Babel.Player.Models.LanguageSupport;
using Babel.Player.Services;

namespace Babel.Player.Models;

/// <summary>Optional ASR language hints (Whisper <c>language=</c>). <see cref="Code"/> null means auto-detect.</summary>
/// <remarks>
/// Hints are limited to languages that appear in both <see cref="NllbLanguageCatalog"/> and <see cref="WhisperAsrLanguageCatalog"/>.
/// <see cref="DisplayName"/> is resolved live so runtime UI-culture switches relabel the ComboBox items.
/// </remarks>
public sealed class SpokenLanguageOption : INotifyPropertyChanged, IEquatable<SpokenLanguageOption>
{
    public string? Code { get; }

    private readonly string? _staticDisplayName;

    /// <summary>Localized display name, re-resolved on each access; constant for the auto-detect entry.</summary>
    public string DisplayName => _staticDisplayName ?? LanguageDisplayNames.ForIso639(
        Code!,
        LocalizationService.Instance.CurrentCulture);

    public event PropertyChangedEventHandler? PropertyChanged;

    private SpokenLanguageOption(string? code, string? staticDisplayName)
    {
        Code = code;
        _staticDisplayName = staticDisplayName;
        if (code is not null)
            LocalizationService.Instance.CultureChanged += OnCultureChanged;
    }

    private void OnCultureChanged(object? sender, CultureInfo culture)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DisplayName)));

    public static SpokenLanguageOption AutoDetect { get; } = new(null, "Auto-detect");

    public static IReadOnlyList<SpokenLanguageOption> All { get; } = BuildAll();

    private static IReadOnlyList<SpokenLanguageOption> BuildAll()
    {
        var hints = NllbLanguageCatalog.IsoCodes
            .Where(code => WhisperAsrLanguageCatalog.IsSupportedHint(code))
            .Select(code => new SpokenLanguageOption(code, staticDisplayName: null))
            .OrderBy(h => h.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new[] { AutoDetect }.Concat(hints).ToList();
    }

    public bool Equals(SpokenLanguageOption? other) =>
        other is not null && string.Equals(Code, other.Code, StringComparison.OrdinalIgnoreCase);

    public override bool Equals(object? obj) => Equals(obj as SpokenLanguageOption);

    public override int GetHashCode() =>
        Code is null ? 0 : StringComparer.OrdinalIgnoreCase.GetHashCode(Code);

    public override string ToString() => DisplayName;
}
