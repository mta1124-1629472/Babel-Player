using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Babel.Player.Services.Registries;

namespace Babel.Player.Services;

/// <summary>
/// Pass-through implementation: forwards to the supplied provider without altering behavior.
/// </summary>
public sealed class DefaultInferenceExecutionEngine : IInferenceExecutionEngine
{
    public static DefaultInferenceExecutionEngine Instance { get; } = new();

    /// <summary>
        /// Prevents external instantiation to enforce the class's singleton usage.
        /// </summary>
    private DefaultInferenceExecutionEngine() { }

    /// <summary>
        /// Forwards a transcription request to the specified transcription provider.
        /// </summary>
        /// <param name="provider">The transcription provider that will execute the request.</param>
        /// <param name="request">The transcription request containing the audio and transcription options.</param>
        /// <returns>A <see cref="TranscriptionResult"/> produced by the provider.</returns>
        public Task<TranscriptionResult> TranscribeAsync(
        ITranscriptionProvider provider,
        TranscriptionRequest request,
        CancellationToken cancellationToken = default) =>
        provider.TranscribeAsync(request, cancellationToken);

    /// <summary>
        /// Initiates a streaming transcription session with the specified streaming transcription provider and writes transcript items to the provided channel writer.
        /// </summary>
        /// <param name="provider">The streaming transcription provider that will perform the transcription.</param>
        /// <param name="request">The transcription request containing audio and configuration for the session.</param>
        /// <param name="writer">A channel writer that will receive <see cref="TranscriptChannelItem"/> messages produced during the streaming transcription.</param>
        /// <param name="cancellationToken">A token that can be used to cancel the streaming transcription operation.</param>
        /// <returns>A <see cref="TranscriptionResult"/> describing the outcome of the transcription session.</returns>
        public Task<TranscriptionResult> TranscribeStreamingAsync(
        IStreamingTranscriptionProvider provider,
        TranscriptionRequest request,
        ChannelWriter<TranscriptChannelItem> writer,
        CancellationToken cancellationToken = default) =>
        provider.TranscribeStreamingAsync(request, writer, cancellationToken);

    /// <summary>
        /// Executes the given translation request using the specified translation provider.
        /// </summary>
        /// <param name="provider">The translation provider that will perform the request.</param>
        /// <param name="request">The translation request containing input text and translation options.</param>
        /// <param name="cancellationToken">A token to cancel the translation operation.</param>
        /// <returns>A <see cref="TranslationResult"/> containing the translated output and associated metadata.</returns>
        public Task<TranslationResult> TranslateAsync(
        ITranslationProvider provider,
        TranslationRequest request,
        CancellationToken cancellationToken = default) =>
        provider.TranslateAsync(request, cancellationToken);

    /// <summary>
        /// Translates a single text/audio segment using the specified translation provider.
        /// </summary>
        /// <param name="provider">The translation provider to execute the single-segment translation.</param>
        /// <param name="request">The single-segment translation request containing the input segment and options.</param>
        /// <param name="cancellationToken">Cancellation token to cancel the translation operation.</param>
        /// <returns>A <see cref="TranslationResult"/> containing the translated segment and related metadata.</returns>
        /// <remarks>
        /// Entry state: caller must supply a ready-to-use <paramref name="provider"/> capable of performing single-segment translation.
        /// Exit state: returns the provider's translation result; no engine state is mutated and no session is persisted by this method.
        /// Cancellation: the operation observes <paramref name="cancellationToken"/> and may terminate early if cancellation is requested.
        /// </remarks>
        public Task<TranslationResult> TranslateSingleSegmentAsync(
        ITranslationProvider provider,
        SingleSegmentTranslationRequest request,
        CancellationToken cancellationToken = default) =>
        provider.TranslateSingleSegmentAsync(request, cancellationToken);

    /// <summary>
        /// Generates speech audio for a single text segment using the supplied TTS provider.
        /// </summary>
        /// <param name="request">Parameters for the segment TTS generation (text, voice, format, and other options).</param>
        /// <param name="cancellationToken">A token to cancel the TTS generation operation.</param>
        /// <returns>A <see cref="TtsResult"/> containing the generated audio bytes and associated metadata.</returns>
        public Task<TtsResult> GenerateSegmentTtsAsync(
        ITtsProvider provider,
        SingleSegmentTtsRequest request,
        CancellationToken cancellationToken = default) =>
        provider.GenerateSegmentTtsAsync(request, cancellationToken);

    /// <summary>
        /// Performs speaker diarization for the specified request using the given provider.
        /// </summary>
        /// <param name="provider">The diarization provider that will execute the request.</param>
        /// <param name="request">Parameters and audio data for the diarization operation.</param>
        /// <param name="cancellationToken">Token to cancel the operation; if canceled, the task may complete early.</param>
        /// <returns>
        /// A <see cref="DiarizationResult"/> containing identified speaker segments and related metadata.
        /// </returns>
        public Task<DiarizationResult> DiarizeAsync(
        IDiarizationProvider provider,
        DiarizationRequest request,
        CancellationToken cancellationToken = default) =>
        provider.DiarizeAsync(request, cancellationToken);

    public Task<VocalSeparationResult> SeparateVocalsAsync(
        IVocalSeparationProvider provider,
        VocalSeparationRequest request,
        CancellationToken cancellationToken = default) =>
        provider.SeparateVocalsAsync(request, cancellationToken);
}
