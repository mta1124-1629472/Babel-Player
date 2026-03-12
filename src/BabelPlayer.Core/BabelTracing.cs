using System.Diagnostics;

namespace BabelPlayer.Core;

/// <summary>
/// Provides the shared <see cref="ActivitySource"/> for Babel-Player distributed tracing.
/// </summary>
/// <remarks>
/// Uses the built-in .NET <c>System.Diagnostics</c> tracing API (no additional packages needed).
/// Listeners can be attached via <see cref="ActivityListener"/> — e.g. OpenTelemetry SDK
/// exporters — without changing any instrumented call sites.
///
/// All log lines emitted by <c>BabelLogManager</c> while an <see cref="Activity"/> is active
/// automatically include <c>trace.id</c> and <c>span.id</c> fields, enabling correlation
/// between log files and trace visualizers.
/// </remarks>
public static class BabelTracing
{
    /// <summary>The application-wide <see cref="ActivitySource"/>. Name: <c>"BabelPlayer"</c>.</summary>
    public static readonly ActivitySource Source = new("BabelPlayer", "1.0.0");

    /// <summary>Standard tag keys attached to activities.</summary>
    public static class Tags
    {
        /// <summary>Local file path of the media being processed.</summary>
        public const string MediaPath = "media.path";

        /// <summary>Transcription or translation model key (e.g. <c>"whisper-large-v3"</c>).</summary>
        public const string ModelKey = "model.key";

        /// <summary>Provider identifier string (e.g. <c>"OpenAI"</c>, <c>"LocalLlama"</c>).</summary>
        public const string Provider = "model.provider";

        /// <summary>Number of subtitle cues involved in the operation.</summary>
        public const string CueCount = "subtitle.cue_count";

        /// <summary>BCP-47 language code of the source audio/text.</summary>
        public const string Language = "subtitle.language";

        /// <summary>Playback backend native window handle.</summary>
        public const string BackendHandle = "playback.host_handle";

        /// <summary>Short description of an error outcome; mirrors <c>otel.error</c> convention.</summary>
        public const string ErrorMessage = "error.message";
    }
}
