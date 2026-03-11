using BabelPlayer.App;
using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace BabelPlayer.WinUI;

internal sealed class LlamaCppBootstrapDialog : ContentDialog
{
    public LlamaCppBootstrapDialog(XamlRoot xamlRoot, string title, string message)
    {
        ArgumentNullException.ThrowIfNull(xamlRoot);

        XamlRoot = xamlRoot;
        Title = string.IsNullOrWhiteSpace(title) ? "Set Up llama.cpp Runtime" : title;
        CloseButtonText = "Cancel";
        DefaultButton = ContentDialogButton.Close;

        Content = BuildContent(message);
    }

    public LlamaCppBootstrapChoice Choice { get; private set; } = LlamaCppBootstrapChoice.Cancel;

    private UIElement BuildContent(string message)
    {
        var root = new Grid
        {
            Width = 560
        };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var intro = new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(message)
                ? "HY-MT local translation needs llama-server.exe. You can let Babel Player install the official runtime automatically, point to an existing copy, or open the official release page."
                : message,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 14),
            FontSize = 13
        };
        Grid.SetRow(intro, 0);
        root.Children.Add(intro);

        var note = new TextBlock
        {
            Text = "Automatic install downloads the pinned official Windows CPU x64 runtime. HY-MT model files are still fetched later by llama.cpp on first use.",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 18),
            Foreground = new SolidColorBrush(ColorHelper.FromArgb(255, 90, 103, 122)),
            FontSize = 12
        };
        Grid.SetRow(note, 1);
        root.Children.Add(note);

        root.Children.Add(CreateChoiceButton(
            row: 2,
            text: "Install automatically (recommended)",
            choice: LlamaCppBootstrapChoice.InstallAutomatically));

        root.Children.Add(CreateChoiceButton(
            row: 3,
            text: "Choose existing llama-server",
            choice: LlamaCppBootstrapChoice.ChooseExisting));

        var footer = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0, 18, 0, 0)
        };
        Grid.SetRow(footer, 4);

        var officialButton = new Button
        {
            Content = "Open official download page",
            MinWidth = 220,
            Padding = new Thickness(14, 9, 14, 9)
        };
        officialButton.Click += (_, _) => SetChoiceAndClose(LlamaCppBootstrapChoice.OpenOfficialDownloadPage);
        footer.Children.Add(officialButton);

        root.Children.Add(footer);
        return root;
    }

    private Button CreateChoiceButton(int row, string text, LlamaCppBootstrapChoice choice)
    {
        var button = new Button
        {
            Content = text,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            Padding = new Thickness(14, 11, 14, 11),
            Margin = new Thickness(0, 0, 0, 10),
            FontWeight = FontWeights.SemiBold
        };
        button.Click += (_, _) => SetChoiceAndClose(choice);
        Grid.SetRow(button, row);
        return button;
    }

    private void SetChoiceAndClose(LlamaCppBootstrapChoice choice)
    {
        Choice = choice;
        Hide();
    }
}
