using System;

namespace Babel.Player.Services;

/// <summary>
/// Headless media transport interface for loading, playing, and controlling media playback.
/// This is intentionally kept minimal for Milestone 2 - only what's needed to prove the transport works.
/// </summary>
public interface IMediaTransport : IDisposable
{
    /// <summary>
    /// Loads a media file from the specified path.
    /// </summary>
    /// <param name="filePath">Path to the media file</param>
    void Load(string filePath);
    
    /// <summary>
    /// Starts media playback.
    /// </summary>
    void Play();
    
    /// <summary>
    /// Pauses media playback.
    /// </summary>
    void Pause();
    
    /// <summary>
    /// Seeks to the specified position in milliseconds.
    /// </summary>
    /// <param name="positionMs">Position in milliseconds</param>
    void Seek(long positionMs);
    
    /// <summary>
    /// Gets the current playback position in milliseconds.
    /// </summary>
    long CurrentTime { get; }
    
    /// <summary>
    /// Gets the total duration of the loaded media in milliseconds.
    /// Returns 0 if no media is loaded or duration is unknown.
    /// </summary>
    long Duration { get; }
    
    /// <summary>
    /// Gets whether the media has ended playback.
    /// </summary>
    bool HasEnded { get; }
    
    /// <summary>
    /// Gets or sets the playback volume (0.0 = silent, 1.0 = 100%).
    /// </summary>
    double Volume { get; set; }

    /// <summary>
    /// Gets or sets the playback rate (1.0 = normal speed, 2.0 = double speed, etc.)
    /// </summary>
    double PlaybackRate { get; set; }

    /// <summary>
    /// Loads an external subtitle file. No-op if the transport has no display.
    /// </summary>
    void LoadSubtitleTrack(string srtPath);

    /// <summary>
    /// Removes all external subtitle tracks. No-op if the transport has no display.
    /// </summary>
    void RemoveAllSubtitleTracks();

    /// <summary>
    /// Gets or sets whether the active subtitle track is visible.
    /// </summary>
    bool SubtitlesVisible { get; set; }

    /// <summary>
    /// Event raised when media playback ends.
    /// </summary>
    event EventHandler? Ended;
    
    /// <summary>
    /// Event raised when media transport encounters an error.
    /// </summary>
    event EventHandler<Exception>? ErrorOccurred;
}
