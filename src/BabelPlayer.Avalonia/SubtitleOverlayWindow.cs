using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using BabelPlayer.App;

namespace BabelPlayer.Avalonia;

public sealed class SubtitleOverlayWindow : Window
{
    private readonly Border _rootBorder;
    private readonly StackPanel _stackPanel;
    private readonly TextBlock _sourceTextBlock;
    private readonly TextBlock _translationTextBlock;

    public SubtitleOverlayWindow()
    {
        SystemDecorations = SystemDecorations.None;
        ShowInTaskbar = false;
        CanResize = false;
        Topmost = true;
        Background = Brushes.Transparent;
        TransparencyLevelHint = [WindowTransparencyLevel.Transparent];

        _sourceTextBlock = new TextBlock
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            TextAlignment = TextAlignment.Center,
            Foreground = Brushes.White,
            FontWeight = FontWeight.Medium,
            TextWrapping = TextWrapping.Wrap,
            IsVisible = false
        };
        _translationTextBlock = new TextBlock
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            TextAlignment = TextAlignment.Center,
            Foreground = Brushes.White,
            FontWeight = FontWeight.SemiBold,
            TextWrapping = TextWrapping.Wrap,
            IsVisible = false
        };
        _stackPanel = new StackPanel
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Children =
            {
                _sourceTextBlock,
                _translationTextBlock
            }
        };

        _rootBorder = new Border
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(24),
            Padding = new Thickness(18, 12),
            CornerRadius = new CornerRadius(12),
            MaxWidth = 880,
            Child = _stackPanel
        };

        Content = new Grid
        {
            IsHitTestVisible = false,
            Children = { _rootBorder }
        };

        ApplyStyle(new ShellSubtitleStyle());
    }

    public bool HasVisibleContent { get; private set; }

    public void ApplyStyle(ShellSubtitleStyle style)
    {
        _sourceTextBlock.FontSize = style.SourceFontSize;
        _sourceTextBlock.Foreground = new SolidColorBrush(ParseHexColor(style.SourceForegroundHex, Color.FromRgb(241, 246, 251)));

        _translationTextBlock.FontSize = style.TranslationFontSize;
        _translationTextBlock.Foreground = new SolidColorBrush(ParseHexColor(style.TranslationForegroundHex, Colors.White));

        _stackPanel.Spacing = style.DualSpacing;
        _rootBorder.Margin = new Thickness(24, 24, 24, Math.Max(24, style.BottomMargin + 24));
        _rootBorder.Background = new SolidColorBrush(Color.FromArgb(
            (byte)Math.Clamp(Math.Round(style.BackgroundOpacity * 255), 0, 255),
            18,
            23,
            32));
    }

    public void SetPresentation(SubtitleOverlayPresentation presentation)
    {
        var showSource = !string.IsNullOrWhiteSpace(presentation.SecondaryText);
        var showTranslation = !string.IsNullOrWhiteSpace(presentation.PrimaryText);

        _sourceTextBlock.Text = presentation.SecondaryText;
        _sourceTextBlock.IsVisible = showSource;
        _translationTextBlock.Text = presentation.PrimaryText;
        _translationTextBlock.IsVisible = showTranslation;
        HasVisibleContent = presentation.IsVisible && (showSource || showTranslation);
    }

    private static Color ParseHexColor(string? hex, Color fallback)
    {
        if (string.IsNullOrWhiteSpace(hex))
        {
            return fallback;
        }

        var value = hex.Trim();
        if (value.StartsWith("#", StringComparison.Ordinal))
        {
            value = value[1..];
        }

        try
        {
            return value.Length switch
            {
                6 => Color.FromRgb(
                    Convert.ToByte(value[..2], 16),
                    Convert.ToByte(value.Substring(2, 2), 16),
                    Convert.ToByte(value.Substring(4, 2), 16)),
                8 => Color.FromArgb(
                    Convert.ToByte(value[..2], 16),
                    Convert.ToByte(value.Substring(2, 2), 16),
                    Convert.ToByte(value.Substring(4, 2), 16),
                    Convert.ToByte(value.Substring(6, 2), 16)),
                _ => fallback
            };
        }
        catch
        {
            return fallback;
        }
    }
}
