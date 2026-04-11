using System;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Babel.Player.Models;

/// <summary>
/// Writes stage names for forward-compatible persistence while still accepting
/// legacy numeric values saved before <see cref="SessionWorkflowStage.Diarized"/>
/// existed.
/// </summary>
public sealed class SessionWorkflowStageJsonConverter : JsonConverter<SessionWorkflowStage>
{
    public override SessionWorkflowStage Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        return reader.TokenType switch
        {
            JsonTokenType.String => ParseStageName(reader.GetString()),
            JsonTokenType.Number => ParseLegacyStageNumber(reader.GetInt32()),
            _ => throw new JsonException($"Unsupported token type {reader.TokenType} for {nameof(SessionWorkflowStage)}."),
        };
    }

    public override void Write(Utf8JsonWriter writer, SessionWorkflowStage value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.ToString());

    private static SessionWorkflowStage ParseStageName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new JsonException($"{nameof(SessionWorkflowStage)} cannot be empty.");

        if (Enum.TryParse<SessionWorkflowStage>(value, ignoreCase: true, out var parsed))
            return parsed;

        throw new JsonException($"Unknown {nameof(SessionWorkflowStage)} value '{value}'.");
    }

    private static SessionWorkflowStage ParseLegacyStageNumber(int value) =>
        value switch
        {
            0 => SessionWorkflowStage.Foundation,
            1 => SessionWorkflowStage.MediaLoaded,
            2 => SessionWorkflowStage.Transcribed,
            3 => SessionWorkflowStage.Translated,
            4 => SessionWorkflowStage.TtsGenerated,
            5 => SessionWorkflowStage.TtsGenerated,
            _ => throw new JsonException($"Unknown legacy {nameof(SessionWorkflowStage)} numeric value '{value}'."),
        };
}
