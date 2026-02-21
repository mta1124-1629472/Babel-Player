namespace PlayerApp.Core
{
	public class AudioCaptureService
	{
		public event Action<byte[]>? OnAudioChunk;  // Add ? to make nullable

		public void Start()
		{
			// Placeholder: implement WASAPI loopback capture using NAudio or CoreAudio APIs.
			// Buffer ~300ms PCM and invoke OnAudioChunk(buffer).
		}

		public void Stop()
		{
			// Stop capture
		}
	}
}
