using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PlayerApp.Core;
using Windows.Graphics;
using Windows.Media.Playback;

namespace PlayerApp.UI
{
	public sealed partial class MainWindow : Window
	{
		private AudioCaptureService _audioCapture;
		private AsrService _asr;
		private MtService _mt;
		private SubtitleManager _subtitle;

		public MainWindow()
		{
			this.InitializeComponent();

			// Set window size
			this.AppWindow.ResizeClient(new SizeInt32(1280, 720));

			_audioCapture = new AudioCaptureService();
			_asr = new AsrService();
			_mt = new MtService();
			_subtitle = new SubtitleManager(SubtitleOverlay);

			_audioCapture.OnAudioChunk += pcm => _asr.FeedAudio(pcm);
			_asr.OnInterim += chunk => DispatcherQueue.TryEnqueue(() => _subtitle.ShowInterim(chunk));
			_asr.OnFinal += async chunk =>
			{
				var lang = LanguageDetector.Detect(chunk.Text);
				var modelPath = await ModelManager.EnsureModelForLanguageAsync(lang);
				_mt.LoadModel(modelPath);
				var translated = _mt.Translate(chunk.Text);
				DispatcherQueue.TryEnqueue(() => _subtitle.CommitFinal(chunk, translated));
			};

			// Start capture when player starts playing
			Player.MediaPlayer.PlaybackSession.PlaybackStateChanged += (s, e) =>
			{
				if (Player.MediaPlayer.PlaybackSession.PlaybackState == MediaPlaybackState.Playing)
				{
					_audioCapture.Start();
				}
				else
				{
					_audioCapture.Stop();
				}
			};

			// Show hardware status
			HardwareStatus.Text = HardwareDetector.GetSummary();
		}

		private void ModelManagerButton_Click(object sender, RoutedEventArgs e)
		{
			// Minimal placeholder: open model manager UI (not implemented in MVP)
			var dlg = new ContentDialog
			{
				Title = "Model Manager",
				Content = "Model manager UI will allow downloading and selecting models.",
				CloseButtonText = "Close"
			};
			_ = dlg.ShowAsync();
		}
	}
}