using System;
using Babel.Player.Models;

namespace Babel.Player.Services;

/// <summary>
/// Default implementation of <see cref="IMediaTransportManager"/>.
/// Creates <see cref="LibMpvHeadlessTransport"/> and <see cref="LibMpvEmbeddedTransport"/>
/// on demand and disposes only the instances it owns (not injected test doubles).
/// </summary>
public sealed class MediaTransportManager : IMediaTransportManager
{
    private readonly IMediaTransport? _injectedSegmentPlayer;
    private readonly IMediaTransport? _injectedSourcePlayer;
    private readonly VideoPlaybackOptions? _videoOptions;
    private readonly Func<VideoPlaybackOptions>? _videoOptionsFactory;
    private readonly AppLog? _log;
    private IMediaTransport? _segmentPlayer;
    private IMediaTransport? _sourceMediaPlayer;

    public MediaTransportManager(
        IMediaTransport? segmentPlayer = null,
        IMediaTransport? sourcePlayer = null,
        VideoPlaybackOptions? videoOptions = null,
        Func<VideoPlaybackOptions>? videoOptionsFactory = null,
        AppLog? log = null)
    {
        _injectedSegmentPlayer = segmentPlayer;
        _injectedSourcePlayer  = sourcePlayer;
        _videoOptions          = videoOptions;
        _videoOptionsFactory   = videoOptionsFactory;
        _log                   = log;
    }

    public IMediaTransport GetOrCreateSegmentPlayer()
    {
        _segmentPlayer ??= _injectedSegmentPlayer ?? new LibMpvHeadlessTransport(suppressAudio: false);
        return _segmentPlayer;
    }

    public IMediaTransport GetOrCreateSourcePlayer()
    {
        _sourceMediaPlayer ??= _injectedSourcePlayer ?? new LibMpvEmbeddedTransport(
            _videoOptionsFactory?.Invoke() ?? _videoOptions,
            _log);
        return _sourceMediaPlayer;
    }

    public IMediaTransport? SegmentPlayer => _segmentPlayer;

    public IMediaTransport? SourceMediaPlayer => _sourceMediaPlayer;

    public void Dispose()
    {
        // Only dispose instances we created; injected players are owned by the caller.
        if (_injectedSegmentPlayer is null)
            _segmentPlayer?.Dispose();

        if (_injectedSourcePlayer is null)
            _sourceMediaPlayer?.Dispose();
    }
}
