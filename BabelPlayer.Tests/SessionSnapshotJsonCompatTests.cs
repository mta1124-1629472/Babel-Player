using System;
using System.Text.Json;
using Babel.Player.Models;
using Babel.Player.Services;

namespace BabelPlayer.Tests;

public sealed class SessionSnapshotJsonCompatTests
{
    private static readonly JsonSerializerOptions SerializerOptions = new();

    [Fact]
    public void Deserialize_LegacyInstrumentalField_MigratesToAmbianceAndLeavesInstrumentalNull()
    {
        var now = DateTimeOffset.Parse("2025-01-15T12:00:00Z");
        var legacyPath = "/session/stems/legacy-ambiance.wav";

        var snapshot = SessionSnapshotJsonCompat.Deserialize(
            $$"""
              {
                "SessionId": "{{Guid.NewGuid()}}",
                "Stage": "MediaLoaded",
                "CreatedAtUtc": "{{now:O}}",
                "LastUpdatedAtUtc": "{{now:O}}",
                "StatusMessage": "legacy",
                "InstrumentalAudioPath": "{{legacyPath}}"
              }
              """,
            SerializerOptions);

        Assert.NotNull(snapshot);
        Assert.Equal(legacyPath, snapshot!.AmbianceAudioPath);
        Assert.Null(snapshot.InstrumentalAudioPath);
    }

    [Fact]
    public void Deserialize_ModernInstrumentalField_PreservesExplicitAmbiance()
    {
        var now = DateTimeOffset.Parse("2025-01-15T12:00:00Z");
        var instrumentalPath = "/session/stems/instrumental.wav";
        var ambiancePath = "/session/stems/ambiance.wav";

        var snapshot = SessionSnapshotJsonCompat.Deserialize(
            $$"""
              {
                "SessionId": "{{Guid.NewGuid()}}",
                "Stage": "MediaLoaded",
                "CreatedAtUtc": "{{now:O}}",
                "LastUpdatedAtUtc": "{{now:O}}",
                "StatusMessage": "modern",
                "AmbianceAudioPath": "{{ambiancePath}}",
                "InstrumentalAudioPath": "{{instrumentalPath}}"
              }
              """,
            SerializerOptions);

        Assert.NotNull(snapshot);
        Assert.Equal(ambiancePath, snapshot!.AmbianceAudioPath);
        Assert.Equal(instrumentalPath, snapshot.InstrumentalAudioPath);
    }

    [Fact]
    public void Deserialize_ModernInstrumentalField_PreservesExplicitNullAmbiance()
    {
        var now = DateTimeOffset.Parse("2025-01-15T12:00:00Z");
        var instrumentalPath = "/session/stems/instrumental.wav";

        var snapshot = SessionSnapshotJsonCompat.Deserialize(
            $$"""
              {
                "SessionId": "{{Guid.NewGuid()}}",
                "Stage": "MediaLoaded",
                "CreatedAtUtc": "{{now:O}}",
                "LastUpdatedAtUtc": "{{now:O}}",
                "StatusMessage": "modern",
                "AmbianceAudioPath": null,
                "InstrumentalAudioPath": "{{instrumentalPath}}"
              }
              """,
            SerializerOptions);

        Assert.NotNull(snapshot);
        Assert.Null(snapshot!.AmbianceAudioPath);
        Assert.Equal(instrumentalPath, snapshot.InstrumentalAudioPath);
    }
}
