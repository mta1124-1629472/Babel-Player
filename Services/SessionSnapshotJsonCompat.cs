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
    /// <summary>
    /// Deserializes JSON into a <see cref="WorkflowSessionSnapshot"/>, migrating a legacy `InstrumentalAudioPath` field to `AmbianceAudioPath` when present.
    /// </summary>
    /// <param name="json">The JSON text to deserialize. Returns <c>null</c> if this is null, empty, or whitespace.</param>
    /// <param name="options">Json serializer options to use for deserialization.</param>
    /// <returns>The deserialized <see cref="WorkflowSessionSnapshot"/>, or <c>null</c> when <paramref name="json"/> is null, empty, or whitespace.</returns>
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

    /// <summary>
    /// Migrates a legacy "InstrumentalAudioPath" property into "AmbianceAudioPath" on the provided JSON object.
    /// </summary>
    /// <param name="obj">The JSON object to migrate; modified in place.</param>
    /// <remarks>
    /// If the legacy property exists and "AmbianceAudioPath" is missing or explicitly null, the legacy value (or null) is copied into "AmbianceAudioPath" as a deep clone. The legacy "InstrumentalAudioPath" property is then removed so it will not populate any modern property during subsequent deserialization.
    /// <summary>
    /// Migrates a legacy "InstrumentalAudioPath" property into "AmbianceAudioPath" on the provided JSON object.
    /// </summary>
    /// <param name="obj">The JSON object to update; modifications are applied in place.</param>
    /// <remarks>
    /// If the object contains the legacy "InstrumentalAudioPath" property and the "AmbianceAudioPath" property is missing or explicitly null,
    /// this method sets "AmbianceAudioPath" to a deep clone of the legacy value (or null if the legacy value is null) and then removes "InstrumentalAudioPath".
    /// </remarks>
    private static void MigrateLegacyFields(JsonObject obj)
    {
        if (obj.TryGetPropertyValue(LegacyInstrumentalField, out var legacyNode))
        {
            if (!obj.TryGetPropertyValue(AmbianceField, out var ambianceNode) || ambianceNode is null)
            {
                // AmbianceAudioPath missing or explicitly null: adopt the legacy value.
                obj[AmbianceField] = legacyNode?.DeepClone();
            }

            // Older snapshots used InstrumentalAudioPath for the ambiance stem.
            // Strip the legacy field before deserialization so it does not also
            // populate the modern InstrumentalAudioPath slot.
            obj.Remove(LegacyInstrumentalField);
        }
    }
}
