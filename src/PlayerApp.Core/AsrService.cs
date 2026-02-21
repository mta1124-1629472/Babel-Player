namespace PlayerApp.Core
{
	public class TranscriptChunk
	{
		public string Text { get; set; } = string.Empty;
		public double StartTimeSec { get; set; }
		public double EndTimeSec { get; set; }
		public bool IsFinal { get; set; }
	}

	public class AsrService
	{
		public event Action<TranscriptChunk>? OnInterim;  // Add ?
		public event Action<TranscriptChunk>? OnFinal;    // Add ?

		public void Initialize(string modelPath)
		{
			// Load ONNX ASR model (quantized) and prepare streaming buffers.
		}

		public void FeedAudio(byte[] pcm)
		{
			// Push PCM to ASR streaming inference.
			// Raise OnInterim and OnFinal events as appropriate.
		}
	}
}
