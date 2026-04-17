using Babel.Player.Models;

namespace Babel.Player.Services;

public static class DubTimingDefaults
{
    public const double StretchMinTempoRatio = 0.75;
    public const double StretchMaxTempoRatio = 1.75;

    public static SegmentTimingMode NormalizeRenderTimingMode(SegmentTimingMode mode) =>
        mode == SegmentTimingMode.Pause ? SegmentTimingMode.Off : mode;
}
