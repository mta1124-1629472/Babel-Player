using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace PlayerApp.UI;

internal enum LlamaCppBootstrapChoice
{
    Cancel,
    InstallAutomatically,
    ChooseExisting,
    OpenOfficialDownloadPage
}

internal sealed class LlamaCppBootstrapWindow : Window
{
    public LlamaCppBootstrapWindow()
    {
        Title = "Set Up llama.cpp Runtime";
        Width = 520;
        Height = 300;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.NoResize;
        Background = Brushes.White;

        var root = new Grid
        {
            Margin = new Thickness(18)
        };

        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var intro = new TextBlock
        {
            Text = "HY-MT local translation needs llama-server.exe. You can let PlayerApp install the official runtime automatically, point to an existing copy, or open the official release page.",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 14),
            FontSize = 13
        };
        Grid.SetRow(intro, 0);

        var note = new TextBlock
        {
            Text = "Automatic install downloads the pinned official Windows CPU x64 runtime. HY-MT model files are still fetched later by llama.cpp on first use.",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 18),
            Foreground = new SolidColorBrush(Color.FromRgb(70, 85, 99)),
            FontSize = 12
        };
        Grid.SetRow(note, 1);

        root.Children.Add(intro);
        root.Children.Add(note);

        root.Children.Add(CreateChoiceButton(
            row: 2,
            text: "Install automatically (recommended)",
            click: () => SetChoiceAndClose(LlamaCppBootstrapChoice.InstallAutomatically)));

        root.Children.Add(CreateChoiceButton(
            row: 3,
            text: "Choose existing llama-server",
            click: () => SetChoiceAndClose(LlamaCppBootstrapChoice.ChooseExisting)));

        var footer = new DockPanel
        {
            LastChildFill = false,
            Margin = new Thickness(0, 18, 0, 0)
        };
        Grid.SetRow(footer, 4);

        var officialButton = new Button
        {
            Content = "Open official download page",
            MinWidth = 180,
            Padding = new Thickness(12, 8, 12, 8),
            Margin = new Thickness(0, 0, 8, 0)
        };
        officialButton.Click += (_, _) => SetChoiceAndClose(LlamaCppBootstrapChoice.OpenOfficialDownloadPage);

        var cancelButton = new Button
        {
            Content = "Cancel",
            MinWidth = 90,
            Padding = new Thickness(12, 8, 12, 8),
            IsCancel = true
        };
        cancelButton.Click += (_, _) => SetChoiceAndClose(LlamaCppBootstrapChoice.Cancel);

        footer.Children.Add(officialButton);
        footer.Children.Add(cancelButton);
        root.Children.Add(footer);

        Content = root;
    }

    public LlamaCppBootstrapChoice Choice { get; private set; } = LlamaCppBootstrapChoice.Cancel;

    private Button CreateChoiceButton(int row, string text, Action click)
    {
        var button = new Button
        {
            Content = text,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Padding = new Thickness(14, 10, 14, 10),
            Margin = new Thickness(0, 0, 0, 10),
            FontWeight = FontWeights.SemiBold
        };
        button.Click += (_, _) => click();
        Grid.SetRow(button, row);
        return button;
    }

    private void SetChoiceAndClose(LlamaCppBootstrapChoice choice)
    {
        Choice = choice;
        DialogResult = choice != LlamaCppBootstrapChoice.Cancel;
        Close();
    }
}
