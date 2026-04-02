using System.Text.Json.Serialization;

namespace Babel.Player.Models;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum InferenceRuntime
{
    Local,
    Containerized,
    Cloud,
}
