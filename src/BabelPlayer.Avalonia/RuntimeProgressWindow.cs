using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using BabelPlayer.App;

namespace BabelPlayer.Avalonia;

internal sealed class RuntimeProgressWindow : Window
{
    private readonly TextBlock _stageTextBlock;
    private readonly ProgressBar _progressBar;
    private readonly TextBlock _detailsTextBlock;

    public RuntimeProgressWindow()
    {
        Title = "Preparing Runtime";
        Width = 420;
        Height = 180;
        CanResize = false;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(Color.Parse("#151515"));

        _stageTextBlock = new TextBlock
        {
            Foreground = new SolidColorBrush(Color.Parse("#F2F2F2")),
            FontSize = 16,
            FontWeight = FontWeight.SemiBold,
            Text = "Preparing runtime..."
        };
        _progressBar = new ProgressBar
        {
            Minimum = 0,
            Maximum = 1,
            Value = 0,
            Height = 10
        };
        _detailsTextBlock = new TextBlock
        {
            Foreground = new SolidColorBrush(Color.Parse("#B8B8B8")),
            Text = "Waiting for progress..."
        };

        Content = new StackPanel
        {
            Margin = new Thickness(20),
            Spacing = 14,
            Children =
            {
                _stageTextBlock,
                _progressBar,
                _detailsTextBlock
            }
        };
    }

    public void ApplyProgress(string runtimeLabel, ShellRuntimeInstallProgress progress)
    {
        _stageTextBlock.Text = FormatStage(runtimeLabel, progress.Stage);

        if (progress.ProgressRatio is double ratio)
        {
            _progressBar.IsIndeterminate = false;
            _progressBar.Value = Math.Clamp(ratio, 0, 1);
        }
        else
        {
            _progressBar.IsIndeterminate = !string.Equals(progress.Stage, "ready", StringComparison.OrdinalIgnoreCase);
            _progressBar.Value = string.Equals(progress.Stage, "ready", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
        }

        _detailsTextBlock.Text = FormatDetails(progress);
    }

    private static string FormatStage(string runtimeLabel, string stage)
    {
        return stage switch
        {
            "downloading" => $"Downloading {runtimeLabel}...",
            "extracting" => $"Extracting {runtimeLabel}...",
            "ready" => $"{runtimeLabel} ready.",
            _ => $"Preparing {runtimeLabel}..."
        };
    }

    private static string FormatDetails(ShellRuntimeInstallProgress progress)
    {
        if (progress.TotalBytes is > 0)
        {
            return $"{progress.BytesTransferred / 1_048_576.0:F1} / {progress.TotalBytes.Value / 1_048_576.0:F1} MB";
        }

        if (progress.TotalItems is > 0)
        {
            return $"{progress.ItemsCompleted ?? 0} / {progress.TotalItems.Value} items";
        }

        if (progress.BytesTransferred > 0)
        {
            return $"{progress.BytesTransferred / 1_048_576.0:F1} MB";
        }

        return string.Equals(progress.Stage, "ready", StringComparison.OrdinalIgnoreCase)
            ? "Done"
            : "Preparing...";
    }
}
