using System.Text.Json.Serialization;

namespace Babel.Player.Models;

[JsonConverter(typeof(SessionWorkflowStageJsonConverter))]
public enum SessionWorkflowStage
{
    Foundation = 0,
    MediaLoaded = 1,
    Transcribed = 2,
    Diarized = 3,
    Translated = 4,
    TtsGenerated = 5,
}
