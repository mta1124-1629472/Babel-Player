using System;

namespace Babel.Player.Services;

/// <summary>
/// Manages the lifecycle of the two media transport instances used by the coordinator:
/// a headless segment player (for TTS audio playback) and an embedded source player
/// (for GPU-accelerated video rendering). Neither transport is a workflow state owner —
/// they are infrastructure, not session state.
/// </summary>
public interface IMediaTransportManager : IDisposable
{
    /// <summary>
    /// Returns the headless segment player, creating it on first access.
    /// Used for TTS audio playback of individual dubbed segments.
    /// </summary>
    IMediaTransport GetOrCreateSegmentPlayer();

    /// <summary>
    /// Returns the embedded source media player, creating it on first access.
    /// Used for GPU-rendered in-context video playback.
    /// </summary>
    IMediaTransport GetOrCreateSourcePlayer();

    /// <summary>
    /// The embedded source player if it has been created; null otherwise.
    /// Exposed so callers can attach it to a window handle or subscribe to events.
    /// </summary>
    IMediaTransport? SourceMediaPlayer { get; }
}
