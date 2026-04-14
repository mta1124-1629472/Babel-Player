using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Babel.Player.Models;

namespace Babel.Player.Services;

public sealed record SpeakerReferenceClipQualityMetrics(
    double DurationSeconds,
    double MeanVolumeDb,
    double MaxVolumeDb,
    double NonSilentRatio);

public sealed record SpeakerReferenceConfidenceEvaluation(
    SpeakerReferenceConfidenceTier Tier,
    IReadOnlyList<string> Reasons,
    SpeakerReferenceClipQualityMetrics Metrics);

public static class SpeakerReferenceClipQualityEvaluator
{
    private static readonly Regex DurationRegex = new(
        @"Duration:\s*(?<h>\d+):(?<m>\d+):(?<s>\d+(?:\.\d+)?)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex MeanVolumeRegex = new(
        @"mean_volume:\s*(?<value>-?\d+(?:\.\d+)?)\s*dB",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex MaxVolumeRegex = new(
        @"max_volume:\s*(?<value>-?\d+(?:\.\d+)?)\s*dB",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex SilenceDurationRegex = new(
        @"silence_duration:\s*(?<value>\d+(?:\.\d+)?)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static async Task<SpeakerReferenceConfidenceEvaluation> EvaluateFileAsync(
        string clipPath,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(clipPath) || !File.Exists(clipPath))
        {
            return new SpeakerReferenceConfidenceEvaluation(
                SpeakerReferenceConfidenceTier.Poor,
                ["Reference clip is missing."],
                new SpeakerReferenceClipQualityMetrics(0, -100, 0, 0));
        }

        var ffmpegPath = DependencyLocator.FindFfmpeg();
        if (string.IsNullOrWhiteSpace(ffmpegPath) || !File.Exists(ffmpegPath))
        {
            return new SpeakerReferenceConfidenceEvaluation(
                SpeakerReferenceConfidenceTier.Review,
                ["ffmpeg was unavailable, so clip quality could not be fully verified."],
                new SpeakerReferenceClipQualityMetrics(0, -100, 0, 0));
        }

        var stderr = await RunAnalysisAsync(ffmpegPath, clipPath, cancellationToken).ConfigureAwait(false);
        var metrics = ParseMetrics(stderr);
        return EvaluateMetrics(metrics);
    }

    public static SpeakerReferenceConfidenceEvaluation EvaluateMetrics(SpeakerReferenceClipQualityMetrics metrics)
    {
        var reasons = new List<string>();
        var tier = SpeakerReferenceConfidenceTier.Good;

        if (metrics.DurationSeconds < 3.0)
        {
            tier = SpeakerReferenceConfidenceTier.Poor;
            reasons.Add("Clip is shorter than 3 seconds.");
        }
        else if (metrics.DurationSeconds < 5.0)
        {
            tier = Downgrade(tier, SpeakerReferenceConfidenceTier.Review);
            reasons.Add("Clip is shorter than the recommended 5 seconds.");
        }

        if (metrics.MeanVolumeDb < -38.0)
        {
            tier = SpeakerReferenceConfidenceTier.Poor;
            reasons.Add("Average speech level is very quiet.");
        }
        else if (metrics.MeanVolumeDb < -30.0)
        {
            tier = Downgrade(tier, SpeakerReferenceConfidenceTier.Review);
            reasons.Add("Average speech level is a little quiet.");
        }

        if (metrics.NonSilentRatio < 0.45)
        {
            tier = SpeakerReferenceConfidenceTier.Poor;
            reasons.Add("Clip has too much silence.");
        }
        else if (metrics.NonSilentRatio < 0.65)
        {
            tier = Downgrade(tier, SpeakerReferenceConfidenceTier.Review);
            reasons.Add("Clip contains notable silence.");
        }

        if (metrics.MaxVolumeDb >= -0.3)
        {
            tier = SpeakerReferenceConfidenceTier.Poor;
            reasons.Add("Clip appears clipped near 0 dB peak.");
        }
        else if (metrics.MaxVolumeDb >= -1.0)
        {
            tier = Downgrade(tier, SpeakerReferenceConfidenceTier.Review);
            reasons.Add("Clip peak is close to clipping.");
        }

        if (reasons.Count == 0)
            reasons.Add("Clip quality checks look good.");

        return new SpeakerReferenceConfidenceEvaluation(tier, reasons, metrics);
    }

    private static async Task<string> RunAnalysisAsync(
        string ffmpegPath,
        string clipPath,
        CancellationToken cancellationToken)
    {
        var psi = new ProcessStartInfo
        {
            FileName = ffmpegPath,
            RedirectStandardError = true,
            RedirectStandardOutput = false,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        psi.ArgumentList.Add("-hide_banner");
        psi.ArgumentList.Add("-i");
        psi.ArgumentList.Add(clipPath);
        psi.ArgumentList.Add("-af");
        psi.ArgumentList.Add("volumedetect,silencedetect=noise=-35dB:d=0.20");
        psi.ArgumentList.Add("-f");
        psi.ArgumentList.Add("null");
        psi.ArgumentList.Add("-");

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start ffmpeg for clip quality analysis.");
        using var registration = cancellationToken.Register(() =>
        {
            try
            {
                if (!process.HasExited)
                    process.Kill(entireProcessTree: true);
            }
            catch
            {
                // Best effort.
            }
        });

        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        return await stderrTask.ConfigureAwait(false);
    }

    private static SpeakerReferenceClipQualityMetrics ParseMetrics(string stderr)
    {
        var durationSeconds = ParseDuration(stderr);
        var meanVolume = ParseDouble(stderr, MeanVolumeRegex, defaultValue: -100);
        var maxVolume = ParseDouble(stderr, MaxVolumeRegex, defaultValue: 0);
        var totalSilence = ParseSilenceDuration(stderr);
        var nonSilentRatio = durationSeconds <= 0
            ? 0
            : Math.Clamp((durationSeconds - totalSilence) / durationSeconds, 0, 1);

        return new SpeakerReferenceClipQualityMetrics(
            DurationSeconds: durationSeconds,
            MeanVolumeDb: meanVolume,
            MaxVolumeDb: maxVolume,
            NonSilentRatio: nonSilentRatio);
    }

    private static double ParseDuration(string stderr)
    {
        var match = DurationRegex.Match(stderr);
        if (!match.Success)
            return 0;

        var hours = ParseInvariantDouble(match.Groups["h"].Value);
        var minutes = ParseInvariantDouble(match.Groups["m"].Value);
        var seconds = ParseInvariantDouble(match.Groups["s"].Value);
        return (hours * 3600) + (minutes * 60) + seconds;
    }

    private static double ParseDouble(string source, Regex regex, double defaultValue)
    {
        var match = regex.Match(source);
        return match.Success
            ? ParseInvariantDouble(match.Groups["value"].Value)
            : defaultValue;
    }

    private static double ParseSilenceDuration(string stderr)
    {
        var total = 0d;
        foreach (Match match in SilenceDurationRegex.Matches(stderr))
        {
            if (!match.Success)
                continue;
            total += ParseInvariantDouble(match.Groups["value"].Value);
        }

        return total;
    }

    private static double ParseInvariantDouble(string value) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : 0;

    private static SpeakerReferenceConfidenceTier Downgrade(
        SpeakerReferenceConfidenceTier current,
        SpeakerReferenceConfidenceTier requested) =>
        current > requested ? current : requested;
}
