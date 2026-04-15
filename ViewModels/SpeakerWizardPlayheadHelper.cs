using System;

namespace Babel.Player.ViewModels;

/// <summary>
/// Computes clip start position and validates against media duration for speaker reference extraction.
/// </summary>
public static class SpeakerWizardPlayheadHelper
{
    /// <summary>
    /// Returns start time (seconds) for a clip centered at <paramref name="centerSec"/> with window length <paramref name="windowSec"/> (clamped 3–15),
    /// and the media duration used for clamping the start to the end of the file.
    /// </summary>
    public static (double StartSec, double MediaDurationSec) ComputeClipStartAndBounds(
        double centerSec,
        double windowSec,
        double mediaDurationSec)
    {
        var duration = Math.Clamp(windowSec, 3.0, 15.0);
        var half = duration / 2.0;
        var start = Math.Max(0, centerSec - half);
        if (mediaDurationSec > duration + 0.05)
        {
            var maxStart = Math.Max(0, mediaDurationSec - duration);
            if (start > maxStart)
                start = maxStart;
        }

        return (start, mediaDurationSec);
    }
}
