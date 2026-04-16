using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Babel.Player.Services.Registries;

namespace Babel.Player.Services;

/// <summary>
/// Single seam for invoking inference providers from the session workflow. Production uses
/// <see cref="DefaultInferenceExecutionEngine"/> (direct delegation); tests may substitute fakes.
/// </summary>
public interface IInferenceExecutionEngine
{
    /// <summary>
        /// Invokes a non-streaming transcription operation using the specified provider and request.
        /// </summary>
        /// <remarks>
        /// Entry state: caller must supply a configured <paramref name="provider"/> and a valid <paramref name="request"/>; the engine expects the provider to be ready to accept work (any readiness or lease gating must be enforced by the caller).  
        /// Exit state: completes with a transcription result and does not modify caller-managed session state.  
        /// Persistence: this operation does not persist session state.  
        /// Cancellation: the operation observes <paramref name="cancellationToken"/> and will complete in a canceled state if cancellation is requested.
        /// </remarks>
        /// <param name="provider">The transcription provider that will execute the transcription request.</param>
        /// <param name="request">The transcription request describing the audio and options to transcribe.</param>
        /// <param name="cancellationToken">Token to observe for cancellation.</param>
        /// <returns>A <see cref="TranscriptionResult"/> containing the transcription outcome.</returns>
        Task<TranscriptionResult> TranscribeAsync(
        ITranscriptionProvider provider,
        TranscriptionRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
        /// Performs a streaming transcription operation using the specified provider and writes intermediate and final items to the supplied channel.
        /// </summary>
        /// <remarks>
        /// Entry state: caller must have an active session and a provider instance that is ready to accept requests; the <paramref name="writer"/> must be open for writing.
        /// Exit state on success: the method completes after the provider finishes transcription and the final result is produced; the <paramref name="writer"/> may contain zero or more streamed <see cref="TranscriptChannelItem"/> entries plus any finalization item the provider produces.
        /// Persistence: this API does not itself persist session state; callers are responsible for any persistence required by their workflow.
        /// Cancellation: honoring <paramref name="cancellationToken"/> causes the operation to attempt cooperative cancellation; partial streamed items already written to <paramref name="writer"/> remain visible to readers.
        /// Guard failures: if the provider is not ready or the <paramref name="writer"/> is closed, the method is expected to complete with an appropriate failure result in the returned <see cref="TranscriptionResult"/> rather than performing writes.
        /// </remarks>
        /// <param name="provider">The streaming transcription provider that will perform the transcription.</param>
        /// <param name="request">Parameters that describe the transcription request (audio source, locale, options, etc.).</param>
        /// <param name="writer">Channel writer to receive streaming transcription items produced during the operation.</param>
        /// <param name="cancellationToken">Token to request cooperative cancellation of the transcription operation.</param>
        /// <returns>A <see cref="TranscriptionResult"/> representing the final transcription outcome and associated metadata.</returns>
        Task<TranscriptionResult> TranscribeStreamingAsync(
        IStreamingTranscriptionProvider provider,
        TranscriptionRequest request,
        ChannelWriter<TranscriptChannelItem> writer,
        CancellationToken cancellationToken = default);

    /// <summary>
        /// Invokes a non-streaming translation operation using the specified translation provider.
        /// </summary>
        /// <remarks>
        /// Entry state: expects the session to be ready to perform outbound inference with no conflicting active request.
        /// Exit state: completes with the session left in its prior ready state on success; the method does not persist session state.
        /// Cancellation: honors <paramref name="cancellationToken"/> and will attempt to cancel the provider operation promptly when signaled.
        /// </remarks>
        /// <param name="provider">The translation provider implementation to execute the request.</param>
        /// <param name="request">The translation request describing source text, target language, and options.</param>
        /// <param name="cancellationToken">Token to observe for cancellation.</param>
        /// <returns>A <see cref="TranslationResult"/> containing the translated text and associated metadata.</returns>
        Task<TranslationResult> TranslateAsync(
        ITranslationProvider provider,
        TranslationRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
        /// Performs translation for a single segment using the specified translation provider and request.
        /// </summary>
        /// <param name="provider">The translation provider to execute the single-segment translation.</param>
        /// <param name="request">The single-segment translation request containing text, language targets, and any provider-specific options.</param>
        /// <param name="cancellationToken">Token to observe for cancellation of the translation operation.</param>
        /// <returns>A <see cref="TranslationResult"/> containing the translated segment and metadata about the operation.</returns>
        /// <remarks>
        /// Entry state: caller must have a prepared segment and any necessary provider credentials available.
        /// Exit state on success: returns with a completed translation result for the requested segment; no session state is persisted by this call.
        /// Cancellation: operation honors <paramref name="cancellationToken"/> and will attempt to stop work promptly if cancellation is requested.
        /// Failure: provider-specific errors are surfaced via the returned <see cref="TranslationResult"/> or by exceptions thrown by the provider implementation.
        /// </remarks>
        Task<TranslationResult> TranslateSingleSegmentAsync(
        ITranslationProvider provider,
        SingleSegmentTranslationRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
        /// Generates synthesized audio for a single segment using the given TTS provider.
        /// </summary>
        /// <remarks>
        /// Expected entry state: the segment's text and required metadata are finalized and ready for synthesis.
        /// Success exit state: a <see cref="TtsResult"/> containing the generated audio and related metadata is produced; the method itself does not persist session state.
        /// Cancellation: the operation observes <paramref name="cancellationToken"/> and will abort promptly when cancelled.
        /// Guard conditions: the caller must provide a non-null <paramref name="provider"/> and a fully populated <paramref name="request"/> describing the segment to synthesize; behavior for invalid inputs is provider-dependent.
        /// </remarks>
        /// <param name="provider">The TTS provider implementation to perform synthesis.</param>
        /// <param name="request">Details of the single segment to synthesize (text, voice/configuration, and any segment identifiers).</param>
        /// <param name="cancellationToken">Token to cancel the synthesis operation.</param>
        /// <returns>A <see cref="TtsResult"/> containing the synthesized audio and any provider-returned metadata.</returns>
        Task<TtsResult> GenerateSegmentTtsAsync(
        ITtsProvider provider,
        SingleSegmentTtsRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
        /// Invokes diarization using the specified provider for the given request.
        /// </summary>
        /// <remarks>
        /// Entry state: execution pipeline must be ready to perform inference/diarization for the current session.
        /// Success state: returns the diarization outcome without altering the session pipeline state or persisting session changes.
        /// Cancellation: honors <paramref name="cancellationToken"/> and will attempt to stop the provider operation when cancellation is requested.
        /// </remarks>
        /// <param name="provider">The diarization provider to execute the request with.</param>
        /// <param name="request">Parameters and input for the diarization operation.</param>
        /// <param name="cancellationToken">Token to observe for cancellation.</param>
        /// <returns>The diarization result containing speaker segments and related metadata.</returns>
        Task<DiarizationResult> DiarizeAsync(
        IDiarizationProvider provider,
        DiarizationRequest request,
        CancellationToken cancellationToken = default);

    Task<VocalSeparationResult> SeparateVocalsAsync(
        IVocalSeparationProvider provider,
        VocalSeparationRequest request,
        CancellationToken cancellationToken = default);
}
