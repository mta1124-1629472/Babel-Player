using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using BabelPlayer.App;

namespace BabelPlayer.Avalonia;

public sealed class SubtitleOverlayWindow : Window
{
    private readonly Border _subtitleBorder;
    private readonly TextBlock _sourceTextBlock;
    private readonly TextBlock _translationTextBlock;

    public SubtitleOverlayWindow()
    {
        SystemDecorations = SystemDecorations.None;
        ShowInTaskbar = false;
        CanResize = false;
        Topmost = true;
        Background = Brushes.Transparent;
        TransparencyLevelHint = new[] { WindowTransparencyLevel.Transparent };

        Content = new Grid
        {
            IsHitTestVisible = false,
            Children =
            {
                (_subtitleBorder = new Border
                {
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Bottom,
                    Margin = new Thickness(24),
                    Padding = new Thickness(16, 8),
                    Background = new SolidColorBrush(Color.FromArgb(179, 0, 0, 0)),
                    CornerRadius = new CornerRadius(8),
                    IsVisible = false,
                    Child = new StackPanel
                    {
                        Spacing = 8,
                        Children =
                        {
                            (_sourceTextBlock = new TextBlock
                            {
                                IsVisible = false,
                                HorizontalAlignment = HorizontalAlignment.Center,
                                TextAlignment = TextAlignment.Center,
                                Foreground = new SolidColorBrush(Color.Parse("#F1F6FB")),
                                FontSize = 28
                            }),
                            (_translationTextBlock = new TextBlock
                            {
                                IsVisible = false,
                                HorizontalAlignment = HorizontalAlignment.Center,
                                TextAlignment = TextAlignment.Center,
                                Foreground = Brushes.White,
                                FontSize = 30,
                                FontWeight = FontWeight.SemiBold
                            })
                        }
                    }
                })
            }
        };

        ApplyStyle(new ShellSubtitleStyle());
    }

    public void ApplyPresentation(SubtitleOverlayPresentation presentation)
    {
        var showPrimary = presentation.IsVisible && !string.IsNullOrWhiteSpace(presentation.PrimaryText);
        var showSecondary = presentation.IsVisible && !string.IsNullOrWhiteSpace(presentation.SecondaryText);

        _translationTextBlock.Text = showPrimary ? presentation.PrimaryText : string.Empty;
        _translationTextBlock.IsVisible = showPrimary;
        _sourceTextBlock.Text = showSecondary ? presentation.SecondaryText : string.Empty;
        _sourceTextBlock.IsVisible = showSecondary;
        _subtitleBorder.IsVisible = showPrimary || showSecondary;
    }

    public void ApplyStyle(ShellSubtitleStyle style)
    {
        _sourceTextBlock.FontSize = style.SourceFontSize;
        _sourceTextBlock.Foreground = new SolidColorBrush(ParseHexColor(style.SourceForegroundHex, Color.Parse("#F1F6FB")));

        _translationTextBlock.FontSize = style.TranslationFontSize;
        _translationTextBlock.Foreground = new SolidColorBrush(ParseHexColor(style.TranslationForegroundHex, Colors.White));

        _subtitleBorder.Background = new SolidColorBrush(Color.FromArgb(
            (byte)Math.Clamp(Math.Round(style.BackgroundOpacity * 255d), 0, 255),
            18,
            23,
            32));
        _subtitleBorder.Margin = new Thickness(24, 24, 24, style.BottomMargin + 24);

        if (_subtitleBorder.Child is StackPanel stackPanel)
        {
            stackPanel.Spacing = style.DualSpacing;
        }
    }

    private static Color ParseHexColor(string? hex, Color fallback)
    {
        if (string.IsNullOrWhiteSpace(hex))
        {
            return fallback;
        }

        try
        {
            return Color.Parse(hex);
        }
        catch
        {
            return fallback;
        }
    }
}
