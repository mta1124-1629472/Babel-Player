using System.Runtime.Versioning;
using System.Speech.Recognition;
using BabelPlayer.Core;

namespace BabelPlayer.App;

/// <summary>
/// Houses the actual Windows Speech Recognition logic on a dedicated STA thread.
/// Kept separate so <see cref="WindowsSpeechTranscriber"/> stays thin.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class WindowsSpeechCore
{
    internal static Task<IReadOnlyList<SubtitleCue>> TranscribeOnStaAsync(
        string wavePath,
        string? languageHint,
        CancellationToken cancellationToken)
    {
        var completion = new TaskCompletionSource<IReadOnlyList<SubtitleCue>>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        var thread = new Thread(() =>
        {
            try
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    completion.TrySetCanceled(cancellationToken);
                    return;
                }

                completion.TrySetResult(RunSpeechRecognition(wavePath, languageHint, cancellationToken));
            }
            catch (Exception ex)
            {
                completion.TrySetException(ex);
            }
        });

        thread.TrySetApartmentState(ApartmentState.STA);
        thread.Start();

        using var _ = cancellationToken.Register(() => completion.TrySetCanceled(cancellationToken));
        return completion.Task;
    }

    [SupportedOSPlatform("windows")]
    private static IReadOnlyList<SubtitleCue> RunSpeechRecognition(
        string wavePath,
        string? languageHint,
        CancellationToken cancellationToken)
    {
        var recognizerInfo = SelectRecognizer(languageHint);
        using var recognizer = new SpeechRecognitionEngine(recognizerInfo);
        using var waitHandle = new ManualResetEventSlim(false);

        Exception? failure = null;
        var cues = new List<SubtitleCue>();

        recognizer.LoadGrammar(new DictationGrammar());
        recognizer.SetInputToWaveFile(wavePath);

        recognizer.SpeechRecognized += (_, args) =>
        {
            if (cancellationToken.IsCancellationRequested || args.Result.Audio is null)
                return;
            if (string.IsNullOrWhiteSpace(args.Result.Text) || args.Result.Confidence < 0.30f)
                return;

            var start = args.Result.Audio.AudioPosition;
            var end = start + args.Result.Audio.Duration;
            cues.Add(new SubtitleCue
            {
                Start = start,
                End = end,
                SourceText = args.Result.Text.Trim(),
                SourceLanguage = LanguageDetector.Detect(args.Result.Text)
            });
        };

        recognizer.RecognizeCompleted += (_, args) =>
        {
            failure = args.Error;
            waitHandle.Set();
        };

        recognizer.RecognizeAsync(RecognizeMode.Multiple);

        while (!waitHandle.Wait(TimeSpan.FromMilliseconds(200)))
        {
            if (!cancellationToken.IsCancellationRequested)
                continue;

            recognizer.RecognizeAsyncCancel();
            cancellationToken.ThrowIfCancellationRequested();
        }

        if (failure is not null)
            throw failure;

        return cues;
    }

    [SupportedOSPlatform("windows")]
    private static RecognizerInfo SelectRecognizer(string? languageHint)
    {
        var installed = SpeechRecognitionEngine.InstalledRecognizers().ToList();
        if (installed.Count == 0)
            throw new InvalidOperationException(
                "No Windows speech recognition engine is installed and Whisper local transcription was unavailable.");

        if (!string.IsNullOrWhiteSpace(languageHint))
        {
            var exact = installed.FirstOrDefault(r =>
                string.Equals(r.Culture.Name, languageHint, StringComparison.OrdinalIgnoreCase));
            if (exact is not null) return exact;

            var sameLanguage = installed.FirstOrDefault(r =>
                string.Equals(r.Culture.TwoLetterISOLanguageName, languageHint, StringComparison.OrdinalIgnoreCase));
            if (sameLanguage is not null) return sameLanguage;
        }

        return installed.FirstOrDefault(r =>
                   string.Equals(r.Culture.Name, "en-US", StringComparison.OrdinalIgnoreCase))
               ?? installed[0];
    }
}
