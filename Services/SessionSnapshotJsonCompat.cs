using System.Text.Json;
using System.Text.Json.Nodes;
using Babel.Player.Models;

namespace Babel.Player.Services;

/// <summary>
/// Backwards-compatibility shim for reading <see cref="WorkflowSessionSnapshot"/> JSON written
/// by older versions of the app. Historic snapshots may contain an <c>InstrumentalAudioPath</c>
/// field that was renamed to <c>AmbianceAudioPath</c>. This helper migrates the legacy field
/// onto the canonical one before deserialization so existing on-disk sessions continue to load.
/// </summary>
internal static class SessionSnapshotJsonCompat
{
    private const string LegacyInstrumentalField = "InstrumentalAudioPath";
    private const string AmbianceField = "AmbianceAudioPath";

    /// <summary>
    /// Deserializes a <see cref="WorkflowSessionSnapshot"/> while migrating the legacy
    /// <c>InstrumentalAudioPath</c> field to <c>AmbianceAudioPath</c> when needed.
    /// Returns null if <paramref name="json"/> is empty, whitespace, or not a JSON object.
    /// </summary>
    public static WorkflowSessionSnapshot? Deserialize(string json, JsonSerializerOptions options)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;

        var node = JsonNode.Parse(json);
        if (node is not JsonObject obj)
        {
            // Preserve existing behavior for primitive/array JSON: let the main deserializer
            // raise a JsonException if the shape is unusable.
            return JsonSerializer.Deserialize<WorkflowSessionSnapshot>(json, options);
        }

        MigrateLegacyFields(obj);

        return obj.Deserialize<WorkflowSessionSnapshot>(options);
    }

    private static void MigrateLegacyFields(JsonObject obj)
    {
        if (obj.TryGetPropertyValue(LegacyInstrumentalField, out var legacyNode))
        {
            if (!obj.TryGetPropertyValue(AmbianceField, out var ambianceNode) || ambianceNode is null)
            {
                // AmbianceAudioPath missing or explicitly null: adopt the legacy value.
                obj[AmbianceField] = legacyNode?.DeepClone();
            }
            obj.Remove(LegacyInstrumentalField);
        }
    }
}
