using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using Babel.Player.Models.LanguageSupport;
using Babel.Player.Services;

namespace Babel.Player.Models;

/// <summary>Pipeline output (dub/sub) language choices. Kept in sync with <see cref="NllbLanguageCatalog"/> for local NMT.</summary>
/// <remarks>
/// <see cref="DisplayName"/> is resolved live via <see cref="LanguageDisplayNames.ForIso639"/>
/// and raises <c>PropertyChanged</c> whenever <see cref="LocalizationService.CultureChanged"/>
/// fires so ComboBoxes relabel on runtime UI-culture switch.
/// </remarks>
public sealed class PipelineTargetLanguageOption : INotifyPropertyChanged, IEquatable<PipelineTargetLanguageOption>
{
    public string Code { get; }

    /// <summary>Localized display name, re-resolved on each access so runtime culture switches take effect.</summary>
    public string DisplayName => LanguageDisplayNames.ForIso639(
        Code,
        LocalizationService.Instance.CurrentCulture);

    public event PropertyChangedEventHandler? PropertyChanged;

    public PipelineTargetLanguageOption(string code)
    {
        Code = code ?? throw new ArgumentNullException(nameof(code));
        // Options are process-lifetime singletons held by static readonly fields,
        // so attaching without later detaching does not leak.
        LocalizationService.Instance.CultureChanged += OnCultureChanged;
    }

    private void OnCultureChanged(object? sender, CultureInfo culture)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DisplayName)));

    // Declared before All: C# initializes static fields in order; BuildAll must not see null here.
    private static readonly CultureInfo EnglishSortCulture = CultureInfo.GetCultureInfo("en");

    public static PipelineTargetLanguageOption English { get; } = new("en");

    public static IReadOnlyList<PipelineTargetLanguageOption> All { get; } = BuildAll();

    private static IReadOnlyList<PipelineTargetLanguageOption> BuildAll()
    {
        var items = NllbLanguageCatalog.IsoCodes
            .Select(code => new PipelineTargetLanguageOption(code))
            .ToList();

        // Stable order: English first, then alphabetical by English UI labels (current UI
        // culture would make order drift after a runtime language switch).
        items.Sort(static (a, b) =>
        {
            // Reflexive: equal codes compare as 0 (List.Sort contract).
            if (string.Equals(a.Code, b.Code, StringComparison.OrdinalIgnoreCase))
                return 0;

            if (string.Equals(a.Code, "en", StringComparison.OrdinalIgnoreCase))
                return -1;
            if (string.Equals(b.Code, "en", StringComparison.OrdinalIgnoreCase))
                return 1;

            var byDisplay = string.Compare(
                LanguageDisplayNames.ForIso639(a.Code, EnglishSortCulture),
                LanguageDisplayNames.ForIso639(b.Code, EnglishSortCulture),
                StringComparison.OrdinalIgnoreCase);

            if (byDisplay != 0)
                return byDisplay;

            return string.Compare(a.Code, b.Code, StringComparison.OrdinalIgnoreCase);
        });

        return items;
    }

    public bool Equals(PipelineTargetLanguageOption? other) =>
        other is not null && string.Equals(Code, other.Code, StringComparison.OrdinalIgnoreCase);

    public override bool Equals(object? obj) => Equals(obj as PipelineTargetLanguageOption);

    public override int GetHashCode() =>
        StringComparer.OrdinalIgnoreCase.GetHashCode(Code);

    public override string ToString() => DisplayName;
}
