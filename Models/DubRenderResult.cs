namespace Babel.Player.Models;

/// <param name="DubTimelinePath">Fresh composed dub track without ambiance.</param>
/// <param name="MixedWithAmbiancePath">Dub mixed over ambiance when ambiance was expected and mixed successfully.</param>
/// <param name="AmbianceExpected">True when the current session has a usable ambiance stem that must be recombined.</param>
/// <param name="AmbianceMixed">True when the ambiance recombine completed and the mixed file exists.</param>
public sealed record DubRenderResult(
    string DubTimelinePath,
    string? MixedWithAmbiancePath,
    bool AmbianceExpected,
    bool AmbianceMixed);
