using System;
using Xunit;
using Babel.Player.Models;

namespace BabelPlayer.Tests;

public sealed class ComputeDowngradeEventTests
{
    [Fact]
    public void Constructor_WithoutDowngrade_SetsIsDowngradedFalse()
    {
        // Arrange & Act
        var @event = new ComputeDowngradeEvent(
            "float16",
            "float16",
            "No downgrade",
            "transcription");

        // Assert
        Assert.False(@event.IsDowngraded);
        Assert.Equal("float16", @event.RequestedComputeType);
        Assert.Equal("float16", @event.EffectiveComputeType);
    }

    [Fact]
    public void Constructor_WithDowngrade_SetsIsDowngradedTrue()
    {
        // Arrange & Act
        var @event = new ComputeDowngradeEvent(
            "float8",
            "float16",
            "FP8 not supported by Whisper on this device",
            "transcription");

        // Assert
        Assert.True(@event.IsDowngraded);
        Assert.Equal("float8", @event.RequestedComputeType);
        Assert.Equal("float16", @event.EffectiveComputeType);
    }

    [Fact]
    public void Summary_WithDowngrade_IncludesReason()
    {
        // Arrange & Act
        var @event = new ComputeDowngradeEvent(
            "float8",
            "float16",
            "FP8 not supported",
            "transcription");

        // Assert
        var summary = @event.Summary;
        Assert.Contains("float8", summary);
        Assert.Contains("float16", summary);
        Assert.Contains("FP8 not supported", summary);
    }

    [Fact]
    public void Summary_WithoutDowngrade_DoesNotIncludeReason()
    {
        // Arrange & Act
        var @event = new ComputeDowngradeEvent(
            "float16",
            "float16",
            "No downgrade",
            "translation");

        // Assert
        var summary = @event.Summary;
        Assert.Contains("float16", summary);
        Assert.DoesNotContain("No downgrade", summary);
    }

    [Fact]
    public void Constructor_GeneratesAutomaticTimestamp()
    {
        // Arrange: record time before creation
        var beforeCreation = DateTime.UtcNow;
        
        // Act
        var @event = new ComputeDowngradeEvent(
            "float8",
            "float16",
            "Test reason",
            "tts");

        var afterCreation = DateTime.UtcNow;

        // Assert
        Assert.NotNull(@event.Timestamp);
        Assert.NotEmpty(@event.Timestamp);
        // Verify it's a valid ISO 8601 timestamp
        var parsed = DateTime.Parse(@event.Timestamp);
        // Convert parsed to UTC for comparison
        var parsedUtc = parsed.Kind == DateTimeKind.Utc ? parsed : parsed.ToUniversalTime();
        Assert.True(parsedUtc >= beforeCreation && parsedUtc <= afterCreation.AddSeconds(1), 
            $"Timestamp {parsedUtc:O} should be close to now. Before: {beforeCreation:O}, After: {afterCreation:O}");
    }

    [Theory]
    [InlineData("transcription")]
    [InlineData("translation")]
    [InlineData("tts")]
    public void Constructor_AcceptsValidStages(string stage)
    {
        // Arrange & Act
        var @event = new ComputeDowngradeEvent(
            "float16",
            "float16",
            "Test",
            stage);

        // Assert
        Assert.Equal(stage, @event.Stage);
    }
}
