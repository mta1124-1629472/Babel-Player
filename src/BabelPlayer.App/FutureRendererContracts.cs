namespace BabelPlayer.App;

public interface IMediaDecodeBackend
{
    IPlaybackClock Clock { get; }

    Task InitializeAsync(CancellationToken cancellationToken);
}

public interface IVideoPresentationPipeline
{
    Task AttachAsync(CancellationToken cancellationToken);

    Task DetachAsync(CancellationToken cancellationToken);
}

public interface ISubtitleCompositor
{
    Task PresentAsync(SubtitlePresentationModel model, CancellationToken cancellationToken);

    Task ClearAsync(CancellationToken cancellationToken);
}

public interface IAudioPipeline
{
    Task ApplyAugmentationsAsync(AudioAugmentationLane lane, CancellationToken cancellationToken);
}
