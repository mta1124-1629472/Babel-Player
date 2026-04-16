namespace Babel.Player.Models;

/// <param name="DubTimelinePath">Fresh composed dub track (no ambiance bed).</param>
/// <param name="MixedWithAmbiancePath">Dub mixed over ambiance when the session has an ambiance stem.</param>
public sealed record DubExportRenderResult(string DubTimelinePath, string? MixedWithAmbiancePath);
